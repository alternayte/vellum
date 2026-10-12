using System.Text.Json;
using System.Text.Json.Serialization;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Trace.Engine;

/// <summary>
/// A declared behaviour: ordered steps over stable contract IDs, the faults to inject and
/// the results that must hold. Steps reference contracts, never free text (SDD guarantee 3).
/// </summary>
public sealed record Scenario(
    ScenarioId Id,
    string Name,
    IReadOnlyList<Step> Steps,
    IReadOnlyList<Fault> Faults,
    IReadOnlyList<Assertion> Assertions);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CallStep), "call")]
[JsonDerivedType(typeof(PublishStep), "publish")]
[JsonDerivedType(typeof(WriteStep), "write")]
[JsonDerivedType(typeof(ParallelStep), "parallel")]
[JsonDerivedType(typeof(BranchStep), "branch")]
public abstract record Step(string Id);

/// <summary>
/// A synchronous request. <paramref name="Then"/> runs inside the callee before it responds;
/// if it fails, the attempt fails. <paramref name="OnError"/> runs in the caller once every
/// attempt has failed, and handles the failure.
/// </summary>
public sealed record CallStep(
    string Id, ElementId From, OperationId Operation,
    JsonElement? Request, JsonElement? Response,
    IReadOnlyList<Step> Then, IReadOnlyList<Step> OnError) : Step(Id);

/// <summary>
/// A message published to a channel. Every subscription on the channel receives it;
/// <paramref name="Handlers"/> gives the steps each consumer runs. The publisher does not
/// wait for consumers and does not fail when one of them does.
/// </summary>
public sealed record PublishStep(
    string Id, ElementId From, ChannelId Channel, MessageId Message,
    JsonElement? Payload, IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<SubscriptionId, IReadOnlyList<Step>> Handlers) : Step(Id);

public sealed record WriteStep(
    string Id, ElementId By, ElementId Store, string Dataset, JsonElement? Change) : Step(Id);

/// <summary>Branches start at the same instant; the step ends when the slowest one does.</summary>
public sealed record ParallelStep(string Id, IReadOnlyList<IReadOnlyList<Step>> Branches) : Step(Id);

/// <summary>Chooses a path from the recorded outcome of an earlier step.</summary>
public sealed record BranchStep(
    string Id, string WhenStep, StepOutcome Is,
    IReadOnlyList<Step> Then, IReadOnlyList<Step> Else) : Step(Id);

public enum StepOutcome { Succeeded, Failed, Skipped }

public enum FaultKind { Error, Timeout, Outage }

/// <summary>
/// Makes attempts against a target fail. The target is an operation, a channel, a
/// subscription or an element (which fails everything that element serves).
/// <paramref name="FailFirst"/> limits the fault to the first n attempts; null fails all.
/// </summary>
public sealed record Fault(Guid Target, FaultKind Kind, int? FailFirst);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(OutcomeAssertion), "outcome")]
[JsonDerivedType(typeof(EventCountAssertion), "event-count")]
public abstract record Assertion(string Id);

/// <summary>
/// Checks the outcome a step had in the attempt that settled its enclosing call or delivery.
/// A step that ran only inside an attempt that was retried has no outcome.
/// </summary>
public sealed record OutcomeAssertion(string Id, string Step, StepOutcome Expected) : Assertion(Id);

/// <summary>
/// Counts trace events of one kind, optionally for one contract or element. It counts every
/// attempt, including retried and abandoned ones: that is how a duplicate shows.
/// </summary>
public sealed record EventCountAssertion(
    string Id, TraceEventKind Kind, Guid? Target, int Expected) : Assertion(Id);
