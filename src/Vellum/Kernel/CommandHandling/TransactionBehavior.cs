using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.EventStore;
using Vellum.Kernel.Projections;

namespace Vellum.Kernel.CommandHandling;

public sealed class TransactionBehavior<TCommand, TResult> : ICommandHandler<TCommand, TResult>
{
    private readonly ICommandHandler<TCommand, TResult> _inner;
    private readonly EventStoreDbContext _db;
    private readonly EventCollector _collector;
    private readonly IEnumerable<IInlineProjection> _projections;

    public TransactionBehavior(
        ICommandHandler<TCommand, TResult> inner,
        EventStoreDbContext db,
        EventCollector collector,
        IEnumerable<IInlineProjection> projections)
    {
        _inner = inner;
        _db = db;
        _collector = collector;
        _projections = projections;
    }

    public async Task<TResult> HandleAsync(TCommand command, CancellationToken ct = default)
    {
        _collector.Clear();

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var result = await _inner.HandleAsync(command, ct);

            var events = _collector.Events;
            foreach (var projection in _projections)
                await projection.ProjectAsync(events, ct);

            await transaction.CommitAsync(ct);

            if (events.Count > 0)
                await _db.Database.ExecuteSqlRawAsync("NOTIFY new_events", ct);

            return result;
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }
}
