using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Stress;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.LoadTests;

/// <summary>
/// Where the numbers of a measured result come from (SDD guarantee 2): the import of one file,
/// by one caller, for one script that Vellum generated for one authorised target.
/// </summary>
/// <param name="Kind"><c>k6-import</c>.</param>
/// <param name="ImportedVia">The agent token that sent the file, or null when a person did.</param>
public sealed record MeasuredProvenance(
    string Kind, string Format, Guid ScriptId, string ScriptHash, string ContentHash,
    Guid TargetId, string TargetName, string BaseUrl, string Environment,
    string ImportedBy, string? ImportedVia, DateTimeOffset ImportedAt);

/// <summary>Request durations in milliseconds. <paramref name="Label"/> is <c>measured</c>.</summary>
public sealed record MeasuredLatencyDto(
    string Label, long Count, double MinMs, double MeanMs, double P50Ms, double P90Ms, double P95Ms, double P99Ms, double MaxMs);

/// <param name="ErrorRate">Failed divided by requests. Null when no request counted.</param>
/// <param name="ThroughputPerSecond">Requests that got the expected status, for each second of the measured window.</param>
public sealed record MeasuredOperationDto(
    string Label, Guid OperationId, string Name, long Requests, long Failed, double? ErrorRate, double ThroughputPerSecond, MeasuredLatencyDto? Latency);

public sealed record MeasuredFlowDto(
    string Label, long Iterations, long Failed, long DroppedIterations, double? ErrorRate, double ThroughputPerSecond, MeasuredLatencyDto? Latency);

/// <summary>
/// One imported result. <paramref name="Stale"/> is true when the scenario or the model
/// stream has another version now than the one the result is bound to: the result then
/// measured another declared flow than the one the project holds.
/// </summary>
/// <param name="StaleBecause"><c>model</c>, <c>scenario</c>, or both.</param>
public sealed record MeasuredResultSummaryDto(
    Guid Id, string Label, Guid ScenarioId, long ScenarioVersion, Guid ModelStreamId, long ModelVersion, Guid ProfileId, int ProfileVersion,
    DateTimeOffset StartedAt, DateTimeOffset EndedAt, double MeasuredSeconds,
    MeasuredProvenance Provenance, MeasuredFlowDto Flow, bool Stale, IReadOnlyList<string> StaleBecause);

/// <param name="NotCovered">The steps of the scenario that the script did not perform. The result says nothing about them.</param>
public sealed record MeasuredResultDto(
    MeasuredResultSummaryDto Result, IReadOnlyList<MeasuredOperationDto> Operations, IReadOnlyList<K6NotCovered> NotCovered);

/// <summary>A file that was not stored. <paramref name="Type"/> is <c>unknown_script</c>, <c>identity_mismatch</c> or <c>target_not_authorised</c>.</summary>
public sealed record ResultRefusedResponse(string Type, string Title, string Detail, IReadOnlyList<FieldError> Errors);

public static class MeasuredResults
{
    public const string Measured = "measured";
    public const int MaxResultsPerProject = 200;

    /// <summary>A result may state a start this long before the script existed or after now: clocks differ.</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Imports the result file of a k6 run. The body is the file as k6 wrote it.
    /// <para>
    /// A result whose script hash names no script that Vellum generated for this project is
    /// rejected, and so is a result whose stated scenario, revision or target differs from
    /// what the script was generated for. It is not stored as "unverified". SDD guarantee 11
    /// says that a result names the exact test, revision and environment, and §8 lets Vellum
    /// ingest results "whose identity can be verified". A stored row with the label
    /// <c>measured</c> and no known script would name none of those, and every reader of the
    /// comparison would have to remember that this one row means less. The stored script is
    /// what binds the numbers to a scenario version, a model version and an authorised
    /// target, so without it there is nothing to bind.
    /// </para>
    /// <para>
    /// The limit of this check: it proves which script the file is for, not that k6 produced
    /// the numbers. Vellum does not run k6, so the file is a statement of the caller. The row
    /// records who imported it, and an agent token is recorded as such.
    /// </para>
    /// </summary>
    public static async Task<IResult> Import(
        Guid projectId, HttpContext http, WorkspaceAuthorizationService auth, LoadTestsDbContext db, IEventStore store, TimeProvider time, CancellationToken ct)
    {
        var userId = Targets.UserId(http.User);
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        using var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, ct);
        if (buffer.Length == 0)
            return Results.BadRequest(new ErrorResponse("invalid_body", "The body is the result file of a k6 run. It is empty."));
        var bytes = buffer.GetBuffer().AsMemory(0, (int)buffer.Length);

