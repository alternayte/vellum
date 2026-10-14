using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Drafts.Merge;

/// <summary>
/// A merge builds its events from a comparison of three models, not from a decision on main.
/// So the events are held to the model's rules as a whole (<see cref="ModelInvariants"/>)
/// before anything is written.
/// </summary>
public static class MergedModel
{
    /// <summary>
    /// Null when every event fits main and the merged model breaks no rule that main did not
    /// already break. Otherwise the reason, and the merge must write nothing.
    /// </summary>
    public static string? Refusal(ModelState main, IReadOnlyList<object> events)
    {
        var (merged, misfit) = ModelInvariants.Apply(main, events);
        if (misfit is not null)
            return $"The merge would write a change that main cannot take: {misfit}. Change the resolutions or the draft.";

        var broken = ModelInvariants.NewViolations(main, merged);
        return broken.Count == 0
            ? null
            : $"The merge would leave main breaking a rule of the model: {string.Join("; ", broken)}. Change the resolutions or the draft.";
    }
}
