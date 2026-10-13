using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.LoadTests;
using Vellum.Modules.Modelling;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Schemas;
using Vellum.Modules.Stress.Engine;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Stress;

/// <summary>The headline of a run, for lists. Every number is an estimate.</summary>
public sealed record StressRunSummary(
    string Label, long Arrivals, long Completed, long Failed, double ThroughputPerSecond,
    double? P50Ms, double? P95Ms, double? P99Ms, string? Bottleneck, string Confidence, bool Drained);

/// <summary>
/// A stored run and what pins it: the version of the profile, of the model stream and of the
/// scenario, the seed and the engine version. The same five give the same <paramref name="Hash"/>.
/// </summary>
/// <param name="Outdated">True when the model or the scenario changed after the run.</param>
/// <param name="OutdatedBecause"><c>model</c>, <c>scenario</c>, or both.</param>
/// <param name="CurrentProfileVersion">The version the profile has now. A run of an older version used other inputs.</param>
public sealed record StressRunSummaryDto(
    Guid Id, Guid ProfileId, int ProfileVersion, int CurrentProfileVersion,
    Guid ModelStreamId, long ModelVersion, Guid ScenarioId, long ScenarioVersion,
    string Seed, string EngineVersion, string Hash, DateTimeOffset At,
    StressRunSummary Summary, bool Outdated, IReadOnlyList<string> OutdatedBecause);

public sealed record StressRunDto(StressRunSummaryDto Run, StressProfile Profile, StressResult Result);

/// <summary>A run that was refused or that passed a limit. Nothing was stored.</summary>
/// <param name="Type"><c>stress-refused</c>, or <c>stress-too-large</c> when a limit stopped the run.</param>
public sealed record StressRefusedResponse(string Type, string Title, IReadOnlyList<StressIssue> Issues);

public static class StressRuns
{
    /// <summary>
    /// Runs the profile against the scenario and the model of the branch, off the request
    /// thread, and stores the result. A refused run, and a run that passes a limit, stores
    /// nothing and gets 422 with the issues.
    /// </summary>
    public static Task<IResult> Run(
        Guid projectId, Guid profileId, Guid? branchId,
        HttpContext http, IEventStore store, SchemasDbContext schemasDb, StressDbContext db, LoadTestsDbContext loadTestsDb,
        WorkspaceAuthorizationService auth, StressRunGate gate, StressLimits limits, StressRunPolicy policy, TimeProvider time,
        CancellationToken ct) =>
        ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor, async (streamId, userId) =>
        {
            var row = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileId && p.ProjectId == projectId, ct);
            if (row is null)
                return StressAccess.NotFound("Stress profile");
            var profile = StressProfiles.Read(row.Document);