        var (document, unreadable) = ResultFile.Open(bytes);
        if (document is null)
            return Results.BadRequest(new ErrorResponse("invalid_body", "The file cannot be read", unreadable));
        using var _ = document;
        var root = document.RootElement;

        var (format, scriptHash, formatError) = ResultFile.Identify(root);
        if (format is null)
            return Invalid([formatError!]);

        var script = await db.Scripts.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Hash == scriptHash, ct);
        if (script is null)
        {
            return Refused("unknown_script", "The result names a script that Vellum did not generate for this project",
                "Nothing binds these numbers to a scenario, a model revision and a target, so they are not stored. Generate the script in Vellum, run that file, and import its result.",
                [new("scriptHash", $"No script of this project has the hash {scriptHash}.")]);
        }

        var plan = Scripts.Plan(script);
        var (parsed, errors) = ResultFile.Read(root, format, scriptHash!, plan);
        if (parsed is null)
            return Invalid(errors);

        if (parsed.Claimed is { } claimed && Mismatches(claimed, script) is { Count: > 0 } mismatches)
        {
            return Refused("identity_mismatch", "The result does not belong to the script it names",
                "The file states another scenario, revision or target than the script with this hash was generated for. It is not stored.", mismatches);
        }

        var now = time.GetUtcNow();
        var startedAt = parsed.StartedAt!.Value;
        if (startedAt < script.CreatedAt - ClockSkew || startedAt > now + ClockSkew)
        {
            return Invalid([new(format == ResultFile.SummaryExportFormat ? "setup_data.startedAtMs" : "startedAt",
                "The run starts before the script was generated, or in the future. A result is from a run of the stored script.")]);
        }

        // A result is attributed to a target only while a person's authorisation of that base URL is in force.
        var target = await Targets.LoadAsync(store, projectId, script.TargetId, ct);
        if (target?.State.AuthorisationInForce is not { } authorisation || authorisation.BaseUrl != script.BaseUrl || authorisation.Environment != script.Environment)
        {
            return Refused("target_not_authorised", "The target of the script is not authorised now",
                "The target was removed, or its base URL or class changed, or its authorisation was revoked after the script was generated. "
                + "An owner authorises the target again; then generate a new script and run it.",
                [new("scriptHash", $"The script is for target '{script.TargetName}' at {script.BaseUrl} ({script.Environment}).")]);
        }

        var row = new ResultEntity
        {
            Id = Guid.NewGuid(), ProjectId = projectId, ScriptId = script.Id, ScriptHash = script.Hash,
            ScenarioId = script.ScenarioId, ScenarioVersion = script.ScenarioVersion,
            ModelStreamId = script.ModelStreamId, ModelVersion = script.ModelVersion,
            TargetId = script.TargetId, Environment = script.Environment, Format = format,
            StartedAt = startedAt, EndedAt = startedAt + TimeSpan.FromMilliseconds(parsed.Run.ElapsedMs),
            ContentHash = Convert.ToHexStringLower(SHA256.HashData(bytes.Span)),
            Document = JsonSerializer.Serialize(parsed.Run, Scripts.Json),
            ImportedBy = userId,
            ImportedVia = AgentPrincipal.IsAgent(http.User) ? AgentPrincipal.Actor(http.User) : null,
            ImportedAt = now,
        };

        if (await db.Results.AsNoTracking().Where(r => r.ProjectId == projectId && r.ContentHash == row.ContentHash).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct) is { } same)
            return Duplicate(same);
        if (await db.Results.CountAsync(r => r.ProjectId == projectId, ct) >= MaxResultsPerProject)
        {
            return Results.Conflict(new ErrorResponse("conflict",
                $"A project holds at most {MaxResultsPerProject} measured results. Delete one before you import another."));
        }

        db.Results.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Duplicate(null);
        }

        return Results.Created($"/api/projects/{projectId}/load-tests/results/{row.Id}",
            Dto(row, script, await new CurrentVersions(store, projectId).BecauseAsync(row, ct)));

        static IResult Invalid(IReadOnlyList<FieldError> errors) =>
            Results.BadRequest(new ErrorResponse("validation_error", "The result file is not valid", Errors: errors));

        static IResult Refused(string type, string title, string detail, IReadOnlyList<FieldError> errors) =>
            Results.Json(new ResultRefusedResponse(type, title, detail, errors), statusCode: StatusCodes.Status422UnprocessableEntity);

        static IResult Duplicate(Guid? existing) => Results.Conflict(new ErrorResponse("conflict", "This file is imported already",
            existing is { } id ? $"It is the measured result {id}." : null));
    }

    private static List<FieldError> Mismatches(ClaimedIdentity claimed, ScriptEntity script)
    {
        var errors = new List<FieldError>();
        void Same<T>(string field, T? stated, T expected)
        {
            if (!EqualityComparer<T>.Default.Equals(stated, expected))
                errors.Add(new(field, $"The file states {stated?.ToString() ?? "nothing"}; the script was generated for {expected}."));
        }

        Same("generator", claimed.Generator, K6ScriptGenerator.Version);
        Same("scenarioId", claimed.ScenarioId, script.ScenarioId);
        Same("scenarioVersion", claimed.ScenarioVersion, script.ScenarioVersion);
        Same("modelStreamId", claimed.ModelStreamId, script.ModelStreamId);
        Same("modelVersion", claimed.ModelVersion, script.ModelVersion);
        Same("targetId", claimed.TargetId, script.TargetId);
        Same("environment", claimed.Environment, script.Environment);
        Same("baseUrlHash", claimed.BaseUrlHash, TargetRules.UrlHash(script.BaseUrl));
        return errors;
    }

    public static async Task<IResult> List(
        Guid projectId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, LoadTestsDbContext db, IEventStore store, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, Targets.UserId(user), WorkspaceRole.Viewer, ct);

        // At most MaxResultsPerProject rows, so the newest come first in memory.
        var rows = (await Rows(db, db.Results.Where(r => r.ProjectId == projectId)).ToListAsync(ct))
            .OrderByDescending(x => x.Result.StartedAt).ThenByDescending(x => x.Result.Id).ToList();
        var current = new CurrentVersions(store, projectId);
        var items = new List<MeasuredResultSummaryDto>();
        foreach (var row in rows)
            items.Add(Dto(row.Result, row.Script, await current.BecauseAsync(row.Result, ct)).Result);
        return Results.Ok(items);
    }

    public static async Task<IResult> Get(
        Guid projectId, Guid resultId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, LoadTestsDbContext db, IEventStore store, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, Targets.UserId(user), WorkspaceRole.Viewer, ct);
        return await ReadAsync(db, store, projectId, resultId, ct) is { } result ? Results.Ok(result) : Targets.NotFound("Measured result");
    }

    /// <summary>
    /// Deletes a result. A result that a stress profile names as the source of a measured
    /// value stays: the value would then name a measurement that does not exist.
    /// </summary>
    public static async Task<IResult> Delete(
        Guid projectId, Guid resultId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, LoadTestsDbContext db, StressDbContext stressDb, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, Targets.UserId(user), WorkspaceRole.Editor, ct);

        var reference = MeasurementRefs.For(resultId);
        var users = await stressDb.Profiles.AsNoTracking().Where(p => p.ProjectId == projectId && p.Document.Contains(reference)).Select(p => p.Name).ToListAsync(ct);
        if (users.Count > 0)
        {
            return Results.Conflict(new ErrorResponse("conflict", "A stress profile names this result as the source of a measured value",
                $"Change the label in: {string.Join(", ", users.Order(StringComparer.Ordinal))}. Then delete the result."));
        }

        var deleted = await db.Results.Where(r => r.Id == resultId && r.ProjectId == projectId).ExecuteDeleteAsync(ct);
        return deleted == 0 ? Targets.NotFound("Measured result") : Results.NoContent();
    }

    /// <summary>A result of the project with its script, or null. A result of another project reads as not found.</summary>
    internal static async Task<MeasuredResultDto?> ReadAsync(LoadTestsDbContext db, IEventStore store, Guid projectId, Guid resultId, CancellationToken ct)
    {
        var row = await Rows(db, db.Results.Where(r => r.ProjectId == projectId && r.Id == resultId)).FirstOrDefaultAsync(ct);
        return row is null ? null : Dto(row.Result, row.Script, await new CurrentVersions(store, projectId).BecauseAsync(row.Result, ct));
    }

    private sealed record Row(ResultEntity Result, ScriptEntity Script);

    private static IQueryable<Row> Rows(LoadTestsDbContext db, IQueryable<ResultEntity> results) =>
        from result in results.AsNoTracking()
        join script in db.Scripts.AsNoTracking() on result.ScriptId equals script.Id
        select new Row(result, new ScriptEntity
        {
            Id = script.Id, Hash = script.Hash, ProfileId = script.ProfileId, ProfileVersion = script.ProfileVersion,
            TargetId = script.TargetId, TargetName = script.TargetName, BaseUrl = script.BaseUrl, Environment = script.Environment, Plan = script.Plan,
        });

    private static MeasuredResultDto Dto(ResultEntity row, ScriptEntity script, IReadOnlyList<string> staleBecause)
    {
        var run = JsonSerializer.Deserialize<MeasuredRun>(row.Document, Scripts.Json)!;
        var seconds = run.MeasuredSeconds;
        var flow = run.Flow;
        return new MeasuredResultDto(
            new MeasuredResultSummaryDto(
                row.Id, Measured, row.ScenarioId, row.ScenarioVersion, row.ModelStreamId, row.ModelVersion, script.ProfileId, script.ProfileVersion,
                row.StartedAt, row.EndedAt, seconds,
                new MeasuredProvenance(
                    "k6-import", row.Format, row.ScriptId, row.ScriptHash, row.ContentHash,
                    row.TargetId, script.TargetName, script.BaseUrl, row.Environment, row.ImportedBy, row.ImportedVia, row.ImportedAt),
                new MeasuredFlowDto(
                    Measured, flow.Iterations, flow.Failed, flow.DroppedIterations,
                    Share(flow.Failed, flow.Iterations), (flow.Iterations - flow.Failed) / seconds, Latency(flow.Latency)),
                staleBecause.Count > 0, staleBecause),
            [.. run.Operations.Select(o => new MeasuredOperationDto(
                Measured, o.OperationId, o.Name, o.Requests, o.Failed, Share(o.Failed, o.Requests), (o.Requests - o.Failed) / seconds, Latency(o.Latency)))],
            Scripts.Plan(script).NotCovered);

        static double? Share(long part, long whole) => whole > 0 ? (double)part / whole : null;

        static MeasuredLatencyDto? Latency(MeasuredLatency? l) =>
            l is null ? null : new(Measured, l.Count, l.MinMs, l.MeanMs, l.P50Ms, l.P90Ms, l.P95Ms, l.P99Ms, l.MaxMs);
    }

    /// <summary>The versions that the model streams and the scenarios of stored results have now, read once each.</summary>
    private sealed class CurrentVersions(IEventStore store, Guid projectId)
    {
        private readonly Dictionary<Guid, long> _models = [];
        private readonly Dictionary<Guid, long?> _scenarios = [];

        public async Task<IReadOnlyList<string>> BecauseAsync(ResultEntity row, CancellationToken ct)
        {
            if (!_models.TryGetValue(row.ModelStreamId, out var model))
                _models[row.ModelStreamId] = model = (await store.Load<ModelState>(StreamId.From(row.ModelStreamId), ct)).Version;
            if (!_scenarios.TryGetValue(row.ScenarioId, out var scenario))
            {
                // A deleted scenario has no current version: the result is stale.
                var (document, version) = await StressAccess.ScenarioAsync(store, projectId, row.ScenarioId, ct);
                _scenarios[row.ScenarioId] = scenario = document is null ? null : version;
            }

            var because = new List<string>();
            if (model != row.ModelVersion) because.Add("model");
            if (scenario != row.ScenarioVersion) because.Add("scenario");
            return because;
        }
    }
}

