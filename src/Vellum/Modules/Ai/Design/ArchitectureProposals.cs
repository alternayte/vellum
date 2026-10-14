using System.Text.Json;
using System.Text.RegularExpressions;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Modelling;
using Vellum.Modules.Schemas;
using Vellum.Modules.Trace.Engine;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Ai.Design;

/// <param name="Request">What the system needs, in plain language. At most 4,000 characters.</param>
/// <param name="BranchId">The draft or variant to design against. Null means the main model.</param>
/// <param name="Selection">
/// The IDs of the things selected on the canvas, at most 500. They are listed in the alias
/// table before the rest of a large model. An ID that names nothing is ignored.
/// </param>
public sealed record CreateArchitectureProposalRequest(string? Request, Guid? BranchId, IReadOnlyList<Guid>? Selection = null);

/// <summary>
/// Additions to the main model that the caller wrote. The server checks them as it checks an
/// answer of the assistant, stores them as a draft and changes nothing else.
/// </summary>
/// <param name="Request">What the additions are for, for the person who decides. At most 4,000 characters.</param>
/// <param name="Proposal">
/// The additions in the flat form of <see cref="ArchitectureOutput"/>. Each item has an
/// alias <c>N1</c>, <c>N2</c>, …. A reference (<c>parent</c>, <c>from</c>, <c>to</c>,
/// <c>owner</c>, <c>channel</c>, <c>consumer</c>, <c>producer</c>, <c>consumers</c>) is the
/// ID of a thing of the model, or the alias of another item.
/// </param>
public sealed record SubmitArchitectureProposalRequest(string? Request, ArchitectureOutput? Proposal);

/// <summary>Item IDs. An ID is in one list at most.</summary>
public sealed record DecideArchitectureProposalRequest(IReadOnlyList<Guid>? Accept, IReadOnlyList<Guid>? Reject);

/// <summary>
/// The values of a proposed addition. The members a kind does not use are null.
/// element: <c>kind</c> (actor, system, app, store, component), <c>name</c>, <c>description</c>, <c>technology</c>, <c>parentId</c>.
/// relationship: <c>fromId</c>, <c>toId</c>, <c>label</c>, <c>technology</c>, <c>kind</c> (structural, behavioural).
/// operation: <c>ownerId</c>, <c>name</c>, <c>transport</c> (http, grpc, command).
/// channel: <c>name</c>, <c>kind</c> (topic, queue, exchange).
/// subscription: <c>channelId</c>, <c>consumerId</c>.
/// binding: <c>fromId</c> (the source channel), <c>toId</c> (the target channel), <c>label</c> (the routing key, null for none).
/// message: <c>name</c>, <c>description</c>, <c>producerId</c>, <c>consumerIds</c>.
/// An ID is the real ID of an existing thing, or the ID of another item of the proposal.
/// </summary>
public sealed record ProposedEntity(
    string? Kind, string? Name, string? Description, string? Technology,
    Guid? ParentId, Guid? FromId, Guid? ToId, string? Label,
    Guid? OwnerId, string? Transport, Guid? ChannelId, Guid? ConsumerId,
    Guid? ProducerId, IReadOnlyList<Guid>? ConsumerIds);

/// <param name="Id">The ID of the item. An accepted item is in the model under this ID.</param>
/// <param name="Alias">The name the model gave the item (<c>N1</c>), for display.</param>
/// <param name="Action">Always <c>add</c>.</param>
/// <param name="DependsOn">The items that must be accepted before, or with, this one.</param>
/// <param name="Status"><c>pending</c>, <c>accepted</c> or <c>rejected</c>.</param>
/// <param name="DecidedBy">The user who accepted or rejected the item. Null while it is pending, and in an item decided before this was kept.</param>
/// <param name="DecidedAt">When the item was accepted or rejected.</param>
public sealed record ArchitectureItemDto(
    Guid Id, string Alias, string Action, string EntityKind, string Name, ProposedEntity Proposed,
    IReadOnlyList<Guid> DependsOn, double Confidence, string Rationale, string Status,
    string? DecidedBy = null, DateTimeOffset? DecidedAt = null);

/// <param name="Status"><c>draft</c> while an item is pending, then <c>decided</c>.</param>
/// <param name="Scope">Always <c>additions</c>: this version proposes no change to and no removal of an existing fact.</param>
/// <param name="ResolvedToExisting">Things the model proposed as new that the branch held already when the proposal was made. No item exists for them; the items that referenced them reference the existing thing.</param>
/// <param name="ModelVersion">The version of the branch when the proposal was made.</param>
/// <param name="Stale">True for a draft when the branch is at another version now.</param>
/// <param name="ItemIssues">For a draft: the pending items that cannot be accepted against the model as it is now, with the reason. A decision that accepts one is refused.</param>
/// <param name="Omitted">How many things the alias table of the request left out because the model is large.</param>
/// <param name="OmittedPrivate">Set when private-sourced things were left out of the prompt because a setting forbids sending them.</param>
/// <param name="Origin">
/// <c>ai</c> for a proposal the in-app assistant generated. For a submitted proposal: who
/// submitted it, <c>agent-token:&lt;id&gt;</c> or <c>user:&lt;id&gt;</c>; <c>provider</c> then
/// holds that kind and that ID, and <c>usage</c> holds nothing.
/// </param>
public sealed record ArchitectureProposalDto(
    Guid Id, Guid ProjectId, Guid? BranchId, string Request, string Status, string Scope,
    string Summary, string ReasoningSummary, IReadOnlyList<string> OpenQuestions,
    IReadOnlyList<ArchitectureItemDto> Items,
    IReadOnlyList<ResolvedToExisting> ResolvedToExisting,
    IReadOnlyList<RejectedReference> RejectedReferences,
    long ModelVersion,
    bool Stale,
    IReadOnlyList<ProposalItemFailure> ItemIssues,
    AliasOmitted Omitted,
    OmittedPrivate? OmittedPrivate,
    ProposalProvider Provider, string PromptTemplateVersion, ProposalUsage Usage,
    string CreatedBy, DateTimeOffset CreatedAt, string? DecidedBy, DateTimeOffset? DecidedAt, string Origin);

