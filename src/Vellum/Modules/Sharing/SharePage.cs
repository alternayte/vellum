using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Build;
using Vellum.Modules.Costs;
using Vellum.Modules.Modelling;
using Vellum.Modules.Promises;
using Vellum.Modules.Review;
using Vellum.Modules.Trace.Engine;
using Vellum.Modules.Views;
using Vellum.Shared;

namespace Vellum.Modules.Sharing;

// The page a share link shows. Every type here is the share's own: no type of an
// authenticated endpoint is reused, so a field that a member endpoint gains later never
// reaches a visitor. Nothing names a member, a setting, a comment, a scan, an AI
// conversation or a line of source text.

/// <summary>The named revision a pinned page shows, as it was named when the link was made.</summary>
public sealed record SharedRevision(string Name, DateTimeOffset CreatedAt);

/// <param name="TruthLabel"><c>declared</c>, <c>observed-in-source</c>, <c>measured</c> or <c>unknown</c>. An inferred fact that no person confirmed is never shared.</param>
/// <param name="Citation">Where the evidence is, as <c>path:lines</c> text only. Null when the fact cites no source line.</param>
/// <param name="Stale">True when a complete scan no longer finds the evidence of the fact.</param>
public sealed record SharedElement(
    Guid Id, string Kind, string Name, string? Description, string? Technology, string Status,
    Guid? ParentId, IReadOnlyList<string> Tags, string? Icon, string TruthLabel, string? Citation, bool Stale);

public sealed record SharedRelationship(
    Guid Id, Guid FromId, Guid ToId, string? Label, string? Technology, string Kind, Guid? MessageId,
    string? LineShape, string TruthLabel, string? Citation, bool Stale);

public sealed record SharedMessage(
    Guid Id, string Name, string? Description, Guid ProducerId, IReadOnlyList<Guid> ConsumerIds, Guid? SchemaId,
    IReadOnlyList<string> Tags, string TruthLabel, string? Citation, bool Stale);

public sealed record SharedOperation(
    Guid Id, Guid OwnerId, string Name, string Transport, Guid? RequestSchemaId, Guid? ResponseSchemaId,
    int TimeoutMs, RetryDto Retry, string TruthLabel, string? Citation, bool Stale);

public sealed record SharedChannel(Guid Id, string Name, string Kind, string TruthLabel, string? Citation, bool Stale);

public sealed record SharedSubscription(
    Guid Id, Guid ChannelId, Guid ConsumerId, RetryDto Retry, Guid? DeadLetterChannelId, string TruthLabel, string? Citation, bool Stale);

public sealed record SharedBinding(
    Guid Id, Guid SourceChannelId, Guid TargetChannelId, string? RoutingKey, string TruthLabel, string? Citation, bool Stale);

public sealed record SharedModel(
    IReadOnlyList<SharedElement> Elements,
    IReadOnlyList<SharedRelationship> Relationships,
    IReadOnlyList<SharedMessage> Messages,
    IReadOnlyList<SharedOperation> Operations,
    IReadOnlyList<SharedChannel> Channels,
    IReadOnlyList<SharedSubscription> Subscriptions,
    IReadOnlyList<SharedBinding> Bindings);

public sealed record SharedPosition(Guid ElementId, double X, double Y, double? Width, double? Height);

public sealed record SharedRoutePoint(double X, double Y);

public sealed record SharedRouteBox(double X, double Y, double Width, double Height);

/// <summary>
/// The path of a line from the auto layout (Tidy), as the canvas saved it: the boxes of the two
/// cards when the layout ran, the points and the centre of the label. The page draws it only
/// while both cards keep those boxes. Coordinates only: a route names nothing.
/// </summary>
public sealed record SharedRoute(SharedRouteBox From, SharedRouteBox To, IReadOnlyList<SharedRoutePoint> Points, SharedRoutePoint? Label)
{
    public static SharedRoute? Of(JsonDocument? saved) => LayoutRoute.Read(saved) is { } route
        ? new SharedRoute(Box(route.From), Box(route.To), [.. route.Points.Select(p => new SharedRoutePoint(p.X, p.Y))],
            route.Label is { } l ? new SharedRoutePoint(l.X, l.Y) : null)
        : null;

    private static SharedRouteBox Box(RouteBox b) => new(b.X, b.Y, b.Width, b.Height);
}

/// <summary>
/// The saved sides of one relationship on a diagram: top, right, bottom or left, or null for
/// auto, and its route from the auto layout, or null.
/// </summary>
public sealed record SharedEdgeSides(Guid RelationshipId, string? SourceSide, string? TargetSide, SharedRoute? Route = null);

/// <summary>The saved route of the derived line from one card to another on a diagram.</summary>
public sealed record SharedLineRoute(Guid FromId, Guid ToId, SharedRoute Route);

