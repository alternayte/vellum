using Vellum.Kernel.Aggregates;

namespace Vellum.Tests.Kernel.Aggregates;

public abstract record CounterEvent
{
    public sealed record Incremented : CounterEvent;
    public sealed record Decremented(int Amount) : CounterEvent;

    private CounterEvent() { }
}

public sealed record CounterState(int Value) : IAggregateState<CounterState, CounterEvent>
{
    public static CounterState Initial => new(0);

    public CounterState Evolve(CounterEvent @event) => @event switch
    {
        CounterEvent.Incremented => this with { Value = Value + 1 },
        CounterEvent.Decremented d => this with { Value = Value - d.Amount },
        _ => throw new InvalidOperationException($"Unknown event: {@event.GetType().Name}")
    };
}

public static class CounterDecider
{
    public static IReadOnlyList<CounterEvent> Decide(CounterState state, CounterCommand command) =>
        command switch
        {
            CounterCommand.Increment => [new CounterEvent.Incremented()],
            CounterCommand.Decrement d when state.Value >= d.Amount => [new CounterEvent.Decremented(d.Amount)],
            CounterCommand.Decrement => throw new InvalidOperationException("Cannot decrement below zero"),
            _ => throw new InvalidOperationException($"Unknown command: {command.GetType().Name}")
        };
}

public abstract record CounterCommand
{
    public sealed record Increment : CounterCommand;
    public sealed record Decrement(int Amount) : CounterCommand;

    private CounterCommand() { }
}