public sealed record ProposalItemFailure(Guid ItemId, string Reason);

/// <summary>The answer when a decision is refused. No item of the request was applied.</summary>
public sealed record ProposalItemsRefused(string Type, string Title, IReadOnlyList<ProposalItemFailure> Failures);

internal sealed record ArchitectureDocument(
    string Summary, string ReasoningSummary, IReadOnlyList<string> OpenQuestions,
    IReadOnlyList<ArchitectureItemDto> Items, IReadOnlyList<RejectedReference> RejectedReferences,
    long ModelVersion, ProposalProvider Provider, string PromptTemplateVersion, ProposalUsage Usage,
    IReadOnlyList<ResolvedToExisting>? ResolvedToExisting = null, AliasOmitted? Omitted = null, OmittedPrivate? OmittedPrivate = null,
    string? Origin = null);

/// <summary>
/// Natural language to additions to the model, as a proposal. Each item is a suggestion with
/// a confidence and a reason. A person accepts or rejects items; an accepted item is written
/// through the modelling decisions as an inferred fact with its provenance. Nothing that
/// exists is changed or removed, and nothing is written before a person accepts it.
/// </summary>
public static partial class ArchitectureProposals
{
    public const string Kind = "architecture";
    public const string Purpose = "infer-architecture";
    public const int MaxItems = 200;
    public const int MaxOutputTokens = 8_000;

    [GeneratedRegex("^N[1-9][0-9]{0,3}$")]
    private static partial Regex NewAlias();

    public static async Task<IResult> Create(
        Guid projectId, CreateArchitectureProposalRequest body, HttpContext http,
        AiClient ai, IEventStore store, AppDbContext db, PrivateSources privateSources,
        TimeProvider time, CancellationToken ct)
    {
        var (caller, refusal) = await DesignAccess.ResolveAsync(http, projectId, body.BranchId, WorkspaceRole.Editor, forWrite: true, ct);
        if (caller is null)
            return refusal!;
        if (FlowProposals.RequestText(body.Request) is { } invalid)
            return invalid;
        if (body.Selection is { Count: > FlowProposals.MaxSelection })
            return Invalid("selection", $"A selection holds at most {FlowProposals.MaxSelection} IDs");
        // Asked before the provider is called, so a full project spends nothing.
        if (await ProposalStore.DraftsFullAsync(db, projectId, ct))
            return ProposalStore.TooManyDrafts();

        var snapshot = await BranchSnapshot.LoadAsync(store, db, projectId, caller.StreamId, ct);
        var gate = await privateSources.LoadAsync(caller.WorkspaceId, projectId, snapshot.Model, ct);
        var aliases = ModelAliases.Build(snapshot.Model, snapshot.Schemas, AliasNeeds.For(body.Request!, selected: body.Selection), gate);
        var prompt = DesignPrompts.InferArchitecture;

        var result = await ai.CompleteStructuredAsync<ArchitectureOutput>(new AiRequest(
            caller.WorkspaceId, AiTask.InferArchitecture, Purpose, DesignPrompts.InferArchitectureVersion, prompt["system"],
            [new AiMessage(AiRole.User,
            [
                AiPart.Instruction(prompt["table"]), AiPart.Untrusted(aliases.Table, aliases.DataClass),
                AiPart.Instruction(prompt["request"]), AiPart.Untrusted(body.Request!, AiDataClass.Public),
            ])],
            DesignSchemas.Architecture, MaxOutputTokens, caller.UserId), ct);
        if (result is not AiResult<ArchitectureOutput>.Succeeded answer)
            return Results.Ok(AiUnavailable.From(((AiResult<ArchitectureOutput>.Failed)result).Failure));

        var (items, resolved, rejected) = Build(answer.Value, aliases.ByAlias, snapshot.Model);
        var document = new ArchitectureDocument(
            DesignText.Lines(answer.Value.Summary), DesignText.Lines(answer.Value.ReasoningSummary, TextRule.MaxDescription),
            DesignText.List(answer.Value.OpenQuestions, FlowAssembler.MaxQuestions),
            items, rejected, snapshot.Version,
            new ProposalProvider(answer.Usage.ProviderKind, answer.Usage.Model, answer.Usage.ReportedModel),
            DesignPrompts.InferArchitectureVersion, ProposalUsage.From(answer.Usage),
            resolved, aliases.Omitted, OmittedPrivate.Of(aliases.OmittedPrivateCount));

        var name = DesignText.Line(answer.Value.Summary);
        var row = new ProposalEntity
        {
            Id = Guid.NewGuid(),
            Kind = Kind,
            WorkspaceId = caller.WorkspaceId,
            ProjectId = projectId,
            BranchId = body.BranchId,
            // A proposal with no item has nothing to decide.
            Status = items.Count > 0 ? "draft" : "decided",
            Name = name.Length > 0 ? name : "Proposed additions",
            Request = body.Request!,
            Document = JsonSerializer.Serialize(document, DesignJson.Options),
            CreatedBy = caller.UserId,
            CreatedAt = time.GetUtcNow(),
        };
        if (!await ProposalStore.InsertDraftAsync(db, row, ct))
            return ProposalStore.TooManyDrafts();

        return Results.Created(
            $"/api/projects/{projectId}/ai/architecture-proposals/{row.Id}", ToDto(row, document, snapshot));
    }

