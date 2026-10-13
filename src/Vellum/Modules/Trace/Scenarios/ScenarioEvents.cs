using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Modules.Trace.Scenarios;

/// <summary>
/// Where a scenario document came from when a person did not write it (SDD guarantee 2).
/// </summary>
/// <param name="Kind"><c>ai-proposal</c> (the in-app assistant generated the flow proposal) or <c>submitted-proposal</c> (an agent token or a caller of the API submitted it).</param>
/// <param name="ProposalId">The flow proposal that a person accepted.</param>
/// <param name="Provider">The provider kind that wrote the proposal, for example <c>anthropic</c>. For a submitted proposal: <c>agent-token</c> or <c>user</c>.</param>
/// <param name="Model">The model name from the workspace settings. For a submitted proposal: the ID of the token or of the user.</param>
public sealed record ScenarioOrigin(string Kind, Guid ProposalId, string Provider, string Model)
{
    public const string AiProposal = "ai-proposal";
    public const string SubmittedProposal = "submitted-proposal";
}

/// <summary>The events of a scenario stream. Plain records; the stream registers every nested type.</summary>
public static class ScenarioEvent
{
    /// <summary>
    /// The whole document as the author saved it. A scenario is saved whether or not it is
    /// valid against the model; validity is a result of a run, not a condition of saving.
    /// </summary>
    /// <param name="Origin">
    /// Set when the saved document came from an accepted AI flow proposal. Null for a save
    /// that a person made, and in every event written before the member existed.
    /// </param>
    public sealed record ScenarioSaved(Guid ProjectId, string Name, Scenario Document, ScenarioOrigin? Origin = null);

    public sealed record ScenarioDeleted(ScenarioId ScenarioId);
}
