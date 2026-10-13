namespace Vellum.Modules.Promises;

/// <summary>
/// The hash of everything a person approves when they approve a revision: the requirement
/// ID, the revision number, whether it retires (and which revision), every field and link of
/// the content, and every obligation with its recipe. The server computes it and returns it
/// on every read; an approval names it, so an approval binds exactly the content the approver
/// saw. A member that is null is left out, so a member added later does not change the hash
/// of content that was approved before it existed.
/// </summary>
public static class PromiseContentHash
{
    public static string Of(string? requirementId, PromiseRevisionState revision) => PromiseJson.StableHash(new
    {
        requirementId,
        revision = revision.Revision,
        retires = revision.Retires,
        retiresRevision = revision.RetiresRevision,
        content = revision.Content,
        obligations = revision.Obligations.Select(o => new { o.Id, o.Number, o.Kind, o.Title, o.Recipe, o.Source }),
    });
}
