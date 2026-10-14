using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Import;
using Vellum.Modules.Modelling;
using Vellum.Modules.Settings.Document;
using Vellum.Shared;

namespace Vellum.Modules.Ai.Design;

/// <summary>What may not leave the instance in a prompt of this feature, for one request.</summary>
/// <param name="Ids">The private-sourced things of the branch: elements, contracts, messages and schemas.</param>
/// <param name="MaySend">The workspace's effective <c>privacy.sendPrivateCodeToAi</c>.</param>
internal sealed record PrivateGate(IReadOnlySet<Guid> Ids, bool MaySend)
{
    public const string Setting = AiFailure.Disabled.PrivateCodeSetting;

    /// <summary>True when the thing must be left out of every prompt.</summary>
    public bool Hides(Guid id) => !MaySend && Ids.Contains(id);

    /// <summary>The class of a prompt part that holds text of the things in <paramref name="included"/>.</summary>
    public AiDataClass ClassOf(IEnumerable<Guid> included) =>
        included.Any(Ids.Contains) ? AiDataClass.PrivateRepository : AiDataClass.Public;
}

/// <summary>Why a response holds less than the model: things were left out of the prompt.</summary>
/// <param name="Reason">The setting that forbids them: <c>privacy.sendPrivateCodeToAi</c>.</param>
public sealed record OmittedPrivate(int Count, string Reason)
{
    internal static OmittedPrivate? Of(int count) => count > 0 ? new OmittedPrivate(count, PrivateGate.Setting) : null;
}

/// <summary>
/// Finds the private-sourced things of a project. A thing is private-sourced when its text
/// came from a private repository: the import module holds a link (<c>import.fact_links</c>)
/// from a fact of a repository to the thing, the scan of that link
/// (<c>import.scans.repository_is_private</c>) did not record the repository as public, and
/// the thing is not a declared fact. A scan that could not tell (null) counts as private. A
/// declared fact is one a person typed, or confirmed as their own statement, so it is never
/// private-sourced, whatever links to it. A schema has no truth label; a link alone decides.
/// This class is the only place of the feature that reads the import module's tables.
/// </summary>
public sealed class PrivateSources(AppDbContext import, SettingsResolver settings)
{
    internal async Task<PrivateGate> LoadAsync(Guid workspaceId, Guid projectId, ModelState model, CancellationToken ct)
    {
        var linked = await (
            from link in import.FactLinks.AsNoTracking()
            join scan in import.Scans.AsNoTracking() on link.ScanId equals scan.Id
            where link.ProjectId == projectId && scan.RepositoryIsPrivate != false
            select link.EntityId).Distinct().ToListAsync(ct);

        var ids = linked.Where(id => StatusOf(model, id) != FactStatus.Declared).ToHashSet();
        var workspace = await settings.GetWorkspaceAsync(workspaceId, ct);
        return new PrivateGate(ids, workspace.Effective.Privacy.SendPrivateCodeToAi);
    }

    /// <summary>The truth label of a thing of the model. Null for an ID that the model stream does not hold, such as a schema.</summary>
    private static FactStatus? StatusOf(ModelState model, Guid id) =>
        model.Elements.TryGetValue(id, out var element) ? element.FactStatus
        : model.Operations.TryGetValue(id, out var operation) ? operation.FactStatus
        : model.Channels.TryGetValue(id, out var channel) ? channel.FactStatus
        : model.Subscriptions.TryGetValue(id, out var subscription) ? subscription.FactStatus
        : model.Bindings.TryGetValue(id, out var binding) ? binding.FactStatus
        : model.Messages.TryGetValue(id, out var message) ? message.FactStatus
        : null;
}