    /// <summary>
    /// Stores additions that the caller wrote as a draft proposal of the main model. The
    /// items go through the checks of <see cref="Create"/>: an item that is not an addition,
    /// that the model holds already, or that references an ID that names nothing is dropped
    /// and listed in <c>rejectedReferences</c>. Nothing but the proposal row is written: the
    /// model does not change. A person accepts or rejects each item.
    /// </summary>
    public static async Task<IResult> Submit(
        Guid projectId, SubmitArchitectureProposalRequest body, HttpContext http,
        IEventStore store, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var (caller, refusal) = await DesignAccess.ResolveAsync(http, projectId, null, WorkspaceRole.Editor, forWrite: true, ct);
        if (caller is null)
            return refusal!;
        if (FlowProposals.RequestText(body.Request) is { } invalid)
            return invalid;
        if (body.Proposal is not { } proposal)
            return Invalid("proposal", "A proposal is required: an object with summary, items, openQuestions and reasoningSummary");
        if (await ProposalStore.DraftsFullAsync(db, projectId, ct))
            return ProposalStore.TooManyDrafts();

        var snapshot = await BranchSnapshot.LoadAsync(store, db, projectId, caller.StreamId, ct);
        // A caller that leaves the action out proposes an addition. Any other action is dropped by the checks.
        var given = (proposal.Items ?? []).Where(i => i is not null).Take(MaxItems + 1)
            .Select(i => i.Action is null ? i with { Action = "add" } : i).ToList();
        var references = SubmittedProposal.References(
            given.SelectMany(i => new[] { i.Parent, i.From, i.To, i.Owner, i.Channel, i.Consumer, i.Producer }.Concat(i.Consumers ?? []))
                .Select(r => r?.Trim()),
            snapshot.Model);
        var (items, resolved, rejected) = Build(proposal with { Items = given }, references, snapshot.Model, byId: true);

        var origin = SubmittedProposal.OriginOf(http.User);
        var document = new ArchitectureDocument(
            DesignText.Lines(proposal.Summary), DesignText.Lines(proposal.ReasoningSummary, TextRule.MaxDescription),
            DesignText.List(proposal.OpenQuestions, FlowAssembler.MaxQuestions),
            items, rejected, snapshot.Version,
            SubmittedProposal.Provider(origin), SubmittedProposal.NoTemplate, SubmittedProposal.NoUsage,
            resolved, AliasOmitted.None, null, origin);

        var name = DesignText.Line(proposal.Summary);
        var row = new ProposalEntity
        {
            Id = Guid.NewGuid(),
            Kind = Kind,
            WorkspaceId = caller.WorkspaceId,
            ProjectId = projectId,
            BranchId = null,
            // A proposal with no item has nothing to decide.
            Status = items.Count > 0 ? "draft" : "decided",
            Name = name.Length > 0 ? name : "Proposed additions",
            Request = body.Request!,
            Document = JsonSerializer.Serialize(document, DesignJson.Options),
            CreatedBy = caller.UserId,
            Origin = origin,
            CreatedAt = time.GetUtcNow(),
        };
        if (!await ProposalStore.InsertDraftAsync(db, row, ct))
            return ProposalStore.TooManyDrafts();

        return Results.Created(
            $"/api/projects/{projectId}/ai/architecture-proposals/{row.Id}", ToDto(row, document, snapshot));
    }

    /// <param name="mine">True: only the proposals the caller made or submitted.</param>
    /// <param name="decided">True: only decided proposals. False: only drafts, also those with some items decided.</param>
    public static async Task<IResult> List(
        Guid projectId, string? cursor, int? limit, string? status, bool? submitted, bool? mine, bool? decided,
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var (_, userId) = await DesignAccess.RequireAsync(http, projectId, WorkspaceRole.Viewer, ct);
        return ProposalStore.StatusFilter(status) ?? Results.Ok(await ProposalStore.ListAsync(
            db, projectId, Kind, cursor, limit, new ProposalStore.ProposalFilter(status, submitted, mine == true ? userId : null, decided), ct));
    }

    /// <summary>The summary with the item counts of the stored document.</summary>
    internal static ProposalSummary WithCounts(ProposalSummary summary, string document)
    {
        var items = JsonSerializer.Deserialize<ArchitectureDocument>(document, DesignJson.Options)!.Items;
        return summary with
        {
            Items = items.Count,
            AcceptedItems = items.Count(i => i.Status == "accepted"),
            RejectedItems = items.Count(i => i.Status == "rejected"),
        };
    }

    /// <summary>
    /// A draft is read with the branch as it is now: <c>stale</c> says whether the branch
    /// moved, and <c>itemIssues</c> names the pending items that can no longer be accepted.
    /// </summary>
    public static async Task<IResult> Get(
        Guid projectId, Guid proposalId, HttpContext http,
        IEventStore store, AppDbContext db, CancellationToken ct)
    {
        await DesignAccess.RequireAsync(http, projectId, WorkspaceRole.Viewer, ct);
        var row = await db.DesignProposals.AsNoTracking().FirstOrDefaultAsync(p => p.Id == proposalId && p.ProjectId == projectId && p.Kind == Kind, ct);
        if (row is null)
            return NotFound("Proposal");
        if (row.Status != "draft")
            return Results.Ok(ToDto(row, Read(row), null));

        var streamId = await DesignAccess.StreamOfAsync(http, projectId, row.BranchId, ct);
        return Results.Ok(ToDto(row, Read(row), await BranchSnapshot.LoadAsync(store, db, projectId, streamId, ct)));
    }

