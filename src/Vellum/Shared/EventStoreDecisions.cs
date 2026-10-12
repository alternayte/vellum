using Deedbox;

namespace Vellum.Shared;

/// <summary>
/// Runs a decider that reports failure as a <see cref="CommandResult{T}"/> inside
/// <see cref="IEventStore.Execute{TState}"/>. The stream stays locked from load to commit, so
/// two writers of one stream take turns. A failed decision appends nothing.
/// </summary>
public static class EventStoreDecisions
{
    public static async Task<CommandResult<TResult>> Decide<TState, TResult>(
        this IEventStore store,
        Guid streamId,
        Func<TState, CommandResult<IReadOnlyList<object>>> decide,
        Func<TState, TResult> read,
        CancellationToken ct)
        where TState : IState<TState>
    {
        var (outcome, state) = await Run(store, streamId, decide, ct);
        return outcome switch
        {
            CommandResult<IReadOnlyList<object>>.Success => new CommandResult<TResult>.Success(read(state)),
            CommandResult<IReadOnlyList<object>>.Invalid invalid => new CommandResult<TResult>.Invalid(invalid.Errors),
            CommandResult<IReadOnlyList<object>>.NotFound notFound => new CommandResult<TResult>.NotFound(notFound.Message),
            CommandResult<IReadOnlyList<object>>.Conflict conflict => new CommandResult<TResult>.Conflict(conflict.Message),
            _ => throw new InvalidOperationException($"Unknown decision result {outcome.GetType().Name}"),
        };
    }

    public static async Task<CommandResult> Decide<TState>(
        this IEventStore store,
        Guid streamId,
        Func<TState, CommandResult<IReadOnlyList<object>>> decide,
        CancellationToken ct)
        where TState : IState<TState>
    {
        var (outcome, _) = await Run(store, streamId, decide, ct);
        return outcome switch
        {
            CommandResult<IReadOnlyList<object>>.Success => new CommandResult.Success(),
            CommandResult<IReadOnlyList<object>>.Invalid invalid => new CommandResult.Invalid(invalid.Errors),
            CommandResult<IReadOnlyList<object>>.NotFound notFound => new CommandResult.NotFound(notFound.Message),
            CommandResult<IReadOnlyList<object>>.Conflict conflict => new CommandResult.Conflict(conflict.Message),
            _ => throw new InvalidOperationException($"Unknown decision result {outcome.GetType().Name}"),
        };
    }

    private static async Task<(CommandResult<IReadOnlyList<object>> Outcome, TState State)> Run<TState>(
        IEventStore store,
        Guid streamId,
        Func<TState, CommandResult<IReadOnlyList<object>>> decide,
        CancellationToken ct)
        where TState : IState<TState>
    {
        // A unit with several streams locks them all before its first append (see
        // SharedTransaction), so it does not deadlock with this write. The cases that remain are
        // rare: an import that waits behind a Deedbox cut-over, or the first use of an event
        // type. Postgres then aborts one side. Nothing was written, so the decision runs again.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // Execute runs the decision again after a stream-creation race, so the last run wins.
                CommandResult<IReadOnlyList<object>> outcome = null!;
                var executed = await store.Execute<TState>(StreamId.From(streamId), state =>
                {
                    outcome = decide(state);
                    return outcome is CommandResult<IReadOnlyList<object>>.Success success ? success.Value : [];
                }, ct);

                return (outcome, executed.State);
            }
            catch (Exception ex) when (attempt < MaxAttempts && ConcurrencyExceptionHandler.IsDeadlock(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25 * (1 << attempt)), ct);
            }
        }
    }

    private const int MaxAttempts = 5;
}
