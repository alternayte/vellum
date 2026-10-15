using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Modules.Modelling;
using Vellum.Modules.Stress;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.LoadTests;

/// <summary>
/// Where the numbers of a measured result come from: the import of one file,
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

/// <param name="UnexpectedStatus">
/// Requests whose status was not the one the script expected. For a step that expects a
/// failure, a 2xx status is unexpected, so this is not an error count. A 3xx status is always unexpected.
/// </param>
/// <param name="UnexpectedStatusRate">Unexpected status divided by requests. Null when no request counted.</param>
/// <param name="ThroughputPerSecond">Requests that got the expected status, for each second of the measured window.</param>
/// <param name="Latency">The durations of the requests that expected a 2xx status and got one.</param>
/// <param name="ExpectedFailures">Requests that expected a failure and got one.</param>
/// <param name="ExpectedFailureLatency">The durations of those requests. They are in no success latency.</param>
public sealed record MeasuredOperationDto(
    string Label, Guid OperationId, string Name, long Requests, long UnexpectedStatus, double? UnexpectedStatusRate, double ThroughputPerSecond,
    MeasuredLatencyDto? Latency, long ExpectedFailures, MeasuredLatencyDto? ExpectedFailureLatency);

/// <param name="UnexpectedStatus">Flows in which a request did not get the expected status.</param>
/// <param name="Latency">Null when no flow counted, and when a step of the flow expects a failure: such a flow has no success latency.</param>
public sealed record MeasuredFlowDto(
    string Label, long Iterations, long UnexpectedStatus, long DroppedIterations, double? UnexpectedStatusRate, double ThroughputPerSecond, MeasuredLatencyDto? Latency);

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
/// <param name="Faults">The faults that the scenario declares. The script injects none: a person injects them in the target.</param>
/// <param name="ExpectsFailure">True when a step of the script expects a failure.</param>
public sealed record MeasuredResultDto(
    MeasuredResultSummaryDto Result, IReadOnlyList<MeasuredOperationDto> Operations, IReadOnlyList<K6NotCovered> NotCovered,
    int Faults, bool ExpectsFailure);

/// <summary>A file that was not stored. <paramref name="Type"/> is <c>unknown_script</c>, <c>outdated_script</c>, <c>identity_mismatch</c> or <c>target_not_authorised</c>.</summary>
public sealed record ResultRefusedResponse(string Type, string Title, string Detail, IReadOnlyList<FieldError> Errors);

public static class MeasuredResults
{
    public const string Measured = "measured";
    public const int MaxResultsPerProject = 200;

    /// <summary>A result may state a start this long before the script existed, and a start or an end this long after now: clocks differ.</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>The database keeps a time to the microsecond, an event to 100 nanoseconds. Two times nearer than this are one time.</summary>
    private static readonly TimeSpan StoredTimePrecision = TimeSpan.FromMicroseconds(2);