    /// <summary>Removes a draft that has no decided item. A decided item is a record of a decision and stays.</summary>
    public static async Task<IResult> Delete(Guid projectId, Guid proposalId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        await DesignAccess.RequireAsync(http, projectId, WorkspaceRole.Editor, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await ProposalStore.LockAsync(db, projectId, proposalId, Kind, ct) is not { } row)
            return NotFound("Proposal");
        if (row.Status != "draft")
            return Conflict($"The proposal is {row.Status}. Only a draft can be deleted.");
        if (Read(row).Items.Any(i => i.Status != "pending"))
            return Conflict("The proposal holds a decided item. Reject the pending items to close it; a decision is not deleted.");

        db.DesignProposals.Remove(row);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Accepts and rejects items. The accepted items of one request are one append to the
    /// model stream, in dependency order: all of them are written or none is. An item whose
    /// dependency is neither accepted already nor accepted in the same request is refused,
    /// and so is the whole request.
    /// <para>
    /// The decision is one transaction. It locks the branch stream and the proposal row, and
    /// checks every accepted item against the locked model: an item that references a thing
    /// that is gone, or that the model holds already, is refused by its ID. The append and the
    /// new item statuses commit together or not at all.
    /// </para>
    /// </summary>
    public static async Task<IResult> Decide(
        Guid projectId, Guid proposalId, DecideArchitectureProposalRequest body, HttpContext http, TimeProvider time, CancellationToken ct)
    {
        var (_, userId) = await DesignAccess.RequireAsync(http, projectId, WorkspaceRole.Editor, ct);
        var accept = (body.Accept ?? []).Distinct().ToList();
        var reject = (body.Reject ?? []).Distinct().ToList();
        if (accept.Count + reject.Count == 0)
            return Invalid("accept", "Name at least one item to accept or to reject");
        if (accept.Count + reject.Count > MaxItems)
            return Invalid("accept", $"A proposal holds at most {MaxItems} items");
        if (accept.Intersect(reject).Any())
            return Invalid("reject", "An item cannot be accepted and rejected in one request");

        var branch = await ProposalStore.BranchOfAsync(http, projectId, proposalId, Kind, ct);
        if (branch is null)
            return NotFound("Proposal");

        // Only an accept writes to the branch, so only an accept needs a branch that is open.
        Guid streamId;
        if (accept.Count > 0)
        {
            var (caller, refusal) = await DesignAccess.ResolveAsync(http, projectId, branch.Value.BranchId, WorkspaceRole.Editor, forWrite: true, ct);
            if (caller is null)
                return refusal!;
            streamId = caller.StreamId;
        }
        else
        {
            streamId = await DesignAccess.StreamOfAsync(http, projectId, branch.Value.BranchId, ct);
        }

        return await SharedTransaction.RunAsync(http.RequestServices, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var store = services.GetRequiredService<IEventStore>().ForProject(projectId).UseDbContext(db);

            // The stream lock comes before the row lock in every write of this feature.
            var snapshot = await BranchSnapshot.LockAsync(store, services.GetRequiredService<AppDbContext>(), projectId, streamId, ct);
            if (await ProposalStore.LockAsync(db, projectId, proposalId, Kind, ct) is not { } row)
                return NotFound("Proposal");

            var document = Read(row);
            var items = document.Items.ToDictionary(i => i.Id);
            if (accept.Concat(reject).Where(id => !items.ContainsKey(id)).Take(1).ToList() is [var unknown])
                return Invalid(accept.Contains(unknown) ? "accept" : "reject", $"The proposal has no item {unknown}");

            var failures = new List<ProposalItemFailure>();
            foreach (var id in accept.Concat(reject))
            {
                if (items[id].Status != "pending")
                    failures.Add(new(id, $"The item is {items[id].Status} already. A decided item does not change."));
            }
            foreach (var id in accept.Where(id => items[id].Status == "pending"))
            {
                foreach (var dependency in items[id].DependsOn.Select(d => items[d]))
                {
                    if (reject.Contains(dependency.Id) || dependency.Status == "rejected")
                        failures.Add(new(id, $"The item needs '{dependency.Name}' ({dependency.Alias}), which is rejected."));
                    else if (dependency.Status != "accepted" && !accept.Contains(dependency.Id))
                        failures.Add(new(id, $"The item needs '{dependency.Name}' ({dependency.Alias}), which is not accepted. Accept it first or in the same request."));
                }

                // The model can have changed since the proposal was made.
                failures.AddRange(Vanished(items[id], items, snapshot.Model, accept));
            }
            if (failures.Count > 0)
                return Refused(failures);

            var now = time.GetUtcNow();
            if (accept.Count > 0)
            {
                var additions = new List<(Addition Addition, double Confidence)>();
                foreach (var item in InDependencyOrder(accept.Select(id => items[id]).ToList()))
                {
                    if (ToAddition(item) is not { } addition)
                        return Refused([new(item.Id, "The item does not describe an addition that can be written.")]);
                    additions.Add((addition, item.Confidence));
                }

                var refused = await InferredWrites.ApplyAsync(
                    store, streamId, additions, InferredWrites.Source(document.Provider, document.Origin, Kind, row.Id), userId, now, ct);
                if (refused is not null)
                    return Refused([new(refused.Id, refused.Reason)]);
            }

            // Each item keeps who decided it and when: one proposal can be decided in parts, by different people.
            document = document with
            {
                Items = [.. document.Items.Select(i =>
                    accept.Contains(i.Id) ? i with { Status = "accepted", DecidedBy = userId, DecidedAt = now }
                    : reject.Contains(i.Id) ? i with { Status = "rejected", DecidedBy = userId, DecidedAt = now }
                    : i)],
            };
            row.Document = JsonSerializer.Serialize(document, DesignJson.Options);
            if (document.Items.All(i => i.Status != "pending"))
            {
                row.Status = "decided";
                row.DecidedBy = userId;
                row.DecidedAt = now;
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(ToDto(row, document, null));
        }, ct);
    }

    /// <summary>
    /// The references of an item that name nothing in the model now: an existing thing that
    /// was removed, or an item that was accepted before and was removed since.
    /// </summary>
    private static IEnumerable<ProposalItemFailure> Vanished(
        ArchitectureItemDto item, Dictionary<Guid, ArchitectureItemDto> items, ModelState model, IReadOnlyCollection<Guid> accepting)
    {
        var proposed = item.Proposed;
        // The source and the target of a binding are channels; those of a relationship are elements.
        var binding = item.EntityKind == AliasKinds.Binding;
        IEnumerable<(string What, Guid? Id, bool Channel)> references =
        [
            ("parent", proposed.ParentId, false), ("source", proposed.FromId, binding), ("target", proposed.ToId, binding),
            ("owner", proposed.OwnerId, false), ("channel", proposed.ChannelId, true), ("consumer", proposed.ConsumerId, false),
            ("producer", proposed.ProducerId, false),
            .. (proposed.ConsumerIds ?? []).Select(c => ("consumer", (Guid?)c, false)),
        ];

        foreach (var (what, reference, channel) in references)
        {
            // A reference to an item that is not accepted yet is the dependency rule's to judge.
            if (reference is not { } id || (items.TryGetValue(id, out var other) && (other.Status != "accepted" || accepting.Contains(id))))
                continue;
            if (channel ? !model.Channels.ContainsKey(id) : !model.Elements.ContainsKey(id))
                yield return new(item.Id, $"The {what} of the item ({id}) is not in the model.");
        }
    }

    /// <summary>Why a pending item cannot be accepted against the model as it is now, for each such item.</summary>
    private static List<ProposalItemFailure> ItemIssues(ArchitectureDocument document, ModelState model)
    {
        var items = document.Items.ToDictionary(i => i.Id);
        var pending = document.Items.Where(i => i.Status == "pending").Select(i => i.Id).ToHashSet();
        var issues = new List<ProposalItemFailure>();
        foreach (var item in document.Items.Where(i => i.Status == "pending"))
        {
            issues.AddRange(Vanished(item, items, model, pending));
            var proposed = item.Proposed;
            var thing = new ProposedThing(
                item.EntityKind, proposed.Name ?? "", proposed.Kind,
                proposed.ParentId is { } parent && model.Elements.ContainsKey(parent) ? parent : null,
                proposed.ParentId is { } other && !model.Elements.ContainsKey(other),
                proposed.OwnerId, proposed.ChannelId, proposed.ConsumerId,
                item.EntityKind == AliasKinds.Binding ? proposed.FromId : null,
                item.EntityKind == AliasKinds.Binding ? proposed.ToId : null,
                item.EntityKind == AliasKinds.Binding ? proposed.Label : null);
            if (ExistingFacts.Find(model, thing, except: item.Id) is ExistingMatch.One one)
                issues.Add(new(item.Id, $"The model holds this already: '{one.Name}' ({one.Id})."));
        }

        return issues;
    }

    private static IResult Refused(IReadOnlyList<ProposalItemFailure> failures) => Results.Json(
        new ProposalItemsRefused("items_refused", "The decision was refused. No item of the request was applied.", failures),
        statusCode: StatusCodes.Status409Conflict);

    /// <summary>Each item after the items it depends on. The dependencies have no cycle: <see cref="Build"/> drops one.</summary>
    private static List<ArchitectureItemDto> InDependencyOrder(List<ArchitectureItemDto> items)
    {
        var ordered = new List<ArchitectureItemDto>();
        var placed = new HashSet<Guid>();
        var chosen = items.Select(i => i.Id).ToHashSet();
        while (ordered.Count < items.Count)
        {
            var before = ordered.Count;
            foreach (var item in items.Where(i => !placed.Contains(i.Id)))
            {
                if (item.DependsOn.All(d => placed.Contains(d) || !chosen.Contains(d)) && placed.Add(item.Id))
                    ordered.Add(item);
            }

            if (ordered.Count == before)
                throw new InvalidOperationException("The items of a stored proposal depend on each other in a cycle.");
        }

        return ordered;
    }

    private static Addition? ToAddition(ArchitectureItemDto item)
    {
        var proposed = item.Proposed;
        switch (item.EntityKind)
        {
            case AliasKinds.Element:
                return ModelEnum.TryParseDefined(proposed.Kind, out ElementKind elementKind) && proposed.Name is { } elementName
                    ? new Addition.Element(item.Id, elementKind, elementName, proposed.Description, proposed.Technology, proposed.ParentId)
                    : null;
            case AliasKinds.Relationship:
                return proposed is { FromId: { } from, ToId: { } to } && ModelEnum.TryParseDefined(proposed.Kind, out RelationshipKind relationshipKind)
                    ? new Addition.Relationship(item.Id, from, to, proposed.Label, proposed.Technology, relationshipKind)
                    : null;
            case AliasKinds.Operation:
                return proposed is { OwnerId: { } owner, Name: { } operationName } && ModelEnum.TryParseDefined(proposed.Transport, out Transport transport)
                    ? new Addition.Operation(item.Id, owner, operationName, transport)
                    : null;
            case AliasKinds.Channel:
                return proposed.Name is { } channelName && ModelEnum.TryParseDefined(proposed.Kind, out ChannelKind channelKind)
                    ? new Addition.Channel(item.Id, channelName, channelKind)
                    : null;
            case AliasKinds.Subscription:
                return proposed is { ChannelId: { } channel, ConsumerId: { } consumer } ? new Addition.Subscription(item.Id, channel, consumer) : null;
            case AliasKinds.Binding:
                return proposed is { FromId: { } source, ToId: { } target } ? new Addition.Binding(item.Id, source, target, proposed.Label) : null;
            case AliasKinds.Message:
                return proposed is { ProducerId: { } producer, Name: { } messageName }
                    ? new Addition.Message(item.Id, messageName, proposed.Description, producer, [.. proposed.ConsumerIds ?? []])
                    : null;
            default:
                return null;
        }
    }

    private sealed record Candidate(ArchitectureItemOutput Output, Guid Id, string? Enum);

    /// <summary>
    /// Maps the model's answer to items. The answer is untrusted. An item that is not an
    /// addition, that takes the alias of an existing thing, that has no usable name or kind,
    /// or that references an alias that names nothing is dropped and listed; so is every item
    /// that depends on a dropped item, and every item that depends on itself.
    /// </summary>
    /// <param name="byId">True for a submitted proposal, whose table lists things under their IDs (<see cref="SubmittedProposal.References"/>).</param>
    internal static (List<ArchitectureItemDto> Items, List<ResolvedToExisting> Resolved, List<RejectedReference> Rejected) Build(
        ArchitectureOutput output, IReadOnlyDictionary<string, AliasRef> table, ModelState model, bool byId = false)
    {
        // An item that turns out to exist is added to this copy of the table under its own alias.
        var aliases = new Dictionary<string, AliasRef>(table, StringComparer.Ordinal);
        var resolved = new List<ResolvedToExisting>();
        var rejected = new List<RejectedReference>();
        var given = (output.Items ?? []).Where(i => i is not null).ToList();
        if (given.Count > MaxItems)
            rejected.Add(new("items", "", $"The answer holds {given.Count} items. Only the first {MaxItems} were read."));

        static string? Token(string? text) => DesignText.Line(text, TextRule.MaxIdentifier) is { Length: > 0 } clean ? clean : null;

        var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        foreach (var raw in given.Take(MaxItems))
        {
            var alias = Token(raw.Alias) ?? "";
            var item = raw with
            {
                Alias = alias,
                EntityKind = Token(raw.EntityKind) ?? "",
                Name = ProposedNames.Normalize(DesignText.Line(raw.Name, TextRule.MaxName + 1)),
                // The empty string is the model's word for "not used".
                Description = string.IsNullOrWhiteSpace(raw.Description) ? null : DesignText.Lines(raw.Description, TextRule.MaxDescription),
                Technology = string.IsNullOrWhiteSpace(raw.Technology) ? null : DesignText.Line(raw.Technology),
                Label = string.IsNullOrWhiteSpace(raw.Label) ? null : DesignText.Line(raw.Label),
                ElementKind = Token(raw.ElementKind),
                RelationshipKind = Token(raw.RelationshipKind),
                Transport = Token(raw.Transport),
                ChannelKind = Token(raw.ChannelKind),
                Parent = Token(raw.Parent),
                From = Token(raw.From),
                To = Token(raw.To),
                Owner = Token(raw.Owner),
                Channel = Token(raw.Channel),
                Consumer = Token(raw.Consumer),
                Producer = Token(raw.Producer),
                Consumers = raw.Consumers?.Select(Token).Where(c => c is not null).Select(c => c!).Distinct(StringComparer.Ordinal).Take(50).ToList(),
                Confidence = FlowAssembler.Confidence(raw.Confidence),
                Rationale = DesignText.Lines(raw.Rationale),
            };

            void Reject(string field, string value, string reason) =>
                rejected.Add(new(field.Length == 0 ? $"item:{alias}" : $"item:{alias}.{field}", value, $"{reason} The item was dropped."));

            if (raw.Action != "add")
            {
                Reject("action", Token(raw.Target) ?? alias,
                    "This version proposes additions only. A change to or a removal of an existing fact is never applied from a proposal.");
                continue;
            }
            if (aliases.ContainsKey(alias))
            {
                Reject("", alias, "The alias names a thing that exists. A proposal does not replace an existing fact.");
                continue;
            }
            if (!NewAlias().IsMatch(alias))
            {
                Reject("alias", alias, "A new item takes an alias N1, N2, ...");
                continue;
            }
            if (candidates.ContainsKey(alias))
            {
                Reject("", alias, "The alias is used by an earlier item.");
                continue;
            }

            var named = item.EntityKind is AliasKinds.Element or AliasKinds.Operation or AliasKinds.Channel or AliasKinds.Message;
            if (named && (string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > TextRule.MaxName))
            {
                Reject("name", alias, $"The name must hold 1 to {TextRule.MaxName} characters.");
                continue;
            }

            // The closed set of each kind. A value outside it is not guessed.
            string? kindValue = null;
            var kindOk = item.EntityKind switch
            {
                AliasKinds.Element => Parse<ElementKind>(item.ElementKind, null, out kindValue),
                AliasKinds.Relationship => Parse<RelationshipKind>(item.RelationshipKind, "structural", out kindValue),
                AliasKinds.Operation => Parse<Transport>(item.Transport, "http", out kindValue),
                AliasKinds.Channel => Parse<ChannelKind>(item.ChannelKind, "topic", out kindValue),
                AliasKinds.Subscription or AliasKinds.Binding or AliasKinds.Message => true,
                _ => false,
            };
            if (!kindOk)
            {
                Reject("kind", alias, "The kind, transport or entity kind is not one of the allowed values.");
                continue;
            }

            candidates[alias] = new Candidate(item, Guid.NewGuid(), kindValue);
        }

        static IEnumerable<(string Field, string? Alias, string Kind, bool Required)> References(ArchitectureItemOutput item) => item.EntityKind switch
        {
            AliasKinds.Element => [("parent", item.Parent, AliasKinds.Element, false)],
            AliasKinds.Relationship => [("from", item.From, AliasKinds.Element, true), ("to", item.To, AliasKinds.Element, true)],
            AliasKinds.Operation => [("owner", item.Owner, AliasKinds.Element, true)],
            AliasKinds.Subscription => [("channel", item.Channel, AliasKinds.Channel, true), ("consumer", item.Consumer, AliasKinds.Element, true)],
            AliasKinds.Binding => [("from", item.From, AliasKinds.Channel, true), ("to", item.To, AliasKinds.Channel, true)],
            AliasKinds.Message =>
            [
                ("producer", item.Producer, AliasKinds.Element, true),
                .. (item.Consumers ?? []).Select(c => ("consumers", (string?)c, AliasKinds.Element, true)),
            ],
            _ => [],
        };

        void Prune()
        {
            for (var changed = true; changed;)
            {
                changed = false;
                foreach (var (alias, candidate) in candidates.ToList())
                {
                    foreach (var (field, reference, kind, required) in References(candidate.Output))
                    {
                        var fits = reference is null
                            ? !required
                            : aliases.TryGetValue(reference, out var existing) ? existing.Kind == kind
                            : candidates.TryGetValue(reference, out var other) && other.Output.EntityKind == kind && reference != alias;
                        if (fits)
                            continue;

                        rejected.Add(new($"item:{alias}.{field}", reference ?? "", reference is null
                            ? $"{AliasKinds.WithArticle(candidate.Output.EntityKind)} needs '{field}'. The item was dropped."
                            : byId
                                ? $"The reference is not the ID of {AliasKinds.WithArticle(kind)} of the model and is not the alias of {AliasKinds.WithArticle(kind)} of the proposal. The item was dropped."
                                : Guid.TryParse(reference, out _)
                                    ? "A raw ID is not a reference. A thing is referenced by its alias from the table. The item was dropped."
                                    : $"The alias does not name {AliasKinds.WithArticle(kind)} of the table or of the proposal. The item was dropped."));
                        candidates.Remove(alias);
                        changed = true;
                        break;
                    }
                }
            }
        }

        Prune();

        // A binding joins an exchange or a topic to a queue or an exchange. The kind of each end
        // is known here: an existing channel has one, and a new channel states one.
        ChannelKind? KindOfChannel(string? alias) =>
            alias is null ? null
            : aliases.TryGetValue(alias, out var existing) ? model.Channels.TryGetValue(existing.Id, out var channel) ? channel.Kind : null
            : candidates.TryGetValue(alias, out var other) && ModelEnum.TryParseDefined(other.Enum, out ChannelKind proposedKind) ? proposedKind : null;
        foreach (var (alias, candidate) in candidates.Where(c => c.Value.Output.EntityKind == AliasKinds.Binding).ToList())
        {
            var item = candidate.Output;
            var problem = item.From == item.To ? "The source and the target of a binding are two channels."
                : KindOfChannel(item.From) is { } from && !ModelInvariants.CanRouteFrom(from) ? "The source of a binding is an exchange or a topic."
                : KindOfChannel(item.To) is { } to && !ModelInvariants.CanRouteTo(to) ? "The target of a binding is a queue or an exchange."
                : null;
            if (problem is null)
                continue;
            rejected.Add(new($"item:{alias}", alias, $"{problem} The item was dropped."));
            candidates.Remove(alias);
        }

        // What is left after every item whose dependencies come first has been taken depends on itself.
        var ordered = new List<string>();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var (alias, candidate) in candidates)
            {
                if (!placed.Contains(alias)
                    && References(candidate.Output).All(r => r.Alias is null || aliases.ContainsKey(r.Alias) || placed.Contains(r.Alias))
                    && placed.Add(alias))
                {
                    ordered.Add(alias);
                    changed = true;
                }
            }
        }
        foreach (var alias in candidates.Keys.Where(a => !placed.Contains(a)).ToList())
        {
            rejected.Add(new($"item:{alias}", alias, "The item depends on itself through other items. The item was dropped."));
            candidates.Remove(alias);
        }