/// <summary>
/// The relationships a view link does not share, between the parts hidden inside two shared
/// cards, in one direction: only how many there are and, for one, its label. The page merges
/// them into the derived line between the cards it draws. No ID or name of a hidden part is here.
/// </summary>
/// <param name="FromId">The nearest shared card that holds the source of each relationship.</param>
/// <param name="ToId">The nearest shared card that holds the target of each relationship.</param>
/// <param name="Label">The label of the one relationship. Null for more than one.</param>
public sealed record SharedHiddenLine(Guid FromId, Guid ToId, string? Label, int Count);

/// <summary>A derived line the view does not draw, by its two shared cards.</summary>
public sealed record SharedHiddenPair(Guid FromId, Guid ToId);

/// <summary>
/// Which lines the diagram draws, as the view saved it (LineVisibility.cs): each switch is true
/// when its lines are drawn. A relationship hidden by ID or by kind draws neither as itself nor
/// inside a derived line; a cross-level line is a direct relationship whose two drawn ends sit at
/// different nesting depths. Only IDs of the page are here.
/// </summary>
public sealed record SharedLineVisibility(
    bool DerivedLines, bool Structural, bool Behavioural, bool CrossLevel,
    IReadOnlyList<Guid> HiddenRelationshipIds, IReadOnlyList<SharedHiddenPair> HiddenDerivedLines)
{
    /// <summary>True when the diagram draws this relationship: it is not hidden by ID or by kind.</summary>
    public bool Draws(SharedRelationship r) =>
        !HiddenRelationshipIds.Contains(r.Id) && (r.Kind == "behavioural" ? Behavioural : Structural);
}

/// <summary>
/// A saved diagram: the place of each element on it, the saved sides and routes of its
/// relationships and the saved routes of its derived lines. Presentation only.
/// <paramref name="Edges"/> is null on a page pinned before sides existed, and
/// <paramref name="Lines"/> on a page pinned before routes existed: the lines then take auto
/// curves. <paramref name="HiddenLines"/> is set on a view link only.
/// <paramref name="LineVisibility"/> is null when the view hides no line. <paramref name="HiddenElementIds"/>
/// are the shared cards a person hid from the diagram; null on a page pinned before cards could be hidden.
/// </summary>
public sealed record SharedView(
    Guid Id, string Name, IReadOnlyList<SharedPosition> Positions, IReadOnlyList<SharedEdgeSides>? Edges = null,
    IReadOnlyList<SharedLineRoute>? Lines = null, IReadOnlyList<SharedHiddenLine>? HiddenLines = null,
    SharedLineVisibility? LineVisibility = null, IReadOnlyList<Guid>? HiddenElementIds = null);

/// <summary>A payload schema: a JSON Schema document.</summary>
public sealed record SharedSchema(Guid Id, string Name, string? Description, string Content, int Version);

/// <summary>
/// One step of a flow: what runs and over which IDs of the shared model. Payload fixtures,
/// headers and write changes are not shared: they are sample data a member typed, and can
/// hold a credential.
/// </summary>
/// <param name="Type"><c>call</c>, <c>publish</c>, <c>write</c>, <c>parallel</c> or <c>branch</c>.</param>
/// <param name="FromId">The element that calls, publishes or writes.</param>
/// <param name="TargetId">The operation called, the message published or the store written.</param>
/// <param name="ChannelId">The channel of a publish.</param>
/// <param name="Groups">The nested steps, each group with its name: <c>then</c>, <c>on-error</c>, <c>else</c>, <c>branch</c> or <c>handler</c>.</param>
public sealed record SharedFlowStep(
    string Id, string Type, Guid? FromId, Guid? TargetId, Guid? ChannelId, string? Dataset,
    string? WhenStep, string? Is, IReadOnlyList<SharedStepGroup> Groups);

public sealed record SharedStepGroup(string Name, IReadOnlyList<SharedFlowStep> Steps);

