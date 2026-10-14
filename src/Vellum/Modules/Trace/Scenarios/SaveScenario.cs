using System.Security.Claims;
using System.Text.Json;
using Deedbox;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vellum.Modules.Trace.Engine;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Trace.Scenarios;

/// <summary>
/// Create and replace. The body is the scenario document itself. A document with validation
/// issues is saved (SDD §8: blocking issues prevent Build Ready, not saving). A body that
/// cannot be read as a scenario document, or that breaks a limit of <see cref="ScenarioShape"/>,
/// is refused with 400, and one that is too large with 413.
/// </summary>
public static class SaveScenario
{
    // Any constant works; it keeps this lock apart from other advisory locks in the database.
    private const int ProjectLockSpace = 0x5343454E;

    private static readonly string[] StepTypes = ["call", "publish", "write", "parallel", "branch"];
    private static readonly string[] AssertionTypes = ["outcome", "event-count"];

    public static async Task<IResult> Create(
        Guid projectId,
        HttpContext http,
        ClaimsPrincipal user,
        WorkspaceAuthorizationService auth,
        IOptions<JsonOptions> json,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var (document, error) = await ReadAsync(http.Request, json.Value.SerializerOptions, ct);
        if (document is null)
            return error!;

        if (document.Id.Value == Guid.Empty)
        {
            return Results.BadRequest(new ErrorResponse("validation_error", "Validation failed",
                Errors: [new FieldError("id", "A scenario needs an ID")]));
        }

        var result = await CreateAsync(http.RequestServices, projectId, document, ct);
        return result.ToCreatedResult($"/api/projects/{projectId}/scenarios/{document.Id}");
    }

