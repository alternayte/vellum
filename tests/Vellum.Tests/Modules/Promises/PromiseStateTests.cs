using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Promises;
using Vellum.Shared;

namespace Vellum.Tests.Modules.Promises;

/// <summary>What the fold of a promise stream checks and keeps, with no database.</summary>
public class PromiseStateTests
{
    private static readonly Guid Project = Guid.NewGuid();
    private static readonly DateTimeOffset At = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PromiseContent Content(string claim = "It holds.") =>
        new("A promise", claim, "scope", "pre", "expected", "forbidden", "evidence", [], null, PromiseLinks.None);

    private static Obligation Plan(int number) => new(
        $"ORD-001/O{number}", number, ObligationKind.ContractTest, "A plan",
        new ObligationRecipe("Run it.", Plan: new TestPlanRecipe(["step"], [], ["seen"], [], [])), Obligation.Manual, null);

    private static PromiseState Fold(params object[] events) => events.Aggregate(PromiseState.Initial, PromiseState.Evolve);

    private static PromiseWorld EmptyWorld() => new(
        Guid.NewGuid(), 1, ModelState.Initial, new Dictionary<Guid, string>(), new Dictionary<Guid, ScenarioSnapshot>(),
        new Dictionary<Guid, int>(), new Dictionary<string, TrackedCommit>(), new Dictionary<string, string>(), new HashSet<string>());

    private static object[] Draft() =>
    [
        new PromiseEvent.PromiseCreated(Project, "ORD-001", 1, PromiseOrigin.Human, Content(), "u", At),
        new PromiseEvent.ObligationAdded(1, Plan(1), "u", At),
    ];

    private static VerificationRun Run(string? scanKind) => new(
        Guid.NewGuid(), "ORD-001/O1", 1, "hash", "u", At, null, "c0ffee", Guid.NewGuid(), "source", RunResult.Failed, "scope", [], [], "detail",
        new RunInputs("hash", ScanKind: scanKind));

    [Fact]
    public void Runs_against_comparison_scans_are_counted_apart_and_never_use_up_the_runs_against_the_followed_ref()
    {
        var state = Fold(Draft());
        for (var i = 0; i < 3; i++)
            state = PromiseState.Evolve(state, new PromiseEvent.VerificationRunRecorded(Run("comparison")));
        Assert.Equal((0, 3), (state.RunsOf(1, "ORD-001/O1"), state.ComparisonRunsOf(1, "ORD-001/O1")));

        // One run against the followed ref is allowed, three comparison runs notwithstanding.
        Assert.IsType<CommandResult<IReadOnlyList<object>>.Success>(PromiseDecider.RecordRun(state, Project, Run("tracking"), maxRuns: 1, maxComparisonRuns: 3));
        // The comparison runs have their own bound.
        Assert.IsType<CommandResult<IReadOnlyList<object>>.Conflict>(PromiseDecider.RecordRun(state, Project, Run("comparison"), maxRuns: 1, maxComparisonRuns: 3));
        state = PromiseState.Evolve(state, new PromiseEvent.VerificationRunRecorded(Run("tracking")));
        Assert.IsType<CommandResult<IReadOnlyList<object>>.Conflict>(PromiseDecider.RecordRun(state, Project, Run("tracking"), maxRuns: 1, maxComparisonRuns: 10));
        Assert.IsType<CommandResult<IReadOnlyList<object>>.Success>(PromiseDecider.RecordRun(state, Project, Run("comparison"), maxRuns: 1, maxComparisonRuns: 10));
    }