        // The server checks each item against the branch, in dependency order, so that an
        // operation on an owner that turns out to exist is compared with that owner's operations.
        foreach (var alias in ordered)
        {
            var item = candidates[alias].Output;
            Guid? Existing(string? reference) => reference is not null && aliases.TryGetValue(reference, out var known) ? known.Id : null;
            var thing = new ProposedThing(
                item.EntityKind, item.Name, candidates[alias].Enum,
                Existing(item.Parent), item.Parent is not null && Existing(item.Parent) is null,
                Existing(item.Owner), Existing(item.Channel), Existing(item.Consumer),
                item.EntityKind == AliasKinds.Binding ? Existing(item.From) : null,
                item.EntityKind == AliasKinds.Binding ? Existing(item.To) : null,
                item.EntityKind == AliasKinds.Binding ? item.Label : null);

            var hasName = item.EntityKind is AliasKinds.Element or AliasKinds.Operation or AliasKinds.Channel or AliasKinds.Message;
            if (hasName && ProposedNames.Problem(model, item.EntityKind, item.Name, thing.OwnerId) is { } problem)
            {
                rejected.Add(new($"item:{alias}.name", alias, $"{problem} The item was dropped."));
                candidates.Remove(alias);
                continue;
            }

            switch (ExistingFacts.Find(model, thing))
            {
                case ExistingMatch.One one:
                    aliases[alias] = new AliasRef(item.EntityKind, one.Id, one.Name);
                    resolved.Add(new ResolvedToExisting(alias, one.Id, one.Name));
                    candidates.Remove(alias);
                    break;
                case ExistingMatch.Ambiguous several:
                    rejected.Add(new($"item:{alias}", alias,
                        $"More than one existing {item.EntityKind} fits ({string.Join(", ", several.Names.Select(n => $"'{n}'"))}). The server does not choose one. The item was dropped."));
                    candidates.Remove(alias);
                    break;
            }
        }

