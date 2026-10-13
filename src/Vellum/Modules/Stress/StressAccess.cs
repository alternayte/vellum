using System.Data;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Deedbox;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vellum.Modules.Modelling;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Schemas;
using Vellum.Modules.Stress.Engine;
using Vellum.Modules.Trace.Engine;
using Vellum.Modules.Trace.Scenarios;
using Vellum.Shared;

namespace Vellum.Modules.Stress;

/// <summary>How many profiles and runs a project keeps, and how often a workspace may run.</summary>
/// <param name="MaxRunsPerProfile">A profile keeps its newest runs. An older run is deleted when a new one is stored.</param>
public sealed record StressRunPolicy(
    int MaxConcurrentPerWorkspace = 2,
    int RunsPerMinutePerWorkspace = 30,
    int MaxRunsPerProfile = 20,
    int MaxProfilesPerProject = 50);

/// <summary>
/// The stress runs that a workspace has in progress and has started in the last minute. A run
/// holds a thread for up to <see cref="StressLimits.WallClock"/>, so one workspace cannot take
/// all of them. A place is free again when the run ends, in any way.
/// </summary>
public sealed class StressRunGate(StressRunPolicy policy) : IDisposable
{
    private readonly PartitionedRateLimiter<Guid> _concurrent = PartitionedRateLimiter.Create<Guid, Guid>(
        workspaceId => RateLimitPartition.GetConcurrencyLimiter(workspaceId, _ => new ConcurrencyLimiterOptions
        {
            PermitLimit = policy.MaxConcurrentPerWorkspace,
            QueueLimit = 0,
        }));

    private readonly PartitionedRateLimiter<Guid> _started = PartitionedRateLimiter.Create<Guid, Guid>(
        workspaceId => RateLimitPartition.GetFixedWindowLimiter(workspaceId, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = policy.RunsPerMinutePerWorkspace,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));

    /// <summary>A place for one run, or null with the time to wait.</summary>
    public IDisposable? TryEnter(Guid workspaceId, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.FromSeconds(1);
        var place = _concurrent.AttemptAcquire(workspaceId);
        if (!place.IsAcquired)
        {
            place.Dispose();
            return null;
        }

        using var started = _started.AttemptAcquire(workspaceId);
        if (started.IsAcquired)
            return place;

        if (started.TryGetMetadata(MetadataName.RetryAfter, out var wait))
            retryAfter = wait;
        place.Dispose();
        return null;
    }

    public void Dispose()
    {
        _concurrent.Dispose();
        _started.Dispose();
    }
}

/// <summary>The scenario and the model of one run, read as of one instant, with the versions that pin them.</summary>
internal sealed record StressWorld(TraceModel Model, long ModelVersion, Scenario? Scenario, long ScenarioVersion);

/// <summary>What the stress handlers share: who calls, how a body is read and how the model is pinned.</summary>
internal static class StressAccess
{
    public static string UserId(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public static IResult NotFound(string what) => Results.NotFound(new ErrorResponse("not_found", $"{what} not found"));

    /// <summary>
    /// Reads a JSON body with the app's options. A body that is not the expected document gets
    /// 400 with the error envelope and the reader's message, not a bare status.
    /// </summary>
    public static async Task<(T? Body, IResult? Error)> ReadAsync<T>(HttpContext http) where T : class
    {
        var options = http.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        using var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, http.RequestAborted);
        if (buffer.Length == 0)
            return (null, Results.BadRequest(new ErrorResponse("invalid_body", "A JSON body is required")));

        try
        {
            return JsonSerializer.Deserialize<T>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), options) is { } body
                ? (body, null)
                : (null, Results.BadRequest(new ErrorResponse("invalid_body", "The body is null")));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return (null, Results.BadRequest(new ErrorResponse("invalid_body", "The body is not the expected JSON document", ex.Message)));
        }
    }

    /// <summary>
    /// One snapshot: the scenario, the branch's model and the project's schemas are read as of
    /// the same instant, as a trace run reads them. The transaction only reads and is rolled back.
    /// </summary>
    public static async Task<StressWorld> LoadAsync(
        IEventStore store, SchemasDbContext schemasDb, Guid projectId, Guid streamId, Guid scenarioId, CancellationToken ct)
    {
        await using var snapshot = await schemasDb.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var pinned = store.UseDbContext(schemasDb);

        var (scenario, scenarioVersion) = await ScenarioAsync(pinned, projectId, scenarioId, ct);
        var (model, modelVersion) = await pinned.Load<ModelState>(StreamId.From(streamId), ct);

        // Only the project's own schemas count, as in a trace run.
        var referenced = ContractDecider.ReferencedSchemaIds(model);
        var schemas = await schemasDb.Schemas.AsNoTracking()
            .Where(s => s.ProjectId == projectId && referenced.Contains(s.Id))
            .Select(s => new { s.Id, s.Content })
            .ToListAsync(ct);

        return new StressWorld(
            TraceModelBuilder.Build(model, schemas.ToDictionary(s => new SchemaId(s.Id), s => s.Content)),
            modelVersion, scenario, scenarioVersion);
    }

    /// <summary>
    /// The scenario of the project and the version of its stream. Every stream type shares one
    /// space of IDs, so an ID that names another kind of stream, or a scenario of another
    /// project, is no scenario here.
    /// </summary>
    public static async Task<(Scenario? Document, long Version)> ScenarioAsync(
        IEventStore store, Guid projectId, Guid scenarioId, CancellationToken ct)
    {
        try
        {
            var (state, version) = await store.Load<ScenarioState>(StreamId.From(scenarioId), ct);
            return (state.DocumentIn(projectId), version);
        }
        catch (DeedboxException e) when (e.Code == DeedboxError.StreamTypeMismatch)
        {
            return (null, 0);
        }
    }
}
