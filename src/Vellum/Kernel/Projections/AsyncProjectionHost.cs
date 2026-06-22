using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vellum.Kernel.EventStore;
using Vellum.Kernel.EventTypes;

namespace Vellum.Kernel.Projections;

public sealed class AsyncProjectionHost : BackgroundService
{
    private const int BatchSize = 200;
    private const int MaxRetriesPerEvent = 3;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReadOnlyList<IAsyncProjection> _projections;
    private readonly IEventTypeRegistry _registry;
    private readonly ILogger<AsyncProjectionHost> _logger;

    public AsyncProjectionHost(
        IServiceScopeFactory scopeFactory,
        IEnumerable<IAsyncProjection> projections,
        IEventTypeRegistry registry,
        ILogger<AsyncProjectionHost> logger)
    {
        _scopeFactory = scopeFactory;
        _projections = projections.ToList();
        _registry = registry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = await ProcessBatchAsync(stoppingToken);
            if (processed == 0)
                await Task.Delay(PollInterval, stoppingToken);
        }
    }

    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        var totalProcessed = 0;

        foreach (var projection in _projections)
        {
            totalProcessed += await ProcessProjectionAsync(projection, ct);
        }

        return totalProcessed;
    }

    private async Task<int> ProcessProjectionAsync(IAsyncProjection projection, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EventStoreDbContext>();

        var checkpoint = await db.Checkpoints.FindAsync([projection.Name], ct);
        var lastPosition = checkpoint?.LastProcessedPosition ?? 0;

        // Gap-safe query: only read events from committed transactions
        var events = await db.Events
            .FromSqlInterpolated(
                $"""
                SELECT stream_id, version, global_position, event_type, payload, metadata, occurred_at
                FROM es.events
                WHERE global_position > {lastPosition}
                  AND xid < pg_snapshot_xmin(pg_current_snapshot())
                ORDER BY global_position
                LIMIT {BatchSize}
                """)
            .AsNoTracking()
            .ToListAsync(ct);

        if (events.Count == 0)
            return 0;

        foreach (var eventEntity in events)
        {
            var data = _registry.DeserializeEvent(eventEntity.EventType, eventEntity.Payload);
            var persisted = new PersistedEvent(
                eventEntity.StreamId,
                eventEntity.Version,
                eventEntity.GlobalPosition,
                eventEntity.EventType,
                data,
                eventEntity.OccurredAt);

            var retries = 0;
            while (true)
            {
                try
                {
                    await projection.ProjectAsync(persisted, ct);
                    break;
                }
                catch (Exception ex) when (++retries <= MaxRetriesPerEvent)
                {
                    _logger.LogWarning(ex,
                        "Projection {Name} failed on event at position {Position} (attempt {Attempt}/{Max})",
                        projection.Name, eventEntity.GlobalPosition, retries, MaxRetriesPerEvent);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Projection {Name} skipping event at position {Position} after {Max} retries",
                        projection.Name, eventEntity.GlobalPosition, MaxRetriesPerEvent);
                    break;
                }
            }

            // Advance checkpoint even if the event was skipped (to not block the projection)
            if (checkpoint is null)
            {
                checkpoint = new CheckpointEntity
                {
                    ProjectionName = projection.Name,
                    LastProcessedPosition = eventEntity.GlobalPosition,
                };
                db.Checkpoints.Add(checkpoint);
            }
            else
            {
                checkpoint.LastProcessedPosition = eventEntity.GlobalPosition;
                checkpoint.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        await db.SaveChangesAsync(ct);
        return events.Count;
    }
}