        // An item that referenced a dropped item goes with it.
        Prune();

        Guid? Id(string? alias) => alias is null ? null : aliases.TryGetValue(alias, out var existing) ? existing.Id : candidates[alias].Id;
        string Label(string? alias) => alias is null ? "" : aliases.TryGetValue(alias, out var existing) ? existing.Name : candidates[alias].Output.Name;

        var items = candidates.Values.Select(candidate =>
        {
            var item = candidate.Output;
            var proposed = item.EntityKind switch
            {
                AliasKinds.Element => new ProposedEntity(candidate.Enum, item.Name, item.Description, item.Technology, Id(item.Parent), null, null, null, null, null, null, null, null, null),
                AliasKinds.Relationship => new ProposedEntity(candidate.Enum, null, null, item.Technology, null, Id(item.From), Id(item.To), item.Label, null, null, null, null, null, null),
                AliasKinds.Operation => new ProposedEntity(null, item.Name, null, null, null, null, null, null, Id(item.Owner), candidate.Enum, null, null, null, null),
                AliasKinds.Channel => new ProposedEntity(candidate.Enum, item.Name, null, null, null, null, null, null, null, null, null, null, null, null),
                AliasKinds.Subscription => new ProposedEntity(null, null, null, null, null, null, null, null, null, null, Id(item.Channel), Id(item.Consumer), null, null),
                AliasKinds.Binding => new ProposedEntity(null, null, null, null, null, Id(item.From), Id(item.To), item.Label, null, null, null, null, null, null),
                _ => new ProposedEntity(null, item.Name, item.Description, null, null, null, null, null, null, null, null, null, Id(item.Producer),
                    [.. (item.Consumers ?? []).Select(c => Id(c)!.Value)]),
            };
            var name = item.EntityKind switch
            {
                AliasKinds.Relationship => string.IsNullOrWhiteSpace(item.Label) ? $"{Label(item.From)} → {Label(item.To)}" : $"{Label(item.From)} → {Label(item.To)}: {item.Label}",
                AliasKinds.Subscription => $"{Label(item.Consumer)} on {Label(item.Channel)}",
                AliasKinds.Binding => item.Label is null ? $"{Label(item.From)} to {Label(item.To)}" : $"{Label(item.From)} to {Label(item.To)} on {item.Label}",
                _ => item.Name,
            };
            return new ArchitectureItemDto(
                candidate.Id, item.Alias, "add", item.EntityKind, DesignText.Line(name, 500), proposed,
                [.. References(item).Where(r => r.Alias is not null && candidates.ContainsKey(r.Alias)).Select(r => candidates[r.Alias!].Id).Distinct()],
                item.Confidence, item.Rationale, "pending");
        }).ToList();