/// <summary>A flow (scenario): its steps over the IDs of the shared model, and how many faults and assertions it declares.</summary>
public sealed record SharedFlow(Guid Id, string Name, IReadOnlyList<SharedFlowStep> Steps, int Faults, int Assertions)
{
    /// <summary>
    /// The flow as a visitor may see it, or null when a step names a fact that the page does
    /// not share: the flow would show that the hidden fact exists and how it is used.
    /// </summary>
    public static SharedFlow? From(Scenario scenario, IReadOnlySet<Guid> shared)
    {
        var complete = true;
        Guid? Known(Guid id)
        {
            if (!shared.Contains(id))
                complete = false;
            return id;
        }

        IReadOnlyList<SharedFlowStep> Steps(IEnumerable<Step> steps) => [.. steps.Select(Step)];
        static IReadOnlyList<SharedStepGroup> Groups(params SharedStepGroup[] groups) => [.. groups.Where(g => g.Steps.Count > 0)];
        SharedFlowStep Step(Step step) => step switch
        {
            CallStep c => new(c.Id, "call", Known(c.From.Value), Known(c.Operation.Value), null, null, null, null,
                Groups(new("then", Steps(c.Then)), new("on-error", Steps(c.OnError)))),
            PublishStep p => new(p.Id, "publish", Known(p.From.Value), Known(p.Message.Value), Known(p.Channel.Value), null, null, null,
                [.. p.Handlers.OrderBy(h => h.Key.Value).Select(h => new SharedStepGroup("handler", Steps(h.Value)))]),
            WriteStep w => new(w.Id, "write", Known(w.By.Value), Known(w.Store.Value), null, w.Dataset, null, null, []),
            ParallelStep p => new(p.Id, "parallel", null, null, null, null, null, null,
                [.. p.Branches.Select(b => new SharedStepGroup("branch", Steps(b)))]),
            BranchStep b => new(b.Id, "branch", null, null, null, null, b.WhenStep, b.Is.ToString().ToLowerInvariant(),
                Groups(new("then", Steps(b.Then)), new("else", Steps(b.Else)))),
            _ => new(step.Id, "unknown", null, null, null, null, null, null, []),
        };

        var flow = new SharedFlow(scenario.Id.Value, scenario.Name, Steps(scenario.Steps), scenario.Faults.Count, scenario.Assertions.Count);
        foreach (var fault in scenario.Faults)
            Known(fault.Target);
        return complete ? flow : null;
    }
}

/// <param name="Type">The kind of page, such as <c>adr</c> or <c>prd</c>, or null.</param>
public sealed record SharedDoc(Guid Id, string Title, string? Type, Guid? ElementId, string? Space, string Content, DateTimeOffset UpdatedAt);

public sealed record SharedAlternative(string Title, string? WhyNot);

/// <param name="Status"><c>accepted</c> or <c>superseded</c>. A proposed or rejected decision is never shared.</param>
public sealed record SharedDecision(
    Guid Id, string Label, string Status, string Title, string Context, string Decision,
    IReadOnlyList<SharedAlternative> Alternatives, string? Consequences, IReadOnlyList<Guid> AffectedIds,
    Guid? Supersedes, DateTimeOffset? AcceptedAt);

public sealed record SharedMoney(string Currency, decimal Amount);

/// <param name="Status">The status of the estimate line: an amount is known, or why it is not.</param>
public sealed record SharedCostLine(
    Guid ElementId, string Resource, string Name, string Category, string Provider, string Region,
    string Status, string? Reason, decimal? Amount, string? Currency);

/// <summary>The estimated monthly spend, with the date it is for and the lines that have no known price.</summary>
public sealed record SharedCosts(DateOnly AsOf, string Currency, IReadOnlyList<SharedMoney> Totals, IReadOnlyList<SharedCostLine> Lines, int UnknownLines)
{
    /// <summary>The lines of <paramref name="elements"/> only, with totals of those lines.</summary>
    public static SharedCosts Of(SharedCosts costs, IReadOnlySet<Guid> elements) => Of(costs.AsOf, costs.Currency, [.. costs.Lines.Where(l => elements.Contains(l.ElementId))]);

    public static SharedCosts Of(DateOnly asOf, string currency, IReadOnlyList<SharedCostLine> lines) => new(
        asOf, currency,
        [.. lines.Where(l => l.Amount is not null && l.Currency is not null)
            .GroupBy(l => l.Currency!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new SharedMoney(g.Key, g.Sum(l => l.Amount!.Value)))],
        lines,
        lines.Count(l => l.Amount is null));
}

/// <param name="Result"><c>passed</c>, <c>failed</c>, <c>not-run</c> or <c>unsupported</c>, as the newest run says.</param>
/// <param name="Current">For a pass or a fail: true when the run read the inputs that are current.</param>
public sealed record SharedObligation(string Id, string Title, string Kind, string Result, bool? Current, DateTimeOffset? At, string? Environment);

public sealed record SharedEvidenceCounts(int Passed, int Failed, int NotRun, int Unsupported, int Stale);

/// <summary>An approved promise and the evidence of its revision in force.</summary>
public sealed record SharedPromise(
    string RequirementId, int Revision, string Title, string Claim, string? Scope, string? Preconditions,
    string? ExpectedOutcome, string? ForbiddenOutcome, DateTimeOffset? ApprovedAt,
    SharedEvidenceCounts Counts, IReadOnlyList<SharedObligation> Obligations);

