using System.Security.Claims;
using System.Text.Json;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.LoadTests;
using Vellum.Modules.Modelling;
using Vellum.Modules.Schemas;
using Vellum.Modules.Settings.Document;
using Vellum.Modules.Stress.Engine;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Stress;

public sealed record StressProfileSummaryDto(Guid Id, string Name, Guid ScenarioId, int Version, DateTimeOffset UpdatedAt, int Runs);

/// <summary>The seed, the window and the arrival rate that the form of a new profile shows first.</summary>
public sealed record StressProfileDefaults(string Seed, double DurationSeconds, double WarmUpSeconds, double ArrivalRatePerSecond);

public sealed record StressProfileDto(Guid Id, int Version, DateTimeOffset UpdatedAt, StressProfile Profile);

/// <summary>
/// Create, read, replace and delete of stress profiles. A save checks ranges and shape only
/// (<see cref="StressProfileRules"/>): a profile that lacks an input of its scenario is saved,
/// and the run names what is missing.
/// </summary>
public static class StressProfiles
{
    // Any constant works; it keeps this lock apart from other advisory locks in the database.
    private const int ProjectLockSpace = 0x53545253;

    public static async Task<IResult> List(
        Guid projectId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, StressDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, StressAccess.UserId(user), WorkspaceRole.Viewer, ct);

