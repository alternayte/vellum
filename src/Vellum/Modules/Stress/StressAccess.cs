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

/// <summary>
/// How many profiles and runs a project keeps and how often a workspace may run. How many runs
/// the instance does at the same time is not here: that is the instance policy
/// <c>maxConcurrentStressRuns</c>, which the admin changes in the app.
/// </summary>
/// <param name="MaxRunsPerProfile">A profile keeps its newest runs. An older run is deleted when a new one is stored.</param>
public sealed record StressRunPolicy(
    int MaxConcurrentPerWorkspace = 2,
    int RunsPerMinutePerWorkspace = 30,
    int MaxRunsPerProfile = 20,
    int MaxProfilesPerProject = 50);

/// <summary>Why a run got no place. <paramref name="Code"/> is <c>busy</c> when the instance is full and <c>rate_limited</c> when the workspace is.</summary>
public sealed record StressGateRefusal(string Code, TimeSpan RetryAfter);

/// <summary>
/// The stress runs in progress: of each workspace, with the runs it started in the last
/// minute, and of the whole instance. A run holds a thread for up to
/// <see cref="StressLimits.WallClock"/>, so one workspace cannot take all the places of the
/// instance, and all workspaces together cannot take more than the instance allows. A place
/// is free again when the run ends, in any way.
/// </summary>
public sealed class StressRunGate(StressRunPolicy policy) : IDisposable
{
    /// <summary>A run ends within the real-time limit. This is a short wait that a second try can meet.</summary>
    private static readonly TimeSpan BusyRetry = TimeSpan.FromSeconds(5);

    private readonly PartitionedRateLimiter<Guid> _concurrent = PartitionedRateLimiter.Create<Guid, Guid>(
        workspaceId => RateLimitPartition.GetConcurrencyLimiter(workspaceId, _ => new ConcurrencyLimiterOptions
        {
            PermitLimit = policy.MaxConcurrentPerWorkspace,
            QueueLimit = 0,
        }));

    // The runs of all workspaces that hold a place. The limit comes with each call, so a
    // change of the instance policy applies to the next run with no restart. Runs that hold a
    // place when the limit goes down keep it.
    private int _running;

    private readonly PartitionedRateLimiter<Guid> _started = PartitionedRateLimiter.Create<Guid, Guid>(
        workspaceId => RateLimitPartition.GetFixedWindowLimiter(workspaceId, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = policy.RunsPerMinutePerWorkspace,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));

    /// <summary>A place for one run, or null with the reason and the time to wait.</summary>
    /// <param name="maxConcurrentPerInstance">
    /// Runs of all workspaces together. One run holds a thread for up to <see cref="StressLimits.WallClock"/>
    /// and the memory that <see cref="StressLimits.MaxQueued"/> and <see cref="StressLimits.MaxPendingEvents"/> allow.
    /// </param>
    public IDisposable? TryEnter(Guid workspaceId, int maxConcurrentPerInstance, out StressGateRefusal? refusal)
    {
        refusal = null;
        var workspace = _concurrent.AttemptAcquire(workspaceId);
        if (!workspace.IsAcquired)
        {
            workspace.Dispose();
            refusal = new("rate_limited", TimeSpan.FromSeconds(1));
            return null;
        }

        if (!TryTakeInstancePlace(maxConcurrentPerInstance))
        {
            workspace.Dispose();
            refusal = new("busy", BusyRetry);
            return null;
        }

        // Last, because a start that is counted cannot be given back.
        using var started = _started.AttemptAcquire(workspaceId);
        if (started.IsAcquired)
            return new Place(workspace, this);

        refusal = new("rate_limited", started.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : TimeSpan.FromSeconds(1));
        Interlocked.Decrement(ref _running);
        workspace.Dispose();
        return null;
    }

    private bool TryTakeInstancePlace(int limit)
    {
        while (true)
        {
            var running = Volatile.Read(ref _running);
            if (running >= limit)
                return false;
            if (Interlocked.CompareExchange(ref _running, running + 1, running) == running)
                return true;
        }
    }

    public void Dispose()
    {
        _concurrent.Dispose();
        _started.Dispose();
    }

    private sealed class Place(RateLimitLease workspace, StressRunGate gate) : IDisposable
    {
        private int _left;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _left, 1) != 0)
                return;
            Interlocked.Decrement(ref gate._running);
            workspace.Dispose();
        }
    }
}

/// <summary>The scenario and the model of one run, read as of one instant, with the versions that pin them.</summary>
internal sealed record StressWorld(TraceModel Model, long ModelVersion, Scenario? Scenario, long ScenarioVersion);

/// <summary>What the stress handlers share: who calls, how a body is read and how the model is pinned.</summary>
internal static class StressAccess
{
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
        IEventStore store, AppDbContext db, Guid projectId, Guid streamId, Guid scenarioId, CancellationToken ct)
    {
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var pinned = store.UseDbContext(db);

        var (scenario, scenarioVersion) = await ScenarioAsync(pinned, projectId, scenarioId, ct);
        var (model, modelVersion) = await pinned.Load<ModelState>(StreamId.From(streamId), ct);

        // Only the project's own schemas count, as in a trace run.
        var referenced = ContractDecider.ReferencedSchemaIds(model);
        var schemas = await db.Schemas.AsNoTracking()
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
