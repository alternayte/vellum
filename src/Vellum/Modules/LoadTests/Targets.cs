using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Settings;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using Decision = Vellum.Shared.CommandResult<System.Collections.Generic.IReadOnlyList<object>>;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.LoadTests;

/// <summary>
/// The class of the system behind a target. A target always states its class: there is no
/// default, so a target is never production because nobody chose.
/// </summary>
public static class TargetEnvironment
{
    public const string Development = "development";
    public const string Test = "test";
    public const string Staging = "staging";
    public const string Production = "production";

    public static readonly string[] All = [Development, Test, Staging, Production];

    /// <summary>The class as a row stores it. Every class a target holds is one of <see cref="All"/>.</summary>
    public static StoredTargetEnvironment Stored(string environment) =>
        EnumText.TryParse<StoredTargetEnvironment>(environment, out var stored)
            ? stored
            : throw new ArgumentOutOfRangeException(nameof(environment), environment, "Not a target class");
}

/// <summary>A person with the Owner role allowed load against <paramref name="BaseUrl"/> as a system of class <paramref name="Environment"/>.</summary>
public sealed record TargetAuthorisation(string BaseUrl, string Environment, string By, DateTimeOffset At);

/// <summary>
/// What a person allowed on a production-class target against the default. Without it, a
/// script for a production-class target leaves out every non-idempotent operation, and a
/// scenario with faults gets no script.
/// </summary>
public sealed record TargetOverride(bool AllowNonIdempotent, bool AllowFailureInjection, string Reason, string By, DateTimeOffset At);

/// <summary>
/// The changes that a decision about a target makes. They are not stored: the row of the
/// target holds the state after them, and its version counts them. A change that gives or
/// takes a permission is written to the audit log in the same transaction.
/// </summary>
public static class TargetChange
{
    public sealed record TargetAdded(Guid ProjectId, string Name, string BaseUrl, string Environment, string By, DateTimeOffset At);

    public sealed record TargetChanged(string Name, string BaseUrl, string Environment, string By, DateTimeOffset At);

    public sealed record TargetAuthorised(string BaseUrl, string Environment, string By, DateTimeOffset At);

    /// <param name="Cause"><c>revoked</c>, <c>base-url-changed</c>, <c>environment-changed</c> or <c>target-removed</c>.</param>
    public sealed record AuthorisationVoided(string Cause, string By, DateTimeOffset At);

    public sealed record OverrideSet(bool AllowNonIdempotent, bool AllowFailureInjection, string Reason, string By, DateTimeOffset At);

    /// <param name="Cause"><c>cleared</c>, <c>base-url-changed</c>, <c>environment-changed</c> or <c>target-removed</c>.</param>
    public sealed record OverrideCleared(string Cause, string By, DateTimeOffset At);

    public sealed record TargetRemoved(string By, DateTimeOffset At);
}

/// <summary>One named base URL of a deployed system that a project may send k6 load to.</summary>
public sealed record TargetState(
    Guid? ProjectId, string? Name, string? BaseUrl, string? Environment,
    TargetAuthorisation? Authorisation, TargetOverride? Override, bool Removed, DateTimeOffset? UpdatedAt)
{
    public static TargetState Initial => new(null, null, null, null, null, null, false, null);

    public static TargetState Evolve(TargetState state, object change) => change switch
    {
        TargetChange.TargetAdded e => state with { ProjectId = e.ProjectId, Name = e.Name, BaseUrl = e.BaseUrl, Environment = e.Environment, UpdatedAt = e.At },
        TargetChange.TargetChanged e => state with { Name = e.Name, BaseUrl = e.BaseUrl, Environment = e.Environment, UpdatedAt = e.At },
        TargetChange.TargetAuthorised e => state with { Authorisation = new(e.BaseUrl, e.Environment, e.By, e.At), UpdatedAt = e.At },
        TargetChange.AuthorisationVoided e => state with { Authorisation = null, UpdatedAt = e.At },
        TargetChange.OverrideSet e => state with { Override = new(e.AllowNonIdempotent, e.AllowFailureInjection, e.Reason, e.By, e.At), UpdatedAt = e.At },
        TargetChange.OverrideCleared e => state with { Override = null, UpdatedAt = e.At },
        TargetChange.TargetRemoved e => state with { Removed = true, Authorisation = null, Override = null, UpdatedAt = e.At },
        _ => state
    };

    /// <summary>True when the state is a target of <paramref name="projectId"/> that is not removed.</summary>
    public bool ExistsIn(Guid projectId) => !Removed && ProjectId == projectId;

    /// <summary>
    /// The authorisation, when it is for the URL and the class that the target has now. A
    /// change of either voids it, so this is a second guard, not the rule.
    /// </summary>
    public TargetAuthorisation? AuthorisationInForce =>
        Authorisation is { } a && a.BaseUrl == BaseUrl && a.Environment == Environment ? a : null;

    public bool IsProduction => Environment == TargetEnvironment.Production;
}

