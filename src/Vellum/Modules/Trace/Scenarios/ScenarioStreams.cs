using Deedbox;
using Vellum.Shared;

namespace Vellum.Modules.Trace.Scenarios;

/// <summary>
/// Every stream type shares one space of stream IDs, and a scenario ID comes from the caller.
/// An ID that names a model or a schema stream is not a scenario: a read finds nothing, and
/// a write is told so. Deedbox reports the mismatch as DBX008.
/// </summary>
internal static class ScenarioStreams
{
    public static async Task<ScenarioState> LoadAsync(IEventStore store, Guid scenarioId, CancellationToken ct)
    {
        try
        {
            return (await store.Load<ScenarioState>(StreamId.From(scenarioId), ct)).State;
        }
        catch (DeedboxException e) when (e.Code == DeedboxError.StreamTypeMismatch)
        {
            return ScenarioState.Initial;
        }
    }

    /// <summary>Runs a decision; <paramref name="otherStreamType"/> is the result when the ID is not a scenario's.</summary>
    public static async Task<CommandResult<TResult>> DecideAsync<TResult>(
        IEventStore store, Guid scenarioId,
        Func<ScenarioState, CommandResult<IReadOnlyList<object>>> decide,
        Func<ScenarioState, TResult> read,
        CommandResult<TResult> otherStreamType,
        CancellationToken ct)
    {
        try
        {
            return await store.Decide(scenarioId, decide, read, ct);
        }
        catch (DeedboxException e) when (e.Code == DeedboxError.StreamTypeMismatch)
        {
            return otherStreamType;
        }
    }
}
