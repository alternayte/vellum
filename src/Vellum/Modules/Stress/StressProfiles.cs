using System.Security.Claims;
using System.Text.Json;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.LoadTests;
using Vellum.Modules.Modelling;
using Vellum.Modules.Schemas;
using Vellum.Modules.Settings;
using Vellum.Modules.Stress.Engine;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Stress;

public sealed record StressProfileSummaryDto(Guid Id, string Name, Guid ScenarioId, int Version, DateTimeOffset UpdatedAt, int Runs);

/// <summary>The seed, the window and the arrival rate that the form of a new profile shows first.</summary>
public sealed record StressProfileDefaults(string Seed, double DurationSeconds, double WarmUpSeconds, double ArrivalRatePerSecond);

/// <param name="Measurements">
/// The measured service times of the profile, each with <c>measurementStale</c>: whether the
/// k6 result that it names is stale now. A service time that is not here is declared.
/// </param>
public sealed record StressProfileDto(
    Guid Id, int Version, DateTimeOffset UpdatedAt, StressProfile Profile, IReadOnlyList<ProfileMeasurement> Measurements);

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
        Guid projectId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);

        var items = await db.Profiles.AsNoTracking()
            .Where(p => p.ProjectId == projectId)
            .OrderBy(p => p.Name).ThenBy(p => p.Id)
            .Select(p => new StressProfileSummaryDto(
                p.Id, p.Name, p.ScenarioId, p.Version, p.UpdatedAt, db.StressRuns.Count(r => r.ProfileId == p.Id)))
            .ToListAsync(ct);
        return Results.Ok(items);
    }

    public static async Task<IResult> Get(
        Guid projectId, Guid profileId, ClaimsPrincipal user, WorkspaceAuthorizationService auth,
        AppDbContext db, IEventStore store, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);

        var row = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileId && p.ProjectId == projectId, ct);
        return row is null ? NotFound("Stress profile") : Results.Ok(await DtoAsync(row, db, store, ct));
    }

    /// <summary>What a profile for the scenario must state, read from the model of the branch.</summary>
    public static Task<IResult> Requirements(
        Guid projectId, Guid scenarioId, Guid? branchId,
        HttpContext http, IEventStore store, AppDbContext db, CancellationToken ct) =>
        ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Viewer, async (streamId, _) =>
        {
            var world = await StressAccess.LoadAsync(store, db, projectId, streamId, scenarioId, ct);
            return world.Scenario is null
                ? NotFound("Scenario")
                : Results.Ok(StressEngine.Requirements(world.Model, world.Scenario));
        }, ct);

    /// <summary>
    /// What a new profile of this project starts with: the workspace's simulation settings,
    /// held inside the limits of a run.
    /// </summary>
    public static async Task<IResult> Defaults(
        Guid projectId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, SettingsResolver settings, StressLimits limits, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);

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
        AppDbContext db, IEventStore store, StressLimits limits, StressRunPolicy policy, TimeProvider time, CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var (sent, error) = await StressAccess.ReadAsync<StressProfile>(http);
        if (sent is null)
            return error!;

        // The count and the new row commit together, and creates of one project take turns
        // on an advisory lock, so two of them cannot both take the last place.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({ProjectLockSpace}, hashtext({projectId.ToString()}))", ct);
        var (profile, refusal) = await ResolveAsync(sent, projectId, db, store, limits, ct);
        if (refusal is not null)
            return refusal;
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
        return Results.Created($"/api/projects/{projectId}/stress/profiles/{row.Id}", await DtoAsync(row, db, store, ct));
    }

    public static async Task<IResult> Update(
        Guid projectId, Guid profileId, HttpContext http, ClaimsPrincipal user, WorkspaceAuthorizationService auth,
        AppDbContext db, IEventStore store, StressLimits limits, TimeProvider time, CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var (sent, error) = await StressAccess.ReadAsync<StressProfile>(http);
        if (sent is null)
            return error!;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Profiles.FirstOrDefaultAsync(p => p.Id == profileId && p.ProjectId == projectId, ct);
        if (row is null)
            return NotFound("Stress profile");
        var (profile, refusal) = await ResolveAsync(sent, projectId, db, store, limits, ct);
        if (refusal is not null)
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
            await transaction.CommitAsync(ct);
        }
        // The version is the concurrency token: another save came first.
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new ErrorResponse("conflict", "The profile changed while you saved it. Read it again."));
        }
        return Results.Ok(await DtoAsync(row, db, store, ct));
    }

    /// <summary>Deletes the profile and its stored runs.</summary>
    public static async Task<IResult> Delete(
        Guid projectId, Guid profileId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Editor, ct);

        var deleted = await db.Profiles.Where(p => p.Id == profileId && p.ProjectId == projectId).ExecuteDeleteAsync(ct);
        return deleted == 0 ? NotFound("Stress profile") : Results.NoContent();
    }

    internal static StressProfile Read(string document) => JsonSerializer.Deserialize<StressProfile>(document, StressJson.Options)!;

    private static async Task<StressProfileDto> DtoAsync(StressProfileEntity row, AppDbContext db, IEventStore store, CancellationToken ct)
    {
        var profile = Read(row.Document);
        return new(row.Id, row.Version, row.UpdatedAt, profile, await MeasuredServiceTimes.StateAsync(db, store, row.ProjectId, profile, ct));
    }

    /// <summary>
    /// The profile to store, or the refusal. A service time with the label <c>measured</c>
    /// gets its numbers from the k6 result that it names (see
    /// <see cref="MeasuredServiceTimes"/>): the caller picks the result and the distribution,
    /// and the numbers that the caller sent for it are not read. Every other label
    /// <c>measured</c> is refused, so a person cannot promote a typed number to a measured one.
    /// <para>
    /// The caller holds a transaction of <paramref name="db"/>. A profile that names a result
    /// takes the lock of the load-test writes of the project until that transaction ends, so
    /// the result is not deleted between this check and the commit of the profile.
    /// </para>
    /// </summary>
    private static async Task<(StressProfile Profile, IResult? Refusal)> ResolveAsync(
        StressProfile sent, Guid projectId, AppDbContext db, IEventStore store, StressLimits limits, CancellationToken ct)
    {
        if (sent.ServiceTimes?.Any(t => t is { Label: AssumptionLabel.Measured }) == true)
            await Targets.LockProjectAsync(db.Database, projectId, ct);

        var (profile, measured) = await MeasuredServiceTimes.ApplyAsync(db, projectId, sent, ct);
        var errors = measured.Count > 0
            ? measured.ToList()
            : StressProfileRules.Check(profile, limits).Select(i => new FieldError(i.Target, i.Message)).ToList();

        if (errors.Count == 0 && (await StressAccess.ScenarioAsync(store, projectId, profile.ScenarioId, ct)).Document is null)
            errors.Add(new FieldError("scenarioId", "The scenario does not exist in this project."));

        return errors.Count == 0
            ? (profile, null)
            : (profile, Results.BadRequest(new ErrorResponse("validation_error", "Validation failed", Errors: errors)));
    }
}