internal static class TargetJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Write(TargetState state) => JsonSerializer.Serialize(state, Options);

    public static TargetState Read(string document) => JsonSerializer.Deserialize<TargetState>(document, Options)!;
}

public static class TargetRules
{
    public const int MaxTargetsPerProject = 20;
    public const int MaxUrlChars = 2_000;
    public const int MaxReasonChars = 500;

    /// <summary>
    /// The base URL in its one stored form: scheme and host in lower case, the path without a
    /// slash at its end, no query and no fragment. The syntax rules are the outbound URL
    /// rules (absolute http or https, a valid host name, no credentials in the URL). The
    /// private-network rule of those is not applied: Vellum never sends a request to a
    /// target, and a test system is often on a private network. A URL with credentials is
    /// refused because the URL is stored and shown; credentials go to k6 in the environment.
    /// </summary>
    public static (string? Url, string? Refusal) Normalise(string? baseUrl)
    {
        var text = baseUrl?.Trim();
        if (string.IsNullOrEmpty(text))
            return (null, "A base URL is required.");
        if (TextRule.Line("baseUrl", text, MaxUrlChars) is { } error)
            return (null, error.Message + ".");
        if (OutboundUrlPolicy.CheckWithoutResolving(text, allowPrivateNetwork: true, requireHttps: false) is { Allowed: false } verdict)
            return (null, verdict.Reason);

        var uri = new Uri(text);
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return (null, "Must not contain a query or a fragment. The script adds the path of each operation to the base URL.");
        return (OutboundUrlPolicy.OriginOf(uri) + uri.AbsolutePath.TrimEnd('/'), null);
    }

    /// <summary>SHA-256 of the stored base URL in lower-case hex. The script compares it with the URL it is run against.</summary>
    public static string UrlHash(string baseUrl) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(baseUrl)));
}

public static class TargetDecider
{
    private static Decision Ok(params object[] changes) => new Decision.Success(changes);
    private static Decision Invalid(string field, string message) => new Decision.Invalid([new ValidationError(field, message)]);
    private static readonly Decision Missing = new Decision.NotFound("Load-test target not found");

    private static Decision? Check(string? name, string? environment, string? baseUrl, out string url)
    {
        url = "";
        if (string.IsNullOrWhiteSpace(name))
            return Invalid("name", "A name is required.");
        if (TextRule.Line("name", name) is { } nameError)
            return Invalid("name", nameError.Message + ".");
        // No default: a request that names no class is refused, so no target is production by omission.
        if (environment is null || !TargetEnvironment.All.Contains(environment))
            return Invalid("environment", $"State the class of the system: {string.Join(", ", TargetEnvironment.All)}. There is no default.");
        var (normalised, refusal) = TargetRules.Normalise(baseUrl);
        if (normalised is null)
            return Invalid("baseUrl", refusal!);
        url = normalised;
        return null;
    }

    public static Decision Add(TargetState state, Guid projectId, string? name, string? baseUrl, string? environment, string by, DateTimeOffset at)
    {
        if (state.ProjectId is not null)
            return new Decision.Conflict("A target with this ID exists.");
        return Check(name, environment, baseUrl, out var url) ?? Ok(new TargetChange.TargetAdded(projectId, name!.Trim(), url, environment!, by, at));
    }

    /// <summary>
    /// A change of the base URL or of the class voids the authorisation and clears the
    /// override: both were given for the URL and the class that a person read.
    /// </summary>
    public static Decision Change(TargetState state, Guid projectId, string? name, string? baseUrl, string? environment, string by, DateTimeOffset at)
    {
        if (!state.ExistsIn(projectId))
            return Missing;
        if (Check(name, environment, baseUrl, out var url) is { } refusal)
            return refusal;

        var events = new List<object> { new TargetChange.TargetChanged(name!.Trim(), url, environment!, by, at) };
        var cause = url != state.BaseUrl ? "base-url-changed" : environment != state.Environment ? "environment-changed" : null;
        if (cause is not null && state.Authorisation is not null)
            events.Add(new TargetChange.AuthorisationVoided(cause, by, at));
        if (cause is not null && state.Override is not null)
            events.Add(new TargetChange.OverrideCleared(cause, by, at));
        return new Decision.Success(events);
    }