/// <summary>
/// What a share link shows. <paramref name="Scope"/> <c>view</c> holds one diagram and the
/// details of what is on it; flows, docs and decisions belong to a project link.
/// <paramref name="Costs"/> and <paramref name="Promises"/> are null unless the link turns them on.
/// </summary>
/// <param name="Mode"><c>live</c> follows the accepted model on main; <c>pinned</c> shows one named revision.</param>
/// <param name="AsOf">When the page was read: now for a live link, the moment the link was made for a pinned one.</param>
public sealed record SharedPage(
    string ProjectName, string Scope, string Mode, SharedRevision? Revision, DateTimeOffset AsOf,
    SharedModel Model, IReadOnlyList<SharedView> Views,
    IReadOnlyList<SharedSchema> Schemas, IReadOnlyList<SharedFlow> Flows,
    IReadOnlyList<SharedDoc> Docs, IReadOnlyList<SharedDecision> Decisions,
    SharedCosts? Costs, IReadOnlyList<SharedPromise>? Promises)
{
    /// <summary>The part of a whole page that <paramref name="link"/> shows. Null when the view of a view link is not on the page.</summary>
    public SharedPage? For(ShareLinkEntity link)
    {
        var page = this with
        {
            Mode = link.Mode,
            Costs = link.ShowCosts ? Costs : null,
            Promises = link.ShowPromises ? Promises : null,
        };
        if (link.Scope != ShareScope.View)
            return page with { Scope = ShareScope.Project };

        var view = Views.FirstOrDefault(v => v.Id == link.ViewId);
        return view is null ? null : page.OnlyView(view);
    }

    /// <summary>
    /// One diagram and the details of what is on it: the elements the view places and their
    /// parents, the facts between them, the schemas those facts name and their cost lines. A
    /// view that places no element shows the whole model, as the canvas does. Flows, docs,
    /// decisions and promises belong to a project link: a promise can name any fact. A
    /// relationship the view hides by ID or by kind is not on the page at all.
    /// </summary>
    private SharedPage OnlyView(SharedView view)
    {
        var visibility = view.LineVisibility;
        var source = visibility is null ? Model : Model with { Relationships = [.. Model.Relationships.Where(visibility.Draws)] };
        var drawn = source.Relationships.Select(r => r.Id).ToHashSet();
        var model = source;
        view = view with
        {
            HiddenLines = [],
            Edges = view.Edges?.Where(e => drawn.Contains(e.RelationshipId)).ToList(),
            LineVisibility = visibility is null ? null : visibility with { HiddenRelationshipIds = [] },
        };
        if (view.Positions.Count > 0)
        {
            var parents = source.Elements.ToDictionary(e => e.Id, e => e.ParentId);
            var kept = new HashSet<Guid>();
            // A card hidden from the view is not shared through it, unless a shown card sits inside it.
            var hiddenCards = (view.HiddenElementIds ?? []).ToHashSet();
            foreach (var position in view.Positions.Where(p => !hiddenCards.Contains(p.ElementId)))
            {
                for (Guid? id = position.ElementId; id is { } at && kept.Add(at);)
                    id = parents.GetValueOrDefault(at);
            }
            var keptRelationships = source.Relationships.Where(r => kept.Contains(r.FromId) && kept.Contains(r.ToId)).Select(r => r.Id).ToHashSet();
            view = view with
            {
                Edges = view.Edges?.Where(e => keptRelationships.Contains(e.RelationshipId)).ToList(),
                Lines = view.Lines?.Where(l => kept.Contains(l.FromId) && kept.Contains(l.ToId)).ToList(),
                HiddenLines = HiddenLines(source.Relationships, parents, kept),
                LineVisibility = view.LineVisibility is { } v
                    ? v with { HiddenDerivedLines = [.. v.HiddenDerivedLines.Where(l => kept.Contains(l.FromId) && kept.Contains(l.ToId))] }
                    : null,
                HiddenElementIds = [.. hiddenCards.Where(kept.Contains).Order()],
            };

            var messages = Model.Messages.Where(m => kept.Contains(m.ProducerId))
                .Select(m => m with { ConsumerIds = [.. m.ConsumerIds.Where(kept.Contains)] }).ToList();
            var messageIds = messages.Select(m => m.Id).ToHashSet();
            var subscriptions = Model.Subscriptions.Where(s => kept.Contains(s.ConsumerId)).ToList();
            var channelIds = subscriptions.Select(s => s.ChannelId).ToHashSet();
            model = new SharedModel(
                [.. Model.Elements.Where(e => kept.Contains(e.Id))],
                [.. source.Relationships.Where(r => kept.Contains(r.FromId) && kept.Contains(r.ToId))
                    .Select(r => r.MessageId is { } m && !messageIds.Contains(m) ? r with { MessageId = null } : r)],
                messages,
                [.. Model.Operations.Where(o => kept.Contains(o.OwnerId))],
                [.. Model.Channels.Where(c => channelIds.Contains(c.Id))],
                [.. subscriptions.Select(s => s.DeadLetterChannelId is { } d && !channelIds.Contains(d) ? s with { DeadLetterChannelId = null } : s)],
                [.. Model.Bindings.Where(b => channelIds.Contains(b.SourceChannelId) && channelIds.Contains(b.TargetChannelId))]);
        }

        var shownElements = model.Elements.Select(e => e.Id).ToHashSet();
        var schemaIds = model.Operations.SelectMany(o => new[] { o.RequestSchemaId, o.ResponseSchemaId })
            .Concat(model.Messages.Select(m => m.SchemaId)).OfType<Guid>().ToHashSet();
        return this with
        {
            Scope = ShareScope.View,
            Model = model,
            Views = [view],
            Schemas = [.. Schemas.Where(s => schemaIds.Contains(s.Id))],
            Flows = [],
            Docs = [],
            Decisions = [],
            Costs = Costs is null ? null : SharedCosts.Of(Costs, shownElements),
            Promises = null,
        };
    }

    /// <summary>
    /// The relationships of the whole page that a view link does not share, by the rules of
    /// derived lines (DerivedLines.cs): each end attaches to its nearest shared card, an end with
    /// none draws nothing, two ends on the same card draw nothing, and the rest merge by pair of
    /// cards in one direction. A visitor sees only shared cards, so the canvas merges each of
    /// these into the derived line of the cards it draws and the lines are the same as in the app.
    /// An inferred relationship is not on the page, so it is not counted.
    /// </summary>
    private static List<SharedHiddenLine> HiddenLines(IReadOnlyList<SharedRelationship> relationships, IReadOnlyDictionary<Guid, Guid?> parents, IReadOnlySet<Guid> kept)
    {
        var resolved = new Dictionary<Guid, Guid?>();
        Guid? NearestKept(Guid id)
        {
            if (resolved.TryGetValue(id, out var known)) return known;
            Guid? found = null;
            var seen = new HashSet<Guid>();
            for (Guid? at = id; at is { } current && parents.ContainsKey(current) && seen.Add(current); at = parents[current])
            {
                if (kept.Contains(current))
                {
                    found = current;
                    break;
                }
            }
            resolved[id] = found;
            return found;
        }

        var groups = new Dictionary<(Guid From, Guid To), List<SharedRelationship>>();
        foreach (var r in relationships)
        {
            if (kept.Contains(r.FromId) && kept.Contains(r.ToId)) continue;
            if (NearestKept(r.FromId) is not { } from || NearestKept(r.ToId) is not { } to || from == to) continue;
            if (!groups.TryGetValue((from, to), out var group)) groups[(from, to)] = group = [];
            group.Add(r);
        }
        return [.. groups
            .OrderBy(g => g.Key.From.ToString(), StringComparer.Ordinal)
            .ThenBy(g => g.Key.To.ToString(), StringComparer.Ordinal)
            .Select(g => new SharedHiddenLine(g.Key.From, g.Key.To, g.Value.Count == 1 ? g.Value[0].Label : null, g.Value.Count))];
    }
}