    /// <summary>
    /// Creates a scenario from a document that is inside the limits of <see cref="ScenarioShape"/>.
    /// The HTTP create and the acceptance of an AI flow proposal both write through here.
    /// </summary>
    internal static Task<CommandResult<Scenario>> CreateAsync(
        IServiceProvider requestServices, Guid projectId, Scenario document, CancellationToken ct) =>
        SharedTransaction.RunAsync(requestServices, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var created = await CreateInTransactionAsync(
                db, services.GetRequiredService<IEventStore>().UseDbContext(db), projectId, document, null, ct);
            if (created is CommandResult<Scenario>.Success)
                await transaction.CommitAsync(ct);
            return created;
        }, ct);

    /// <summary>
    /// The create inside a transaction that the caller opened on <paramref name="db"/> and
    /// commits. <paramref name="pinned"/> is the event store bound to that context.
    /// </summary>
    /// <param name="origin">Set when the document is an accepted AI flow proposal.</param>
    internal static async Task<CommandResult<Scenario>> CreateInTransactionAsync(
        DbContext db, IEventStore pinned, Guid projectId, Scenario document, ScenarioOrigin? origin, CancellationToken ct)
    {
        // The count and the new row commit together, and creates of one project take turns on
        // an advisory lock, so two of them cannot both take the last place. No unit asks for
        // this lock while it holds the lock of a scenario stream, so it adds no wait cycle.
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({ProjectLockSpace}, hashtext({projectId.ToString()}))", ct);

        var count = await db.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM trace.scenarios WHERE project_id = {projectId}")
            .SingleAsync(ct);
        if (count >= ScenarioDecider.MaxPerProject)
        {
            return new CommandResult<Scenario>.Conflict(
                $"A project holds at most {ScenarioDecider.MaxPerProject} scenarios. Delete one before you add another.");
        }

        try
        {
            return await pinned.DecideInTransaction<ScenarioState, Scenario>(
                document.Id,
                state => ScenarioDecider.Create(state, projectId, document, origin),
                state => state.Document!,
                ct);
        }
        // The ID names a stream of another kind: a model branch or a schema.
        catch (DeedboxException e) when (e.Code == DeedboxError.StreamTypeMismatch)
        {
            return new CommandResult<Scenario>.Conflict(ScenarioDecider.IdInUse);
        }
    }

    public static async Task<IResult> Update(
        Guid projectId, Guid scenarioId,
        HttpContext http,
        ClaimsPrincipal user,
        WorkspaceAuthorizationService auth,
        IEventStore store,
        IOptions<JsonOptions> json,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var (document, error) = await ReadAsync(http.Request, json.Value.SerializerOptions, ct);
        if (document is null)
            return error!;

        // The route names the scenario. A body may leave the ID out, but may not name another.
        if (document.Id.Value != Guid.Empty && document.Id.Value != scenarioId)
        {
            return Results.BadRequest(new ErrorResponse("validation_error", "Validation failed",
                Errors: [new FieldError("id", "The ID in the body differs from the ID in the path")]));
        }

        var replacement = document with { Id = scenarioId };
        var result = await ScenarioStreams.DecideAsync(
            store, scenarioId,
            state => ScenarioDecider.Update(state, projectId, replacement),
            state => state.Document!,
            new CommandResult<Scenario>.NotFound("Scenario not found"),
            ct);
        return result.ToHttpResult();
    }

    private static async Task<(Scenario? Document, IResult? Error)> ReadAsync(
        HttpRequest request, JsonSerializerOptions options, CancellationToken ct)
    {
        Scenario? document;
        try
        {
            using var raw = await JsonDocument.ParseAsync(
                request.Body, new JsonDocumentOptions { MaxDepth = options.MaxDepth }, ct);

            // The serializer reports a step without a known `type` as an abstract type it
            // cannot build. The author needs to know which step and which values are valid.
            if (TypeProblem(raw.RootElement) is { } problem)
                return (null, Malformed(problem));

            document = raw.Deserialize<Scenario>(options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return (null, Malformed(ex.Message));
        }

        if (document is null)
            return (null, Malformed("The body is null."));

        // The decision applies the same rule. It is applied here first because a document that
        // breaks it may be too deep to write, and the size below is measured by writing it.
        if (ScenarioShape.Check(document) is { } shapeError)
        {
            return (null, Results.BadRequest(new ErrorResponse("validation_error", "Validation failed",
                Errors: [new FieldError(shapeError.Field, shapeError.Message)])));
        }

        // Every save stores the whole document again, so the stored size is bounded, not only
        // the size of the request.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, options).Length;
        if (bytes > ScenarioShape.MaxDocumentBytes)
        {
            return (null, Results.Json(
                new ErrorResponse("scenario_too_large",
                    $"A scenario document holds at most {ScenarioShape.MaxDocumentBytes} bytes. This one has {bytes}."),
                statusCode: StatusCodes.Status413PayloadTooLarge));
        }

        return (document, null);
    }

    /// <summary>The first step or assertion whose <c>type</c> is missing or unknown, or null.</summary>
    private static string? TypeProblem(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        return Steps(Member(root, "steps"), "steps") ?? Assertions(Member(root, "assertions"));
    }

    private static string? Steps(JsonElement? list, string path)
    {
        if (list is not { ValueKind: JsonValueKind.Array } steps)
            return null;

        var index = 0;
        foreach (var step in steps.EnumerateArray())
        {
            var at = $"{path}[{index++}]";
            if (step.ValueKind != JsonValueKind.Object)
                continue;
            if (TypeOf(step, at, "step", StepTypes, out var type) is { } problem)
                return problem;

            var nested = type switch
            {
                "call" => Steps(Member(step, "then"), $"{at}.then") ?? Steps(Member(step, "onError"), $"{at}.onError"),
                "branch" => Steps(Member(step, "then"), $"{at}.then") ?? Steps(Member(step, "else"), $"{at}.else"),
                "parallel" => Branches(Member(step, "branches"), at),
                "publish" => Handlers(Member(step, "handlers"), at),
                _ => null,
            };
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private static string? Branches(JsonElement? branches, string at)
    {
        if (branches is not { ValueKind: JsonValueKind.Array } list)
            return null;

        var index = 0;
        foreach (var branch in list.EnumerateArray())
        {
            if (Steps(branch, $"{at}.branches[{index++}]") is { } problem)
                return problem;
        }

        return null;
    }

    private static string? Handlers(JsonElement? handlers, string at)
    {
        if (handlers is not { ValueKind: JsonValueKind.Object } map)
            return null;

        var index = 0;
        foreach (var handler in map.EnumerateObject())
        {
            if (Steps(handler.Value, $"{at}.handlers[{index++}]") is { } problem)
                return problem;
        }

        return null;
    }

    private static string? Assertions(JsonElement? list)
    {
        if (list is not { ValueKind: JsonValueKind.Array } assertions)
            return null;

        var index = 0;
        foreach (var assertion in assertions.EnumerateArray())
        {
            var at = $"assertions[{index++}]";
            if (assertion.ValueKind == JsonValueKind.Object
                && TypeOf(assertion, at, "assertion", AssertionTypes, out _) is { } problem)
                return problem;
        }

        return null;
    }

    private static string? TypeOf(JsonElement value, string at, string what, string[] types, out string? type)
    {
        type = null;
        var allowed = string.Join(", ", types);
        if (!value.TryGetProperty("type", out var given))
            return $"{at}: the {what} has no \"type\". Use one of: {allowed}.";

        if (given.ValueKind == JsonValueKind.String)
            type = types.FirstOrDefault(given.ValueEquals);

        return type is null
            ? $"{at}: the \"type\" of the {what} is not known. Use one of: {allowed}."
            : null;
    }

    /// <summary>A member by name in any letter case, as the serializer reads it.</summary>
    private static JsonElement? Member(JsonElement value, string name)
    {
        JsonElement? found = null;
        foreach (var property in value.EnumerateObject())
        {
            if (property.NameEquals(name) || string.Equals(SafeName(property), name, StringComparison.OrdinalIgnoreCase))
                found = property.Value;
        }

        return found;
    }

    private static string? SafeName(JsonProperty property)
    {
        try
        {
            return property.Name;
        }
        // A name with half of a surrogate pair is no member the serializer knows.
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static IResult Malformed(string detail) =>
        Results.BadRequest(new ErrorResponse("invalid_scenario", "The body is not a scenario document", detail));
}