        var items = await db.Profiles.AsNoTracking()
            .Where(p => p.ProjectId == projectId)
            .OrderBy(p => p.Name).ThenBy(p => p.Id)
            .Select(p => new StressProfileSummaryDto(
                p.Id, p.Name, p.ScenarioId, p.Version, p.UpdatedAt, db.Runs.Count(r => r.ProfileId == p.Id)))
            .ToListAsync(ct);
        return Results.Ok(items);
    }

    public static async Task<IResult> Get(
        Guid projectId, Guid profileId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, StressDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, StressAccess.UserId(user), WorkspaceRole.Viewer, ct);

        var row = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileId && p.ProjectId == projectId, ct);
        return row is null ? StressAccess.NotFound("Stress profile") : Results.Ok(Dto(row));
    }

    /// <summary>What a profile for the scenario must state, read from the model of the branch.</summary>
    public static Task<IResult> Requirements(
        Guid projectId, Guid scenarioId, Guid? branchId,
        HttpContext http, IEventStore store, SchemasDbContext schemasDb, CancellationToken ct) =>
        ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Viewer, async (streamId, _) =>
        {
            var world = await StressAccess.LoadAsync(store, schemasDb, projectId, streamId, scenarioId, ct);
            return world.Scenario is null
                ? StressAccess.NotFound("Scenario")
                : Results.Ok(StressEngine.Requirements(world.Model, world.Scenario));
        }, ct);

    /// <summary>
    /// What a new profile of this project starts with: the workspace's simulation settings,
    /// held inside the limits of a run.
    /// </summary>
    public static async Task<IResult> Defaults(
        Guid projectId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, SettingsResolver settings, StressLimits limits, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, StressAccess.UserId(user), WorkspaceRole.Viewer, ct);

        var simulation = (await settings.GetWorkspaceAsync(await auth.GetProjectWorkspaceIdAsync(projectId, ct), ct)).Effective.Simulation;
        var duration = Math.Min(simulation.DurationSeconds, limits.MaxDurationSeconds);
        return Results.Ok(new StressProfileDefaults(
            simulation.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            duration,
            Math.Min(simulation.WarmUpSeconds, Math.Max(0, duration - 1)),
            simulation.ArrivalRatePerSecond));
    }

    public static async Task<IResult> Create(
        Guid projectId, HttpContext http, ClaimsPrincipal user, WorkspaceAuthorizationService auth,
        StressDbContext db, LoadTestsDbContext loadTestsDb, IEventStore store, StressLimits limits, StressRunPolicy policy, TimeProvider time, CancellationToken ct)
    {
        var userId = StressAccess.UserId(user);
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var (profile, error) = await StressAccess.ReadAsync<StressProfile>(http);
        if (profile is null)
            return error!;
        if (await RefusalAsync(profile, projectId, store, loadTestsDb, limits, ct) is { } refusal)
            return refusal;

        // The count and the new row commit together, and creates of one project take turns
        // on an advisory lock, so two of them cannot both take the last place.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({ProjectLockSpace}, hashtext({projectId.ToString()}))", ct);
        if (await db.Profiles.CountAsync(p => p.ProjectId == projectId, ct) >= policy.MaxProfilesPerProject)
        {
            return Results.Conflict(new ErrorResponse("conflict",
                $"A project holds at most {policy.MaxProfilesPerProject} stress profiles. Delete one before you add another."));
        }

        var row = new StressProfileEntity
        {
            Id = Guid.NewGuid(), ProjectId = projectId, ScenarioId = profile.ScenarioId, Name = profile.Name, Version = 1,
            Document = JsonSerializer.Serialize(profile, StressJson.Options), UpdatedAt = time.GetUtcNow(), UpdatedBy = userId,
        };
        db.Profiles.Add(row);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Created($"/api/projects/{projectId}/stress/profiles/{row.Id}", Dto(row));
    }

    public static async Task<IResult> Update(
        Guid projectId, Guid profileId, HttpContext http, ClaimsPrincipal user, WorkspaceAuthorizationService auth,
        StressDbContext db, LoadTestsDbContext loadTestsDb, IEventStore store, StressLimits limits, TimeProvider time, CancellationToken ct)
    {
        var userId = StressAccess.UserId(user);
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var (profile, error) = await StressAccess.ReadAsync<StressProfile>(http);
        if (profile is null)
            return error!;

        var row = await db.Profiles.FirstOrDefaultAsync(p => p.Id == profileId && p.ProjectId == projectId, ct);
        if (row is null)
            return StressAccess.NotFound("Stress profile");
        if (await RefusalAsync(profile, projectId, store, loadTestsDb, limits, ct) is { } refusal)
            return refusal;

        row.Name = profile.Name;
        row.ScenarioId = profile.ScenarioId;
        row.Document = JsonSerializer.Serialize(profile, StressJson.Options);
        row.Version++;
        row.UpdatedAt = time.GetUtcNow();
        row.UpdatedBy = userId;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        // The version is the concurrency token: another save came first.
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new ErrorResponse("conflict", "The profile changed while you saved it. Read it again."));
        }
        return Results.Ok(Dto(row));
    }

    /// <summary>Deletes the profile and its stored runs.</summary>
    public static async Task<IResult> Delete(
        Guid projectId, Guid profileId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, StressDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, StressAccess.UserId(user), WorkspaceRole.Editor, ct);

        var deleted = await db.Profiles.Where(p => p.Id == profileId && p.ProjectId == projectId).ExecuteDeleteAsync(ct);
        return deleted == 0 ? StressAccess.NotFound("Stress profile") : Results.NoContent();
    }

    internal static StressProfile Read(string document) => JsonSerializer.Deserialize<StressProfile>(document, StressJson.Options)!;

    private static StressProfileDto Dto(StressProfileEntity row) => new(row.Id, row.Version, row.UpdatedAt, Read(row.Document));

    private static async Task<IResult?> RefusalAsync(
        StressProfile profile, Guid projectId, IEventStore store, LoadTestsDbContext loadTestsDb, StressLimits limits, CancellationToken ct)
    {
        var errors = StressProfileRules.Check(profile, limits).Select(i => new FieldError(i.Target, i.Message)).ToList();

        // A value is measured only when it names a measurement that Vellum imported (SDD
        // guarantee 2): a k6 result of this project. A name that resolves to no such result
        // is refused, so a person cannot promote a typed number to a measured one.
        if (errors.Count == 0)
        {
            var measured = Labelled(profile).Where(l => l.Label == AssumptionLabel.Measured).ToList();
            var unresolved = await MeasurementRefs.UnresolvedAsync(loadTestsDb, projectId, [.. measured.Select(l => l.MeasurementRef!)], ct);
            errors.AddRange(measured.Where(l => unresolved.Contains(l.MeasurementRef!)).Select(l => new FieldError($"{l.Field}.measurementRef",
                $"A value is 'measured' only when it names a measured result that Vellum imported for this project, as '{MeasurementRefs.Prefix}<result ID>'. "
                + $"'{l.MeasurementRef}' names none. Import the k6 result first, or use 'declared'.")));
        }

        if (errors.Count == 0 && (await StressAccess.ScenarioAsync(store, projectId, profile.ScenarioId, ct)).Document is null)
            errors.Add(new FieldError("scenarioId", "The scenario does not exist in this project."));

        return errors.Count == 0
            ? null
            : Results.BadRequest(new ErrorResponse("validation_error", "Validation failed", Errors: errors));
    }

    /// <summary>Every labelled value of the profile, with the field path that a validation error names.</summary>
    private static IEnumerable<(string Field, AssumptionLabel Label, string? MeasurementRef)> Labelled(StressProfile profile) =>
        profile.Resources.Select((r, i) => ($"resources[{i}]", r.Label, r.MeasurementRef))
            .Concat(profile.ServiceTimes.Select((t, i) => ($"serviceTimes[{i}]", t.Label, t.MeasurementRef)))
            .Concat(profile.Subscriptions.Select((s, i) => ($"subscriptions[{i}]", s.Label, s.MeasurementRef)))
            .Concat(profile.Branches.Select((b, i) => ($"branches[{i}]", b.Label, b.MeasurementRef)))
            .Concat(profile.Overrides.Select((o, i) => ($"overrides[{i}]", o.Label, o.MeasurementRef)))
            .Append(("arrivals", profile.Arrivals.Label, profile.Arrivals.MeasurementRef));
}