public static class ShareScope
{
    public const string Project = "project";
    public const string View = "view";
}

public static class ShareMode
{
    public const string Live = "live";
    public const string Pinned = "pinned";
}

/// <summary>What a page is made of, before the link picks its part.</summary>
/// <param name="ViewId">For a view link, the one view to read. Null reads every view.</param>
public sealed record SharePageRequest(
    Guid ProjectId, PinnedModel Model, SharedRevision? Revision, DateTimeOffset AsOf,
    bool WithCosts, bool WithPromises, bool WithProjectParts, Guid? ViewId = null);

/// <summary>Reads a page from the stores of the other modules and keeps only what a visitor may see.</summary>
public sealed partial class SharePageReader(IServiceProvider services, AppDbContext db, EstimateReader costs)
{
    public async Task<SharedPage> ReadAsync(SharePageRequest request, CancellationToken ct)
    {
        var projectId = request.ProjectId;
        var input = await BuildInput.LoadAsync(services, projectId, request.Model, BuildLimits.Default, ct);
        var model = ShareModel(request.Model.State);

        var viewsQuery = db.Views.AsNoTracking().Where(v => v.ProjectId == projectId);
        if (request.ViewId is { } viewId)
            viewsQuery = viewsQuery.Where(v => v.Id == viewId);
        var views = await viewsQuery.OrderBy(v => v.Name).ThenBy(v => v.Id).Select(v => new { v.Id, v.Name, v.HiddenElementIds }).ToListAsync(ct);
        var viewIds = views.Select(v => v.Id).ToList();
        var shown = model.Elements.Select(e => e.Id).ToHashSet();
        var positions = (await db.LayoutPositions.AsNoTracking()
                .Where(p => viewIds.Contains(p.ViewId))
                .Select(p => new { p.ViewId, p.ElementId, p.X, p.Y, p.Width, p.Height })
                .ToListAsync(ct))
            .Where(p => shown.Contains(p.ElementId))
            .ToLookup(p => p.ViewId);
        var shownRelationships = model.Relationships.Select(r => r.Id).ToHashSet();
        var edges = (await db.LayoutEdges.AsNoTracking()
                .Where(e => viewIds.Contains(e.ViewId) && (e.SourceSide != null || e.TargetSide != null || e.RoutePoints != null))
                .Select(e => new { e.ViewId, e.RelationshipId, e.SourceSide, e.TargetSide, e.RoutePoints })
                .ToListAsync(ct))
            .Where(e => shownRelationships.Contains(e.RelationshipId))
            .Select(e => new { e.ViewId, Edge = new SharedEdgeSides(e.RelationshipId, e.SourceSide, e.TargetSide, SharedRoute.Of(e.RoutePoints)) })
            .Where(e => e.Edge.SourceSide is not null || e.Edge.TargetSide is not null || e.Edge.Route is not null)
            .ToLookup(e => e.ViewId, e => e.Edge);
        var lines = (await db.LayoutLines.AsNoTracking()
                .Where(l => viewIds.Contains(l.ViewId))
                .Select(l => new { l.ViewId, l.FromId, l.ToId, l.Route })
                .ToListAsync(ct))
            .Where(l => shown.Contains(l.FromId) && shown.Contains(l.ToId))
            .Select(l => (l.ViewId, Route: SharedRoute.Of(l.Route) is { } route ? new SharedLineRoute(l.FromId, l.ToId, route) : null))
            .Where(l => l.Route is not null)
            .ToLookup(l => l.ViewId, l => l.Route!);
        // Only the IDs of the page: a hidden relationship that is not shared is not named.
        var visibility = (await db.LineVisibilities.AsNoTracking().Where(l => viewIds.Contains(l.ViewId)).ToListAsync(ct))
            .Select(l => (l.ViewId, Visibility: LineVisibility.Of(l)))
            .ToDictionary(l => l.ViewId, l => new SharedLineVisibility(
                l.Visibility.DerivedLines, l.Visibility.Structural, l.Visibility.Behavioural, l.Visibility.CrossLevel,
                [.. l.Visibility.HiddenRelationshipIds.Where(shownRelationships.Contains).Distinct().Order()],
                [.. l.Visibility.HiddenDerivedLines.Where(p => shown.Contains(p.FromId) && shown.Contains(p.ToId))
                    .Distinct().OrderBy(p => p.FromId).ThenBy(p => p.ToId).Select(p => new SharedHiddenPair(p.FromId, p.ToId))]));
        var sharedViews = views
            .Select(v => new SharedView(v.Id, v.Name,
                [.. positions[v.Id].OrderBy(p => p.ElementId).Select(p => new SharedPosition(p.ElementId, p.X, p.Y, p.Width, p.Height))],
                [.. edges[v.Id].OrderBy(e => e.RelationshipId)],
                [.. lines[v.Id].OrderBy(l => l.FromId).ThenBy(l => l.ToId)],
                LineVisibility: visibility.GetValueOrDefault(v.Id),
                HiddenElementIds: [.. v.HiddenElementIds.Where(shown.Contains).Distinct().Order()]))
            .ToList();

        var schemas = input.Schemas.OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Id)
            .Select(s => new SharedSchema(s.Id, s.Name, s.Description, s.Content, s.Version))
            .ToList();