            using var place = gate.TryEnter(await auth.GetProjectWorkspaceIdAsync(projectId, ct), out var refusal);
            if (place is null)
            {
                var seconds = Math.Max(1, (int)Math.Ceiling(refusal!.RetryAfter.TotalSeconds));
                http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                return Results.Json(
                    new ErrorResponse(
                        refusal.Code,
                        refusal.Code == "busy" ? "The instance is busy with other stress runs" : "Too many stress runs",
                        $"Wait {seconds} seconds and try again."),
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            var world = await StressAccess.LoadAsync(store, schemasDb, projectId, streamId, profile.ScenarioId, ct);
            if (world.Scenario is null)
            {
                return Refused([new StressIssue("unknown-scenario", profile.ScenarioId.ToString(),
                    "The scenario of this profile does not exist any more. Choose another scenario.")]);
            }

            // A measured service time counts for the confidence level only when its k6 result is of the model
            // version and the scenario version of this run. A result is never changed, so the same profile
            // version, model version and scenario version give the same doubts and the same hash.
            var doubts = await MeasuredServiceTimes.DoubtsAsync(loadTestsDb, projectId, profile, streamId, world.ModelVersion, world.ScenarioVersion, ct);

            // The budget is real time. It decides whether there is a result, never what the result is.
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(limits.WallClock);
            var outcome = await Task.Run(() => StressEngine.Run(world.Model, world.Scenario, profile, limits, budget.Token, doubts), CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            if (outcome.Result is not { } result)
                return Refused(outcome.Issues);

            var run = new StressRunEntity
            {
                Id = Guid.NewGuid(), ProfileId = profileId, ProjectId = projectId, ProfileVersion = row.Version,
                ModelStreamId = streamId, ModelVersion = world.ModelVersion,
                ScenarioId = profile.ScenarioId, ScenarioVersion = world.ScenarioVersion,
                Seed = profile.Seed, EngineVersion = result.EngineVersion, Hash = result.Hash,
                At = time.GetUtcNow(), RanBy = userId,
                Summary = JsonSerializer.Serialize(Summarise(result), StressJson.Options),
                Profile = row.Document,
                Result = JsonSerializer.Serialize(result, StressJson.Options),
            };

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // The lock holds the profile for this transaction: a delete of it, or of its project, waits and then removes this run too.
            var held = await db.Profiles.FromSql($"SELECT * FROM stress.profiles WHERE id = {profileId} FOR UPDATE").AsNoTracking().ToListAsync(ct);
            if (held.Count == 0)
                return StressAccess.NotFound("Stress profile");

            db.Runs.Add(run);
            await db.SaveChangesAsync(ct);
            // Retention: the newest runs stay.
            var keep = db.Runs.Where(r => r.ProfileId == profileId).OrderByDescending(r => r.At).ThenByDescending(r => r.Id)
                .Take(policy.MaxRunsPerProfile).Select(r => r.Id);
            await db.Runs.Where(r => r.ProfileId == profileId && !keep.Contains(r.Id)).ExecuteDeleteAsync(ct);
            await transaction.CommitAsync(ct);

            return Results.Created($"/api/projects/{projectId}/stress/runs/{run.Id}",
                new StressRunDto(Dto(run, held[0].Version, world.ModelVersion, world.ScenarioVersion), profile, result));
        }, ct);

    private static IResult Refused(IReadOnlyList<StressIssue> issues)
    {
        var tooLarge = issues.Any(i => i.Code == "stress-too-large");
        return Results.Json(
            new StressRefusedResponse(
                tooLarge ? "stress-too-large" : "stress-refused",
                tooLarge ? "The run is over a limit. No result is stored." : "The run was refused. No result is stored.",
                issues),
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    public static async Task<IResult> List(
        Guid projectId, Guid profileId, ClaimsPrincipal user, WorkspaceAuthorizationService auth,
        StressDbContext db, IEventStore store, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, StressAccess.UserId(user), WorkspaceRole.Viewer, ct);

        var profile = await db.Profiles.AsNoTracking()
            .Where(p => p.Id == profileId && p.ProjectId == projectId).Select(p => new { p.Version }).FirstOrDefaultAsync(ct);
        if (profile is null)
            return StressAccess.NotFound("Stress profile");

        // Without the documents: a list does not read the results.
        var rows = await db.Runs.AsNoTracking()
            .Where(r => r.ProfileId == profileId)
            .OrderByDescending(r => r.At).ThenByDescending(r => r.Id)
            .Select(r => new StressRunEntity
            {
                Id = r.Id, ProfileId = r.ProfileId, ProjectId = r.ProjectId, ProfileVersion = r.ProfileVersion,
                ModelStreamId = r.ModelStreamId, ModelVersion = r.ModelVersion, ScenarioId = r.ScenarioId, ScenarioVersion = r.ScenarioVersion,
                Seed = r.Seed, EngineVersion = r.EngineVersion, Hash = r.Hash, At = r.At, RanBy = r.RanBy, Summary = r.Summary,
            })
            .ToListAsync(ct);

        var current = new CurrentVersions(store, projectId);
        var items = new List<StressRunSummaryDto>();
        foreach (var row in rows)
            items.Add(await current.DtoAsync(row, profile.Version, ct));
        return Results.Ok(items);
    }

    public static async Task<IResult> Get(
        Guid projectId, Guid runId, ClaimsPrincipal user, WorkspaceAuthorizationService auth,
        StressDbContext db, IEventStore store, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, StressAccess.UserId(user), WorkspaceRole.Viewer, ct);

        return await ReadAsync(db, store, projectId, runId, ct) is { } run ? Results.Ok(run) : StressAccess.NotFound("Stress run");
    }

    public static async Task<IResult> Delete(
        Guid projectId, Guid runId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, StressDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, StressAccess.UserId(user), WorkspaceRole.Editor, ct);

        var deleted = await db.Runs.Where(r => r.Id == runId && r.ProjectId == projectId).ExecuteDeleteAsync(ct);
        return deleted == 0 ? StressAccess.NotFound("Stress run") : Results.NoContent();
    }

