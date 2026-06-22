using Vellum.Kernel.CommandHandling;

namespace Vellum.Kernel.Projections;

public interface IInlineProjection
{
    Task ProjectAsync(IReadOnlyList<CollectedEvent> events, CancellationToken ct = default);
}