        List<SharedFlow> flows = [];
        List<SharedDoc> docs = [];
        List<SharedDecision> decisions = [];
        if (request.WithProjectParts)
        {
            var facts = SharedIds(model);
            flows = [.. input.Scenarios.OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Id.Value)
                .Select(s => SharedFlow.From(s, facts)).OfType<SharedFlow>()];
            docs = [.. (await DocsAsync(projectId, ct)).Select(d => d.ElementId is { } e && !shown.Contains(e) ? d with { ElementId = null } : d)];
            decisions = [.. Decisions(input.Decisions).Select(d => d with { AffectedIds = [.. d.AffectedIds.Where(facts.Contains)] })];
        }

        SharedCosts? sharedCosts = null;
        if (request.WithCosts)
            sharedCosts = await CostsAsync(projectId, shown, DateOnly.FromDateTime(request.AsOf.UtcDateTime), ct);

        var promises = request.WithPromises ? Promises(input.Promises) : null;

        return new SharedPage(
            input.ProjectName, request.ViewId is null ? ShareScope.Project : ShareScope.View, ShareMode.Live,
            request.Revision, request.AsOf, model, sharedViews, schemas, flows, docs, decisions, sharedCosts, promises);
    }

    /// <summary>Every ID the shared model holds, of every kind.</summary>
    private static HashSet<Guid> SharedIds(SharedModel model) =>
    [
        .. model.Elements.Select(e => e.Id), .. model.Relationships.Select(r => r.Id), .. model.Messages.Select(m => m.Id),
        .. model.Operations.Select(o => o.Id), .. model.Channels.Select(c => c.Id), .. model.Subscriptions.Select(s => s.Id),
        .. model.Bindings.Select(b => b.Id),
    ];

    /// <summary>
    /// The model without the facts a visitor may not see: an inferred fact that no person
    /// confirmed, and every fact that stands on one (a child of a hidden element, an edge to
    /// it, an operation it owns).
    /// </summary>
    public static SharedModel ShareModel(ModelState state)
    {
        var elements = new Dictionary<Guid, ElementState>();
        foreach (var element in state.Elements.Values.Where(e => e.FactStatus != FactStatus.Inferred))
            elements[element.Id.Value] = element;

        // A child whose parent is hidden would float free on the canvas, and it would tell that
        // the hidden parent exists. Hiding runs until no element points at a hidden one.
        bool removed;
        do
        {
            removed = false;
            foreach (var element in elements.Values.ToList())
            {
                if (element.ParentId is { } parent && !elements.ContainsKey(parent.Value))
                {
                    elements.Remove(element.Id.Value);
                    removed = true;
                }
            }
        } while (removed);

        static bool Shared(FactStatus status) => status != FactStatus.Inferred;
        bool Has(Guid id) => elements.ContainsKey(id);

        var messages = state.Messages.Values
            .Where(m => Shared(m.FactStatus) && Has(m.ProducerId.Value))
            .ToDictionary(m => m.Id.Value);
        var channels = state.Channels.Values.Where(c => Shared(c.FactStatus)).ToDictionary(c => c.Id.Value);

        return new SharedModel(
            [.. elements.Values.OrderBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Id.Value).Select(e => new SharedElement(
                e.Id.Value, Lower(e.Kind), e.Name, e.Description, e.Technology, Lower(e.Status), e.ParentId?.Value,
                [.. e.Tags], e.Icon, e.FactStatus.ToApiString(), Citation(e.Provenance), e.Stale is not null))],
            [.. state.Relationships.Values
                .Where(r => Shared(r.FactStatus) && Has(r.FromId.Value) && Has(r.ToId.Value))
                .OrderBy(r => r.Id.Value)
                .Select(r => new SharedRelationship(
                    r.Id.Value, r.FromId.Value, r.ToId.Value, r.Label, r.Technology, Lower(r.Kind),
                    r.MessageId is { } m && messages.ContainsKey(m.Value) ? m.Value : null, r.LineShape,
                    r.FactStatus.ToApiString(), Citation(r.Provenance), r.Stale is not null))],
            [.. messages.Values.OrderBy(m => m.Name, StringComparer.Ordinal).ThenBy(m => m.Id.Value).Select(m => new SharedMessage(
                m.Id.Value, m.Name, m.Description, m.ProducerId.Value, [.. m.ConsumerIds.Select(c => c.Value).Where(Has)],
                m.SchemaId?.Value, [.. m.Tags], m.FactStatus.ToApiString(), Citation(m.Provenance), m.Stale is not null))],
            [.. state.Operations.Values
                .Where(o => Shared(o.FactStatus) && Has(o.OwnerId.Value))
                .OrderBy(o => o.Name, StringComparer.Ordinal).ThenBy(o => o.Id.Value)
                .Select(o => new SharedOperation(
                    o.Id.Value, o.OwnerId.Value, o.Name, Lower(o.Transport), o.RequestSchemaId?.Value, o.ResponseSchemaId?.Value,
                    o.TimeoutMs, RetryDto.From(o.Retry), o.FactStatus.ToApiString(), Citation(o.Provenance), o.Stale is not null))],
            [.. channels.Values.OrderBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Id.Value).Select(c => new SharedChannel(
                c.Id.Value, c.Name, Lower(c.Kind), c.FactStatus.ToApiString(), Citation(c.Provenance), c.Stale is not null))],
            [.. state.Subscriptions.Values
                .Where(s => Shared(s.FactStatus) && channels.ContainsKey(s.ChannelId.Value) && Has(s.ConsumerId.Value))
                .OrderBy(s => s.Id.Value)
                .Select(s => new SharedSubscription(
                    s.Id.Value, s.ChannelId.Value, s.ConsumerId.Value, RetryDto.From(s.Retry),
                    s.DeadLetterChannelId is { } d && channels.ContainsKey(d.Value) ? d.Value : null,
                    s.FactStatus.ToApiString(), Citation(s.Provenance), s.Stale is not null))],
            [.. state.Bindings.Values
                .Where(b => Shared(b.FactStatus) && channels.ContainsKey(b.SourceChannelId.Value) && channels.ContainsKey(b.TargetChannelId.Value))
                .OrderBy(b => b.Id.Value)
                .Select(b => new SharedBinding(
                    b.Id.Value, b.SourceChannelId.Value, b.TargetChannelId.Value, b.RoutingKey,
                    b.FactStatus.ToApiString(), Citation(b.Provenance), b.Stale is not null))]);
    }

    [GeneratedRegex(@"^[^\s@]+@[^\s:]+:(?<path>[^\s:][^\s]*):(?<lines>[0-9]+(?:-[0-9]+)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex SourcePointer();

    /// <summary>
    /// The <c>path:lines</c> part of a source pointer such as
    /// <c>github.com/acme/orders@abc123:src/Api/Orders.cs:41-57</c>. The repository, the
    /// commit and every other kind of pointer (an AI proposal, an agent token) stay inside the
    /// workspace: they give null.
    /// </summary>
    public static string? Citation(Provenance? provenance) =>
        provenance?.Source is { Length: <= TextRule.MaxPointer } source && SourcePointer().Match(source) is { Success: true } match
            ? $"{match.Groups["path"].Value}:{match.Groups["lines"].Value}"
            : null;

    private static string Lower<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();

    /// <summary>The docs of main. A page of a draft is not accepted, so it is not shared.</summary>
    private async Task<List<SharedDoc>> DocsAsync(Guid projectId, CancellationToken ct)
    {
        var spaces = await db.Spaces.AsNoTracking().Where(s => s.ProjectId == projectId).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var rows = await db.Documents.AsNoTracking()
            .Where(d => d.ProjectId == projectId && d.DraftId == null)
            .OrderBy(d => d.Title).ThenBy(d => d.Id)
            .Select(d => new { d.Id, d.Title, d.Type, d.ElementId, d.SpaceId, d.Content, d.UpdatedAt })
            .ToListAsync(ct);
        return [.. rows.Select(d => new SharedDoc(
            d.Id, d.Title, d.Type, d.ElementId, d.SpaceId is { } space ? spaces.GetValueOrDefault(space) : null, d.Content, d.UpdatedAt))];
    }

    /// <summary>The decisions a person accepted. A proposal waits for the accept path and a rejected one was never in force.</summary>
    private static List<SharedDecision> Decisions(IReadOnlyList<PinnedDecision> pinned)
    {
        var book = new DecisionBook(pinned);
        return [.. book.All
            .Where(d => d.State.Status == DecisionStatus.Accepted && d.State.Content is not null)
            .Select(d =>
            {
                var content = d.State.Content!;
                return new SharedDecision(
                    d.Id, DecisionState.Label(d.State.Number), book.StatusOf(d), content.Title, content.Context, content.Decision,
                    [.. content.Alternatives.Select(a => new SharedAlternative(a.Title, a.WhyNot))], content.Consequences,
                    [.. content.AffectedIds], content.Supersedes, d.State.AcceptedAt);
            })];
    }

    /// <summary>The estimate of main, with the lines of the elements the page shows.</summary>
    private async Task<SharedCosts> CostsAsync(Guid projectId, HashSet<Guid> shown, DateOnly asOf, CancellationToken ct)
    {
        var main = await db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => p.StreamId).FirstAsync(ct);
        var estimate = (await costs.BuildAsync(projectId, main, asOf, ct)).Dto.Estimate;
        return SharedCosts.Of(estimate.AsOf, estimate.Currency,
            [.. estimate.Lines.Where(l => shown.Contains(l.ElementId)).Select(l => new SharedCostLine(
                l.ElementId, l.ResourceName, l.Name, l.Category, l.Provider, l.Region, l.Status, l.Reason, l.Amount, l.Currency))]);
    }

    /// <summary>The approved promises, each with the evidence of the revision in force.</summary>
    private static List<SharedPromise> Promises(IReadOnlyList<PromiseDto> promises) =>
        [.. promises
            .Where(p => p.State == "approved" && p.InForceRevision is not null)
            .Select(p =>
            {
                var current = p.Current;
                var titles = current.Obligations.ToDictionary(o => o.Id, StringComparer.Ordinal);
                var counts = current.EvidenceStatus.Counts;
                return new SharedPromise(
                    p.RequirementId, current.Revision, current.Title, current.Claim, current.Scope, current.Preconditions,
                    current.ExpectedOutcome, current.ForbiddenOutcome, current.ApprovedAt,
                    new SharedEvidenceCounts(counts.Passed, counts.Failed, counts.NotRun, counts.Unsupported, counts.Stale),
                    [.. current.EvidenceStatus.Obligations.Select(o => new SharedObligation(
                        o.ObligationId,
                        titles.TryGetValue(o.ObligationId, out var obligation) ? obligation.Title : o.ObligationId,
                        titles.TryGetValue(o.ObligationId, out var known) ? known.Kind : "unknown",
                        o.Result, o.Current, o.At, o.Environment))]);
            })];

    /// <summary>The JSON a pinned page is stored as: the same as the API writes.</summary>
    public static string Serialize(SharedPage page, JsonSerializerOptions options) => JsonSerializer.Serialize(page, options);

    public static SharedPage Deserialize(string json, JsonSerializerOptions options) => JsonSerializer.Deserialize<SharedPage>(json, options)!;
}