    /// <summary>
    /// The caller names what it read and authorises: the stored base URL and the class. A
    /// target that holds another URL or another class now is not authorised by this call.
    /// The version that the caller read is checked with the write (<see cref="Targets.Authorise"/>).
    /// </summary>
    public static Decision Authorise(TargetState state, Guid projectId, string? baseUrl, string? environment, string by, DateTimeOffset at)
    {
        if (!state.ExistsIn(projectId))
            return Missing;
        if (!string.Equals(baseUrl, state.BaseUrl, StringComparison.Ordinal))
            return new Decision.Conflict(Targets.AuthoriseConflict("base URL"));
        if (!string.Equals(environment, state.Environment, StringComparison.Ordinal))
            return new Decision.Conflict(Targets.AuthoriseConflict("class"));
        return Ok(new TargetChange.TargetAuthorised(state.BaseUrl!, state.Environment!, by, at));
    }

    public static Decision Revoke(TargetState state, Guid projectId, string by, DateTimeOffset at) =>
        !state.ExistsIn(projectId) ? Missing
        : state.Authorisation is null ? Ok()
        : Ok(new TargetChange.AuthorisationVoided("revoked", by, at));

    public static Decision SetOverride(
        TargetState state, Guid projectId, bool allowNonIdempotent, bool allowFailureInjection, string? reason, string? confirmName, string by, DateTimeOffset at)
    {
        if (!state.ExistsIn(projectId))
            return Missing;
        if (!allowNonIdempotent && !allowFailureInjection)
            return state.Override is null ? Ok() : Ok(new TargetChange.OverrideCleared("cleared", by, at));

        if (!state.IsProduction)
            return Invalid("environment", "An override is for a production-class target. A target of another class has no exclusion to override.");
        if (string.IsNullOrWhiteSpace(reason))
            return Invalid("reason", "State why this production system may get these requests.");
        if (TextRule.Line("reason", reason, TargetRules.MaxReasonChars) is { } error)
            return Invalid("reason", error.Message + ".");
        if (!string.Equals(confirmName, state.Name, StringComparison.Ordinal))
            return Invalid("confirmName", "Type the name of the target to confirm the override.");
        return Ok(new TargetChange.OverrideSet(allowNonIdempotent, allowFailureInjection, reason.Trim(), by, at));
    }

    /// <summary>A removal takes the authorisation and the override with it. Each is its own change, so each has its audit entry.</summary>
    public static Decision Remove(TargetState state, Guid projectId, string by, DateTimeOffset at)
    {
        if (!state.ExistsIn(projectId))
            return Missing;
        var events = new List<object>();
        if (state.Authorisation is not null)
            events.Add(new TargetChange.AuthorisationVoided("target-removed", by, at));
        if (state.Override is not null)
            events.Add(new TargetChange.OverrideCleared("target-removed", by, at));
        events.Add(new TargetChange.TargetRemoved(by, at));
        return new Decision.Success(events);
    }
}

/// <param name="Environment">Required: <c>development</c>, <c>test</c>, <c>staging</c> or <c>production</c>.</param>
public sealed record TargetRequest(string? Name, string? BaseUrl, string? Environment);

/// <summary>What the caller read and authorises. A target that differs in one of the three is not authorised: 409.</summary>
/// <param name="BaseUrl">The base URL of the target in its stored form.</param>
/// <param name="Environment">The class of the target.</param>
/// <param name="Version">The version of the target.</param>
public sealed record AuthoriseTargetRequest(string? BaseUrl, string? Environment, long? Version);

/// <param name="ConfirmName">The name of the target, typed again.</param>
public sealed record TargetOverrideRequest(bool AllowNonIdempotent, bool AllowFailureInjection, string? Reason, string? ConfirmName);

/// <param name="Authorisation">Null when no person authorised the target for the base URL and the class it has now.</param>
/// <param name="Override">Null when the production defaults apply.</param>
public sealed record TargetDto(
    Guid Id, string Name, string BaseUrl, string Environment, long Version,
    TargetAuthorisation? Authorisation, TargetOverride? Override, DateTimeOffset UpdatedAt);

public static class Targets
{
    public const string AuditAuthorised = "load-test.target.authorised";
    public const string AuditAuthorisationVoided = "load-test.target.authorisation-voided";
    public const string AuditOverrideSet = "load-test.target.override-set";
    public const string AuditOverrideCleared = "load-test.target.override-cleared";

    internal static string AuthoriseConflict(string what) =>
        $"The {what} of the target is not the one you name. Read the target again and authorise what it holds now.";