        return (items, resolved, rejected.Count <= FlowAssembler.MaxFindings
            ? rejected
            : [.. rejected.Take(FlowAssembler.MaxFindings - 1), new RejectedReference("", "", "More items were dropped; they are not listed.")]);
    }

    /// <summary>Reads a member of a closed set. A missing value takes <paramref name="fallback"/> when the kind has one.</summary>
    private static bool Parse<T>(string? text, string? fallback, out string? value) where T : struct, Enum
    {
        value = null;
        if (!ModelEnum.TryParseDefined(text ?? fallback, out T parsed))
            return false;
        value = parsed.ToString().ToLowerInvariant();
        return true;
    }

    private static ArchitectureDocument Read(ProposalEntity row) =>
        JsonSerializer.Deserialize<ArchitectureDocument>(row.Document, DesignJson.Options)!;

    /// <param name="current">The branch as it is now, for a draft that is read. Null for an answer to a write and for a decided proposal.</param>
    private static ArchitectureProposalDto ToDto(ProposalEntity row, ArchitectureDocument document, BranchSnapshot? current) => new(
        row.Id, row.ProjectId, row.BranchId, row.Request, row.Status, "additions",
        document.Summary, document.ReasoningSummary, document.OpenQuestions, document.Items,
        document.ResolvedToExisting ?? [], document.RejectedReferences,
        document.ModelVersion,
        current is not null && row.Status == "draft" && current.Version != document.ModelVersion,
        current is not null && row.Status == "draft" ? ItemIssues(document, current.Model) : [],
        document.Omitted ?? AliasOmitted.None, document.OmittedPrivate,
        document.Provider, document.PromptTemplateVersion, document.Usage,
        row.CreatedBy, row.CreatedAt, row.DecidedBy, row.DecidedAt, document.Origin ?? SubmittedProposal.AiOrigin);
}