/// <summary>
/// The name a stress profile gives to an imported result: <c>k6:{result ID}</c>. A value of a
/// profile is <c>measured</c> only with such a name, and only when the name resolves to a
/// result of the same project.
/// </summary>
public static class MeasurementRefs
{
    public const string Prefix = "k6:";

    public static string For(Guid resultId) => $"{Prefix}{resultId}";

    /// <summary>The references that name no imported result of the project.</summary>
    public static async Task<IReadOnlySet<string>> UnresolvedAsync(
        LoadTestsDbContext db, Guid projectId, IReadOnlyCollection<string> references, CancellationToken ct)
    {
        // One spelling for each result: the ID in lower case with dashes.
        var ids = references.Distinct(StringComparer.Ordinal)
            .Where(r => r.StartsWith(Prefix, StringComparison.Ordinal) && Guid.TryParseExact(r[Prefix.Length..], "D", out var id) && For(id) == r)
            .ToDictionary(r => r, r => Guid.ParseExact(r[Prefix.Length..], "D"), StringComparer.Ordinal);
        var wanted = ids.Values.Distinct().ToList();
        var found = wanted.Count == 0
            ? []
            : await db.Results.AsNoTracking().Where(r => r.ProjectId == projectId && wanted.Contains(r.Id)).Select(r => r.Id).ToListAsync(ct);
        return references.Where(r => !ids.TryGetValue(r, out var id) || !found.Contains(id)).ToHashSet(StringComparer.Ordinal);
    }
}
