using System.Collections.Immutable;
using Vellum.Modules.Drafts.Merge;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Tests.Modules.Drafts;

public class ThreeWayMergeTests
{
    private static readonly Guid Id1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Id2 = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Id3 = Guid.Parse("00000000-0000-0000-0000-000000000003");

    private static ElementState El(Guid id, string name, string? tech = null, ElementStatus status = ElementStatus.Current) =>
        new(id, ElementKind.System, name, null, tech, null, status, null, []);

    private static RelationshipState Rel(Guid id, Guid from, Guid to, string? label = null) =>
        new(id, from, to, label, null, null);

    private static ModelState State(
        IEnumerable<ElementState>? elements = null,
        IEnumerable<RelationshipState>? rels = null)
    {
        var elDict = (elements ?? []).ToImmutableDictionary(e => e.Id);
        var relDict = (rels ?? []).ToImmutableDictionary(r => r.Id);
        return new ModelState(elDict, relDict, ImmutableDictionary<Guid, MessageState>.Empty);
    }

    [Fact]
    public void Unchanged_ours_changed_theirs_takes_theirs()
    {
        var baseState = State([El(Id1, "API")]);
        var ours = State([El(Id1, "API")]);
        var theirs = State([El(Id1, "Gateway")]);

        var result = ThreeWayMerge.Compute(baseState, ours, theirs);

        Assert.Empty(result.Conflicts);
        Assert.Single(result.AutoResolved);
        Assert.Equal("modified", result.AutoResolved[0].ChangeKind);
        var resolved = (ElementState)result.AutoResolved[0].ResolvedValue!;
        Assert.Equal("Gateway", resolved.Name);
    }

    [Fact]
    public void Changed_ours_unchanged_theirs_keeps_ours()
    {
        var baseState = State([El(Id1, "API")]);
        var ours = State([El(Id1, "Gateway")]);
        var theirs = State([El(Id1, "API")]);

        var result = ThreeWayMerge.Compute(baseState, ours, theirs);

        Assert.Empty(result.Conflicts);
        Assert.Empty(result.AutoResolved);
    }

    [Fact]
    public void Both_changed_identically_auto_resolves()
    {
        var baseState = State([El(Id1, "API")]);
        var ours = State([El(Id1, "Gateway")]);
        var theirs = State([El(Id1, "Gateway")]);

        var result = ThreeWayMerge.Compute(baseState, ours, theirs);

        Assert.Empty(result.Conflicts);
        Assert.Empty(result.AutoResolved);
    }

    [Fact]
    public void Both_changed_differently_is_conflict()
    {
        var baseState = State([El(Id1, "API")]);
        var ours = State([El(Id1, "Gateway")]);
        var theirs = State([El(Id1, "Proxy")]);

        var result = ThreeWayMerge.Compute(baseState, ours, theirs);

        Assert.Single(result.Conflicts);
        Assert.Equal("both_modified", result.Conflicts[0].ConflictKind);
    }

    [Fact]
    public void Deleted_ours_modified_theirs_is_conflict()
    {
        var baseState = State([El(Id1, "API")]);
        var ours = State();
        var theirs = State([El(Id1, "Gateway")]);

        var result = ThreeWayMerge.Compute(baseState, ours, theirs);

        Assert.Single(result.Conflicts);
        Assert.Equal("delete_modify", result.Conflicts[0].ConflictKind);
    }

    [Fact]
    public void Modified_ours_deleted_theirs_is_conflict()
    {
        var baseState = State([El(Id1, "API")]);
        var ours = State([El(Id1, "Gateway")]);
        var theirs = State();

        var result = ThreeWayMerge.Compute(baseState, ours, theirs);

        Assert.Single(result.Conflicts);
        Assert.Equal("delete_modify", result.Conflicts[0].ConflictKind);
    }

    [Fact]
    public void Added_only_theirs_takes_theirs()
    {
        var baseState = State();
        var ours = State();
        var theirs = State([El(Id1, "New Service")]);

        var result = ThreeWayMerge.Compute(baseState, ours, theirs);

        Assert.Empty(result.Conflicts);
        Assert.Single(result.AutoResolved);
        Assert.Equal("added", result.AutoResolved[0].ChangeKind);
    }

    [Fact]
    public void Added_only_ours_keeps_ours_no_change()
    {
        var baseState = State();
        var ours = State([El(Id1, "New Service")]);
        var theirs = State();

        var result = ThreeWayMerge.Compute(baseState, ours, theirs);

        Assert.Empty(result.Conflicts);
        Assert.Empty(result.AutoResolved);
    }

    [Fact]
    public void Both_added_distinct_ids_keeps_both()
    {
        var baseState = State();
        var ours = State([El(Id1, "Service A")]);
        var theirs = State([El(Id2, "Service B")]);

        var result = ThreeWayMerge.Compute(baseState, ours, theirs);

        Assert.Empty(result.Conflicts);
        Assert.Single(result.AutoResolved);
        Assert.Equal(Id2, result.AutoResolved[0].EntityId);
    }

    [Fact]
    public void Orphaned_child_detected_as_conflict()
    {
        var parent = El(Id1, "Parent");
        var child = new ElementState(Id2, ElementKind.App, "Child", null, null, null,
            ElementStatus.Current, Id1, []);
        var baseState = State([parent, child]);
        var ours = State([child]); // parent deleted on ours
        var theirs = State([parent, child with { Name = "Updated Child" }]); // child modified on theirs

        var result = ThreeWayMerge.Compute(baseState, ours, theirs);

        Assert.Contains(result.Conflicts, c => c.ConflictKind == "orphaned_child");
    }

    [Fact]
    public void Dangling_relationship_detected_as_conflict()
    {
        var el1 = El(Id1, "A");
        var el2 = El(Id2, "B");
        var rel = Rel(Id3, Id1, Id2, "calls");
        var baseState = State([el1, el2], [rel]);
        var ours = State([el2], [rel]); // el1 deleted on ours
        var theirs = State([el1, el2], [rel with { Label = "invokes" }]); // rel modified on theirs

        var result = ThreeWayMerge.Compute(baseState, ours, theirs);

        Assert.Contains(result.Conflicts, c => c.ConflictKind == "dangling_relationship");
    }

    [Fact]
    public void Relationships_follow_same_merge_rules()
    {
        var el1 = El(Id1, "A");
        var el2 = El(Id2, "B");
        var relId = Id3;
        var rel = Rel(relId, Id1, Id2, "calls");
        var baseState = State([el1, el2], [rel]);
        var ours = State([el1, el2], [rel]);
        var theirs = State([el1, el2], [rel with { Label = "invokes" }]);

        var result = ThreeWayMerge.Compute(baseState, ours, theirs);

        Assert.Empty(result.Conflicts);
        Assert.Single(result.AutoResolved);
        Assert.Equal("relationship", result.AutoResolved[0].EntityType);
    }
}
