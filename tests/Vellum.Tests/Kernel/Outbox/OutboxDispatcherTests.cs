using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Vellum.Kernel.EventStore;
using Vellum.Kernel.Outbox;

namespace Vellum.Tests.Kernel.Outbox;

[Collection("Integration")]
public class OutboxDispatcherTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;

    public OutboxDispatcherTests(IntegrationFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var db = CreateDb();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM es.outbox_messages");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM es.dead_letters");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private EventStoreDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<EventStoreDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new EventStoreDbContext(options);
    }

    private async Task SeedMessage(string eventType = "test.event.v1", int retryCount = 0)
    {
        await using var db = CreateDb();
        db.OutboxMessages.Add(new OutboxMessageEntity
        {
            EventType = eventType,
            Payload = JsonSerializer.SerializeToDocument(new { value = 1 }),
            RetryCount = retryCount,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Dispatches_pending_message_and_marks_processed()
    {
        await SeedMessage();
        var dispatched = new List<string>();

        await using var db = CreateDb();
        var dispatcher = new OutboxDispatcher(
            new TestServiceScopeFactory(() => CreateDb()),
            NullLogger<OutboxDispatcher>.Instance);

        await dispatcher.ProcessBatchAsync(
            msg => { dispatched.Add(msg.EventType); return Task.CompletedTask; },
            CancellationToken.None);

        Assert.Contains("test.event.v1", dispatched);

        await using var verifyDb = CreateDb();
        var remaining = await verifyDb.OutboxMessages
            .Where(m => m.ProcessedAt == null)
            .CountAsync();
        Assert.Equal(0, remaining);
    }

    [Fact]
    public async Task Failed_dispatch_increments_retry_and_sets_next_retry()
    {
        await SeedMessage();

        await using var db = CreateDb();
        var dispatcher = new OutboxDispatcher(
            new TestServiceScopeFactory(() => CreateDb()),
            NullLogger<OutboxDispatcher>.Instance);

        await dispatcher.ProcessBatchAsync(
            _ => throw new Exception("Dispatch failed"),
            CancellationToken.None);

        await using var verifyDb = CreateDb();
        var msg = await verifyDb.OutboxMessages
            .Where(m => m.ProcessedAt == null)
            .FirstAsync();
        Assert.Equal(1, msg.RetryCount);
        Assert.NotNull(msg.NextRetryAt);
    }

    [Fact]
    public async Task Message_moved_to_dead_letters_after_max_retries()
    {
        await SeedMessage(retryCount: 4); // Will become 5th failure

        await using var db = CreateDb();
        var dispatcher = new OutboxDispatcher(
            new TestServiceScopeFactory(() => CreateDb()),
            NullLogger<OutboxDispatcher>.Instance);

        await dispatcher.ProcessBatchAsync(
            _ => throw new Exception("Still failing"),
            CancellationToken.None);

        await using var verifyDb = CreateDb();
        var deadLetters = await verifyDb.DeadLetters.CountAsync();
        Assert.True(deadLetters > 0);

        var remaining = await verifyDb.OutboxMessages
            .Where(m => m.ProcessedAt == null)
            .CountAsync();
        Assert.Equal(0, remaining);
    }

    private sealed class TestServiceScopeFactory : IServiceScopeFactory
    {
        private readonly Func<EventStoreDbContext> _dbFactory;

        public TestServiceScopeFactory(Func<EventStoreDbContext> dbFactory) => _dbFactory = dbFactory;

        public IServiceScope CreateScope() => new TestScope(_dbFactory);

        private sealed class TestScope : IServiceScope
        {
            private readonly EventStoreDbContext _db;
            public IServiceProvider ServiceProvider { get; }

            public TestScope(Func<EventStoreDbContext> dbFactory)
            {
                _db = dbFactory();
                ServiceProvider = new TestServiceProvider(_db);
            }

            public void Dispose() => _db.Dispose();
        }

        private sealed class TestServiceProvider : IServiceProvider
        {
            private readonly EventStoreDbContext _db;
            public TestServiceProvider(EventStoreDbContext db) => _db = db;
            public object? GetService(Type serviceType) =>
                serviceType == typeof(EventStoreDbContext) ? _db : null;
        }
    }
}
