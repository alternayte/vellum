namespace Vellum.Kernel.Aggregates;

public interface IAggregateState<TSelf, TEvent>
    where TSelf : IAggregateState<TSelf, TEvent>
{
    static abstract TSelf Initial { get; }
    TSelf Evolve(TEvent @event);
}