    [Fact]
    public void The_fold_checks_an_approval_against_the_content_it_names()
    {
        var draft = Fold(Draft());
        var hash = PromiseContentHash.Of(draft.RequirementId, draft.Draft!);

        var approved = PromiseState.Evolve(draft, new PromiseEvent.PromiseApproved(1, "owner", At, 3, new Dictionary<string, string>(), hash));
        Assert.Equal((hash, true), (approved.InForce!.ApprovedContentHash, approved.InForce.ApprovalIntact));
        Assert.DoesNotContain(PromiseViews.Evidence(approved, approved.InForce, new Dictionary<string, VerificationRun>(), EmptyWorld()).Gaps,
            g => g.Contains("is not the content that was approved"));

        // The stream holds other content than the approval names: an edit that the approver did not read.
        var edited = PromiseState.Evolve(draft, new PromiseEvent.PromiseDraftChanged(1, Content("Something else holds."), "agent", At));
        var mismatched = PromiseState.Evolve(edited, new PromiseEvent.PromiseApproved(1, "owner", At, 3, new Dictionary<string, string>(), hash));
        Assert.False(mismatched.InForce!.ApprovalIntact);
        Assert.Contains(PromiseViews.Evidence(mismatched, mismatched.InForce, new Dictionary<string, VerificationRun>(), EmptyWorld()).Gaps,
            g => g == $"The content of revision 1 is not the content that was approved: the approval names the hash {hash}. Review the promise and approve a new revision.");

        // An approval stored before the hash existed names none. Nothing is claimed about it.
        var legacy = PromiseState.Evolve(draft, new PromiseEvent.PromiseApproved(1, "owner", At, 3, new Dictionary<string, string>()));
        Assert.Null(legacy.InForce!.ApprovalIntact);
    }

    [Fact]
    public void The_content_hash_covers_every_field_and_every_recipe_and_ignores_members_that_are_null()
    {
        var draft = Fold(Draft());
        var hash = PromiseContentHash.Of(draft.RequirementId, draft.Draft!);

        Assert.NotEqual(hash, PromiseContentHash.Of("ORD-002", draft.Draft!));
        Assert.NotEqual(hash, PromiseContentHash.Of(draft.RequirementId, draft.Draft! with { Content = Content() with { Scope = "another scope" } }));
        Assert.NotEqual(hash, PromiseContentHash.Of(draft.RequirementId, draft.Draft! with { Content = Content() with { Links = PromiseLinks.None with { ElementIds = [Guid.NewGuid()] } } }));
        var bound = Plan(1) with
        {
            Recipe = Plan(1).Recipe with { Plan = Plan(1).Recipe.Plan! with { EvidenceSource = new EvidenceSource("github-actions", "acme/orders", "ci.yml", null) } },
        };
        Assert.NotEqual(hash, PromiseContentHash.Of(draft.RequirementId, draft.Draft! with { Obligations = [bound] }));
        Assert.NotEqual(hash, PromiseContentHash.Of(draft.RequirementId, draft.Draft! with { Retires = true }));

        // The same content gives the same hash, and a title of an obligation is content too.
        Assert.Equal(hash, PromiseContentHash.Of(draft.RequirementId, Fold(Draft()).Draft!));
        Assert.NotEqual(hash, PromiseContentHash.Of(draft.RequirementId, draft.Draft! with { Obligations = [Plan(1) with { Title = "Another title" }] }));
    }

    [Fact]
    public void A_revision_that_retires_copies_nothing_and_shows_what_it_retires()
    {
        var draft = Fold(Draft());
        var approved = PromiseState.Evolve(draft, new PromiseEvent.PromiseApproved(1, "owner", At, 3, new Dictionary<string, string>(), PromiseContentHash.Of("ORD-001", draft.Draft!)));
        var retiring = PromiseState.Evolve(approved, new PromiseEvent.PromiseRevised(2, true, "owner", At));

        var revision = retiring.Draft!;
        Assert.Equal((2, true, 1), (revision.Revision, revision.Retires, revision.RetiresRevision));
        Assert.Empty(revision.Obligations);
        Assert.Null(revision.Content.Scope);
        Assert.Same(retiring.InForce, retiring.Shown(revision));

        // A revision that does not retire still starts as a copy.
        var revised = PromiseState.Evolve(approved, new PromiseEvent.PromiseRevised(2, false, "owner", At));
        Assert.Equal((approved.InForce!.Content, 1), (revised.Draft!.Content, revised.Draft.Obligations.Count));
        Assert.Null(revised.Draft.RetiresRevision);
    }
}