    /// <summary>
    /// Imports the result file of a k6 run. The body is the file as k6 wrote it.
    /// <para>
    /// A result whose script hash names no script that Vellum generated for this project is
    /// rejected, and so is a result whose stated scenario, revision or target differs from
    /// what the script was generated for. It is not stored as "unverified".
    /// A result names the exact test, revision and environment, and Vellum ingests only results
    /// whose identity can be verified. A stored row with the label
    /// <c>measured</c> and no known script would name none of those, and every reader of the
    /// comparison would have to remember that this one row means less. The stored script is
    /// what binds the numbers to a scenario version, a model version and an authorised
    /// target, so without it there is nothing to bind.
    /// </para>
    /// <para>
    /// A result is bound to the load of its script too (<see cref="ResultFile"/>): a run with
    /// other executor options, a shorter run and a run with other iteration counts are refused.
    /// </para>
    /// <para>
    /// The limit of this check: it proves which script the file is for, not that k6 produced
    /// the numbers. Vellum does not run k6, so the file is a statement of the caller. The row
    /// records who imported it, and an agent token is recorded as such.
    /// </para>
    /// </summary>
    public static async Task<IResult> Import(
        Guid projectId, HttpContext http, WorkspaceAuthorizationService auth, AppDbContext db, IEventStore store, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
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
        if (plan.Generator != K6ScriptGenerator.Version)
        {
            return Refused("outdated_script", "The script of this result is from an older generator",
                $"The script was generated by {plan.Generator}. This server accepts results of {K6ScriptGenerator.Version}, which binds a run to the load of its profile. Generate the script again, run it, and import its result.",
                [new("scriptHash", $"The script {scriptHash} is of generator {plan.Generator}.")]);
        }
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
        var export = format == ResultFile.SummaryExportFormat;
        if (startedAt < script.CreatedAt - ClockSkew || startedAt > now + ClockSkew)
        {
            return Invalid([new(export ? "setup_data.startedAtMs" : "startedAt",
                "The run starts before the script was generated, or in the future. A result is from a run of the stored script.")]);
        }
        var endedAt = startedAt + TimeSpan.FromMilliseconds(parsed.Run.ElapsedMs);
        if (endedAt > now + ClockSkew)
        {
            return Invalid([new(export ? "metrics.vellum_run_elapsed_ms.max" : "window.elapsedMs",
                "The run ends in the future: its start and its length give an end after now. A result is from a run that has ended.")]);
        }

        // A result is attributed to a target only while the authorisation that the script was generated under is in
        // force. A later authorisation, by any person, is another one: it does not admit the scripts of an earlier one.
        var target = await Targets.LoadAsync(db, projectId, script.TargetId, ct);
        if (target?.State.AuthorisationInForce is not { } authorisation || authorisation.BaseUrl != script.BaseUrl || authorisation.Environment != EnumText.Of(script.Environment)
            || authorisation.By != script.AuthorisedBy || (authorisation.At - script.AuthorisedAt).Duration() >= StoredTimePrecision)
        {
            return Refused("target_not_authorised", "The authorisation that the script was generated under is not in force",
                "The target was removed, or its base URL or class changed, or its authorisation was revoked after the script was generated. "
                + "An authorisation that an owner gave later does not cover this script. Generate a new script and run it.",
                [new("scriptHash", $"The script is for target '{script.TargetName}' at {script.BaseUrl} ({EnumText.Of(script.Environment)}), under the authorisation of {script.AuthorisedAt:O}.")]);
        }

        var row = new ResultEntity
        {
            Id = Guid.NewGuid(), ProjectId = projectId, ScriptId = script.Id, ScriptHash = script.Hash,
            ScenarioId = script.ScenarioId, ScenarioVersion = script.ScenarioVersion,
            ModelStreamId = script.ModelStreamId, ModelVersion = script.ModelVersion,
            TargetId = script.TargetId, Environment = script.Environment, Format = format,
            StartedAt = startedAt, EndedAt = endedAt, ElapsedMs = parsed.Run.ElapsedMs,
            ContentHash = Convert.ToHexStringLower(SHA256.HashData(bytes.Span)),
            Document = JsonSerializer.Serialize(parsed.Run, Scripts.Json),
            ImportedBy = userId,
            ImportedVia = AgentPrincipal.IsAgent(http.User) ? AgentPrincipal.Actor(http.User) : null,
            ImportedAt = now,
        };

        // The checks and the insert are one unit, and the writes of one project take turns: two imports cannot both
        // take the last place, and a script that this result names is not removed to make room for another script.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await Targets.LockProjectAsync(db.Database, projectId, ct);

        if (await ExistingAsync() is { } same)
            return Duplicate(same.Id, same.SameFile);
        if (await db.Results.CountAsync(r => r.ProjectId == projectId, ct) >= MaxResultsPerProject)
        {
            return Results.Conflict(new ErrorResponse("conflict",
                $"A project holds at most {MaxResultsPerProject} measured results. Delete one before you import another."));
        }

        db.Results.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A writer that does not take the lock stored the same run. The failed statement ended this transaction.
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var stored = await ExistingAsync();
            return Duplicate(stored?.Id, stored?.SameFile ?? false);
        }
        // The script went with its project, or was removed, between the read and the insert.
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            return Refused("unknown_script", "The script of this result does not exist any more",
                "The script was removed while the file was imported. Nothing is stored. Generate the script again, run it, and import its result.",
                [new("scriptHash", $"No script of this project has the hash {scriptHash}.")]);
        }

        return Results.Created($"/api/projects/{projectId}/load-tests/results/{row.Id}",
            Dto(row, script, await new CurrentVersions(store, projectId).BecauseAsync(row, ct)));

        static IResult Invalid(IReadOnlyList<FieldError> errors) =>
            Results.BadRequest(new ErrorResponse("validation_error", "The result file is not valid", Errors: errors));

        static IResult Refused(string type, string title, string detail, IReadOnlyList<FieldError> errors) =>
            Results.Json(new ResultRefusedResponse(type, title, detail, errors), statusCode: StatusCodes.Status422UnprocessableEntity);

        // The same bytes, or the same run in the other format: a run of a script has one start and one length.
        async Task<(Guid Id, bool SameFile)?> ExistingAsync()
        {
            var found = await db.Results.AsNoTracking()
                .Where(r => r.ProjectId == projectId
                    && (r.ContentHash == row.ContentHash || (r.ScriptId == row.ScriptId && r.StartedAt == row.StartedAt && r.ElapsedMs == row.ElapsedMs)))
                .Select(r => new { r.Id, r.ContentHash }).FirstOrDefaultAsync(ct);
            return found is null ? null : (found.Id, found.ContentHash == row.ContentHash);
        }

        static IResult Duplicate(Guid? existing, bool sameFile) => Results.Conflict(new ErrorResponse("conflict",
            sameFile ? "This file is imported already" : "This run is imported already",
            existing is { } id
                ? sameFile ? $"It is the measured result {id}." : $"The file is another format of the same run. The run is the measured result {id}."
                : null));
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
        Same("environment", claimed.Environment, EnumText.Of(script.Environment));
        Same("baseUrlHash", claimed.BaseUrlHash, TargetRules.UrlHash(script.BaseUrl));
        return errors;
    }

    public static async Task<IResult> List(
        Guid projectId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, AppDbContext db, IEventStore store, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);

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
        Guid projectId, Guid resultId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, AppDbContext db, IEventStore store, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);
        return await ReadAsync(db, store, projectId, resultId, ct) is { } result ? Results.Ok(result) : NotFound("Measured result");
    }

    /// <summary>
    /// Deletes a result. A result that a stress profile names as the source of a measured
    /// value stays: the value would then name a measurement that does not exist.
    /// </summary>
    public static async Task<IResult> Delete(
        Guid projectId, Guid resultId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Editor, ct);

        // The check and the delete are one unit. A save of a profile that names a result takes the same lock,
        // so no profile starts to name this result between the two.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await Targets.LockProjectAsync(db.Database, projectId, ct);

        var reference = MeasurementRefs.For(resultId);
        var users = await db.Profiles.AsNoTracking().Where(p => p.ProjectId == projectId && p.Document.Contains(reference)).Select(p => p.Name).ToListAsync(ct);
        if (users.Count > 0)
        {
            return Results.Conflict(new ErrorResponse("conflict", "A stress profile names this result as the source of a measured value",
                $"Change the label in: {string.Join(", ", users.Order(StringComparer.Ordinal))}. Then delete the result."));
        }

        var deleted = await db.Results.Where(r => r.Id == resultId && r.ProjectId == projectId).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        return deleted == 0 ? NotFound("Measured result") : Results.NoContent();
    }

    /// <summary>A result of the project with its script, or null. A result of another project reads as not found.</summary>
    internal static async Task<MeasuredResultDto?> ReadAsync(AppDbContext db, IEventStore store, Guid projectId, Guid resultId, CancellationToken ct) =>
        (await ReadWithPlanAsync(db, store, projectId, resultId, ct))?.Result;

    /// <summary>A result of the project with the plan of its script, or null.</summary>
    internal static async Task<(MeasuredResultDto Result, K6Plan Plan)?> ReadWithPlanAsync(
        AppDbContext db, IEventStore store, Guid projectId, Guid resultId, CancellationToken ct)
    {
        var row = await Rows(db, db.Results.Where(r => r.ProjectId == projectId && r.Id == resultId)).FirstOrDefaultAsync(ct);
        return row is null
            ? null
            : (Dto(row.Result, row.Script, await new CurrentVersions(store, projectId).BecauseAsync(row.Result, ct)), Scripts.Plan(row.Script));
    }

    private sealed record Row(ResultEntity Result, ScriptEntity Script);

    private static IQueryable<Row> Rows(AppDbContext db, IQueryable<ResultEntity> results) =>
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
        var plan = Scripts.Plan(script);
        var seconds = run.MeasuredSeconds;
        var flow = run.Flow;
        return new MeasuredResultDto(
            new MeasuredResultSummaryDto(
                row.Id, Measured, row.ScenarioId, row.ScenarioVersion, row.ModelStreamId, row.ModelVersion, script.ProfileId, script.ProfileVersion,
                row.StartedAt, row.EndedAt, seconds,
                new MeasuredProvenance(
                    "k6-import", row.Format, row.ScriptId, row.ScriptHash, row.ContentHash,
                    row.TargetId, script.TargetName, script.BaseUrl, EnumText.Of(row.Environment), row.ImportedBy, row.ImportedVia, row.ImportedAt),
                new MeasuredFlowDto(
                    Measured, flow.Iterations, flow.UnexpectedStatus, flow.DroppedIterations,
                    Share(flow.UnexpectedStatus, flow.Iterations), (flow.Iterations - flow.UnexpectedStatus) / seconds, Latency(flow.Latency)),
                staleBecause.Count > 0, staleBecause),
            [.. run.Operations.Select(o => new MeasuredOperationDto(
                Measured, o.OperationId, o.Name, o.Requests, o.UnexpectedStatus, Share(o.UnexpectedStatus, o.Requests), (o.Requests - o.UnexpectedStatus) / seconds,
                Latency(o.Latency), o.ExpectedFailures, Latency(o.ExpectedFailureLatency)))],
            plan.NotCovered, plan.Faults, plan.ExpectFailure().Count > 0);

        static double? Share(long part, long whole) => whole > 0 ? (double)part / whole : null;

        static MeasuredLatencyDto? Latency(MeasuredLatency? l) =>
            l is null ? null : new(Measured, l.Count, l.MinMs, l.MeanMs, l.P50Ms, l.P90Ms, l.P95Ms, l.P99Ms, l.MaxMs);
    }

    /// <summary>The versions that the model streams and the scenarios of stored results have now, read once each.</summary>
    internal sealed class CurrentVersions(IEventStore store, Guid projectId)
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
/// The name a stress profile gives to an imported result: <c>k6:{result ID}</c>. What makes a
/// value <c>measured</c> is in <see cref="MeasuredServiceTimes"/>.
/// </summary>
public static class MeasurementRefs
{
    public const string Prefix = "k6:";

    public static string For(Guid resultId) => $"{Prefix}{resultId}";
}
