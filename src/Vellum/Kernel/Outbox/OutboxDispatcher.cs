using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vellum.Kernel.EventStore;

namespace Vellum.Kernel.Outbox;

public sealed class OutboxDispatcher : BackgroundService
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(25),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
    ];

    private const int MaxRetries = 5;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxDispatcher> _logger;

    public OutboxDispatcher(IServiceScopeFactory scopeFactory, ILogger<OutboxDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ProcessBatchAsync(DefaultDispatch, stoppingToken);
            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private static Task DefaultDispatch(OutboxMessageEntity message) => Task.CompletedTask;

    public async Task ProcessBatchAsync(
        Func<OutboxMessageEntity, Task> dispatch,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EventStoreDbContext>();

        var now = DateTimeOffset.UtcNow;
        var messages = await db.OutboxMessages
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM es.outbox_messages
                WHERE processed_at IS NULL
                  AND (next_retry_at IS NULL OR next_retry_at <= {now})
                ORDER BY id
                LIMIT 100
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);

        foreach (var message in messages)
        {
            try
            {
                await dispatch(message);
                message.ProcessedAt = DateTimeOffset.UtcNow;
            }
            catch (Exception ex)
            {
                message.RetryCount++;

                if (message.RetryCount >= MaxRetries)
                {
                    db.DeadLetters.Add(new DeadLetterEntity
                    {
                        EventType = message.EventType,
                        Payload = message.Payload,
                        Error = ex.ToString(),
                    });
                    db.OutboxMessages.Remove(message);
                    _logger.LogError(ex, "Outbox message {Id} moved to dead letters after {MaxRetries} retries",
                        message.Id, MaxRetries);
                }
                else
                {
                    var delay = RetryDelays[Math.Min(message.RetryCount - 1, RetryDelays.Length - 1)];
                    message.NextRetryAt = DateTimeOffset.UtcNow + delay;
                    _logger.LogWarning(ex, "Outbox message {Id} failed (retry {Count}/{Max}), next retry at {NextRetry}",
                        message.Id, message.RetryCount, MaxRetries, message.NextRetryAt);
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
