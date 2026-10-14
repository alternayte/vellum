using Deedbox.Testing;
using Vellum.Modules.Modelling;
using Vellum.Modules.Promises;
using Vellum.Modules.Review;
using Vellum.Modules.Schemas;
using Vellum.Modules.Trace.Scenarios;

namespace Vellum.Tests;

public class EventContractTests
{
    /// <summary>
    /// Pins every stored event name and shape in <c>events.lock</c>. A rename without an alias,
    /// a removed event or an incompatible shape change fails here instead of on a stored stream.
    /// </summary>
    [Fact]
    public void Event_contracts_are_stable() =>
        EventContracts.Verify(deedbox => deedbox.AddModelStreams().AddSchemaStreams().AddScenarioStreams().AddPromiseStreams().AddDecisionStreams(), "events.lock");
}