    /// <summary>A run of the project with its documents, or null. A run of another project reads as not found.</summary>
    internal static async Task<StressRunDto?> ReadAsync(StressDbContext db, IEventStore store, Guid projectId, Guid runId, CancellationToken ct)
    {
        var row = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId && r.ProjectId == projectId, ct);
        if (row is null)
            return null;

        var profileVersion = await db.Profiles.AsNoTracking().Where(p => p.Id == row.ProfileId).Select(p => p.Version).FirstAsync(ct);
        return new StressRunDto(
            await new CurrentVersions(store, projectId).DtoAsync(row, profileVersion, ct),
            StressProfiles.Read(row.Profile),
            JsonSerializer.Deserialize<StressResult>(row.Result, StressJson.Options)!);
    }

    private static StressRunSummary Summarise(StressResult result) => new(
        StressResult.Estimated, result.Flow.Arrivals, result.Flow.Counts.Completed, result.Flow.Counts.Failed,
        result.Flow.ThroughputPerSecond, result.Flow.Latency.P50Ms, result.Flow.Latency.P95Ms, result.Flow.Latency.P99Ms,
        result.Bottleneck.Name, result.Confidence.Level, result.Drain.Drained);

    private static StressRunSummaryDto Dto(StressRunEntity row, int profileVersion, long modelVersion, long? scenarioVersion)
    {
        var because = new List<string>();
        if (modelVersion != row.ModelVersion) because.Add("model");
        if (scenarioVersion != row.ScenarioVersion) because.Add("scenario");
        return new StressRunSummaryDto(
            row.Id, row.ProfileId, row.ProfileVersion, profileVersion,
            row.ModelStreamId, row.ModelVersion, row.ScenarioId, row.ScenarioVersion,
            row.Seed, row.EngineVersion, row.Hash, row.At,
            JsonSerializer.Deserialize<StressRunSummary>(row.Summary, StressJson.Options)!,
            because.Count > 0, because);
    }

    /// <summary>The versions that the model streams and the scenarios of stored runs have now, read once each.</summary>
    private sealed class CurrentVersions(IEventStore store, Guid projectId)
    {
        private readonly Dictionary<Guid, long> _models = [];
        private readonly Dictionary<Guid, long?> _scenarios = [];

        public async Task<StressRunSummaryDto> DtoAsync(StressRunEntity row, int profileVersion, CancellationToken ct)
        {
            if (!_models.TryGetValue(row.ModelStreamId, out var model))
                _models[row.ModelStreamId] = model = (await store.Load<ModelState>(StreamId.From(row.ModelStreamId), ct)).Version;
            if (!_scenarios.TryGetValue(row.ScenarioId, out var scenario))
            {
                // A deleted scenario has no current version: the run is outdated.
                var (document, version) = await StressAccess.ScenarioAsync(store, projectId, row.ScenarioId, ct);
                _scenarios[row.ScenarioId] = scenario = document is null ? null : version;
            }
            return Dto(row, profileVersion, model, scenario);
        }
    }
}