    /// <summary>
    /// Writes of one project that count rows, or that check one row and change another, take
    /// turns on this lock. It is held to the end of the transaction of <paramref name="database"/>.
    /// </summary>
    internal static Task LockProjectAsync(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database, Guid projectId, CancellationToken ct) =>
        database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LockSpace}, hashtext({projectId.ToString()}))", ct);

    internal static TargetDto Dto(Guid id, TargetState state, long version) => new(
        id, state.Name!, state.BaseUrl!, state.Environment!, version, state.AuthorisationInForce, state.Override, state.UpdatedAt!.Value);

    /// <summary>The target of the project, or null.</summary>
    internal static async Task<(TargetState State, long Version)?> LoadAsync(AppDbContext db, Guid projectId, Guid targetId, CancellationToken ct)
    {
        var row = await db.Targets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == targetId && t.ProjectId == projectId && !t.Removed, ct);
        return row is null ? null : (TargetJson.Read(row.Document), row.Version);
    }

    public static async Task<IResult> List(
        Guid projectId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);

        var rows = await db.Targets.AsNoTracking().Where(t => t.ProjectId == projectId && !t.Removed)
            .OrderBy(t => t.Name).ThenBy(t => t.Id).ToListAsync(ct);
        return Results.Ok(rows.Select(r => Dto(r.Id, TargetJson.Read(r.Document), r.Version)).ToList());
    }

    public static async Task<IResult> Add(
        Guid projectId, TargetRequest request, HttpContext http, WorkspaceAuthorizationService auth, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var targetId = Guid.NewGuid();
        var result = await WriteAsync(http, projectId, targetId, checkPlace: true,
            state => TargetDecider.Add(state, projectId, request.Name, request.BaseUrl, request.Environment, userId, time.GetUtcNow()), ct);
        return result is CommandResult<TargetDto>.Success created
            ? Results.Created($"/api/projects/{projectId}/load-tests/targets/{targetId}", created.Value)
            : result.ToHttpResult();
    }

    public static async Task<IResult> Change(
        Guid projectId, Guid targetId, TargetRequest request, HttpContext http, WorkspaceAuthorizationService auth, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        return (await WriteAsync(http, projectId, targetId, checkPlace: false,
            state => TargetDecider.Change(state, projectId, request.Name, request.BaseUrl, request.Environment, userId, time.GetUtcNow()), ct)).ToHttpResult();
    }

    /// <summary>Owner and a person only: an agent token cannot allow load against a system.</summary>
    public static async Task<IResult> Authorise(
        Guid projectId, Guid targetId, AuthoriseTargetRequest request, HttpContext http, WorkspaceAuthorizationService auth, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Owner, ct);
        if (request.Version is not { } version)
            return new CommandResult<TargetDto>.Conflict(AuthoriseConflict("version")).ToHttpResult();
        return (await WriteAsync(http, projectId, targetId, checkPlace: false,
            state => TargetDecider.Authorise(state, projectId, request.BaseUrl, request.Environment, userId, time.GetUtcNow()), ct, version)).ToHttpResult();
    }

    public static async Task<IResult> Revoke(
        Guid projectId, Guid targetId, HttpContext http, WorkspaceAuthorizationService auth, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Owner, ct);
        return (await WriteAsync(http, projectId, targetId, checkPlace: false,
            state => TargetDecider.Revoke(state, projectId, userId, time.GetUtcNow()), ct)).ToHttpResult();
    }

    public static async Task<IResult> SetOverride(
        Guid projectId, Guid targetId, TargetOverrideRequest request, HttpContext http, WorkspaceAuthorizationService auth, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Owner, ct);
        return (await WriteAsync(http, projectId, targetId, checkPlace: false,
            state => TargetDecider.SetOverride(
                state, projectId, request.AllowNonIdempotent, request.AllowFailureInjection, request.Reason, request.ConfirmName, userId, time.GetUtcNow()), ct)).ToHttpResult();
    }

    /// <summary>Removes the target. Its scripts and results stay: they hold the target as it was.</summary>
    public static async Task<IResult> Remove(
        Guid projectId, Guid targetId, HttpContext http, WorkspaceAuthorizationService auth, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        var result = await WriteAsync(http, projectId, targetId, checkPlace: false,
            state => TargetDecider.Remove(state, projectId, userId, time.GetUtcNow()), ct);
        return result is CommandResult<TargetDto>.Success ? Results.NoContent() : result.ToHttpResult();
    }

    /// <summary>
    /// One transaction: the row of the target and the audit entries of the changes that give
    /// or take a permission. An audit entry never exists without its change. The row is
    /// locked from the read to the commit, so writes of one target take turns.
    /// </summary>
    private static async Task<CommandResult<TargetDto>> WriteAsync(
        HttpContext http, Guid projectId, Guid targetId, bool checkPlace, Func<TargetState, Decision> decide, CancellationToken ct, long? readVersion = null)
    {
        var userId = http.User.UserId();
        var workspaceId = await http.RequestServices.GetRequiredService<WorkspaceAuthorizationService>().GetProjectWorkspaceIdAsync(projectId, ct);

        return await SharedTransaction.RunAsync<CommandResult<TargetDto>>(http.RequestServices, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            if (checkPlace)
            {
                // Adds of one project take turns, so two of them cannot both take the last place.
                await LockProjectAsync(db.Database, projectId, ct);
                if (await db.Targets.CountAsync(t => t.ProjectId == projectId && !t.Removed, ct) >= TargetRules.MaxTargetsPerProject)
                    return new CommandResult<TargetDto>.Conflict($"A project holds at most {TargetRules.MaxTargetsPerProject} load-test targets. Remove one before you add another.");
            }

            var row = await db.Targets
                .FromSql($"SELECT * FROM loadtests.targets WHERE id = {targetId} FOR UPDATE")
                .FirstOrDefaultAsync(ct);
            var before = row is null ? TargetState.Initial : TargetJson.Read(row.Document);
            var version = row?.Version ?? 0;

            var decision = decide(before);
            if (decision is not Decision.Success { Value: var changes })
            {
                return decision switch
                {
                    Decision.Invalid invalid => new CommandResult<TargetDto>.Invalid(invalid.Errors),
                    Decision.Conflict conflict => new CommandResult<TargetDto>.Conflict(conflict.Message),
                    _ => new CommandResult<TargetDto>.NotFound("Load-test target not found"),
                };
            }

            // The row was locked from the read to here, so the version that the caller read is compared exactly.
            if (readVersion is { } read && version != read)
                return new CommandResult<TargetDto>.Conflict(AuthoriseConflict("version"));

            var state = changes.Aggregate(before, TargetState.Evolve);
            if (changes.Count > 0)
            {
                if (row is null)
                {
                    row = new TargetEntity { Id = targetId, ProjectId = projectId };
                    db.Targets.Add(row);
                }
                version += changes.Count;
                row.Name = state.Name!;
                row.Version = version;
                row.Removed = state.Removed;
                row.Document = TargetJson.Write(state);
                await db.SaveChangesAsync(ct);
            }

            foreach (var entry in changes.Select(c => Audit(targetId, before, c)).OfType<(string Action, string Target)>())
                await AuditLog.AppendAsync(db, workspaceId, userId, entry.Action, entry.Target, ct);

            await transaction.CommitAsync(ct);
            return new CommandResult<TargetDto>.Success(Dto(targetId, state, version));
        }, ct);
    }

    // Any constant works; it keeps this lock apart from other advisory locks in the database.
    private const int LockSpace = 0x4C4F4144;

    /// <summary>
    /// The audit entry of a change that gives or takes a permission. It names the class and
    /// the origin of the URL that the permission is or was for: <paramref name="before"/> is
    /// the target before the changes of the call, so a voided authorisation is named as it was
    /// given, not as the target is after the change that voids it.
    /// </summary>
    private static (string Action, string Target)? Audit(Guid targetId, TargetState before, object change)
    {
        string What(string? environment, string? baseUrl) =>
            $"load-test-target:{targetId} environment:{environment} origin:{OutboundUrlPolicy.OriginOf(baseUrl)}";
        return change switch
        {
            TargetChange.TargetAuthorised given => (AuditAuthorised, What(given.Environment, given.BaseUrl)),
            TargetChange.AuthorisationVoided voided => (AuditAuthorisationVoided, $"{What(before.Authorisation?.Environment ?? before.Environment, before.Authorisation?.BaseUrl ?? before.BaseUrl)} cause:{voided.Cause}"),
            TargetChange.OverrideSet set => (AuditOverrideSet, $"{What(before.Environment, before.BaseUrl)} non-idempotent:{(set.AllowNonIdempotent ? "allowed" : "excluded")} failure-injection:{(set.AllowFailureInjection ? "allowed" : "excluded")}"),
            TargetChange.OverrideCleared cleared => (AuditOverrideCleared, $"{What(before.Environment, before.BaseUrl)} cause:{cleared.Cause}"),
            _ => null,
        };
    }
}
