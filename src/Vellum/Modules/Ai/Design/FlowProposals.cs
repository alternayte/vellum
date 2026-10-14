using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Schemas;
using Vellum.Modules.Trace.Engine;
using Vellum.Modules.Trace.Scenarios;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Ai.Design;

/// <param name="Request">What the flow must show, in plain language. At most 4,000 characters.</param>
/// <param name="BranchId">The draft or variant to design against. Null means the main model.</param>
/// <param name="ReviseScenarioId">A scenario of the project to revise. Null proposes a new scenario.</param>
/// <param name="Selection">
/// The IDs of the things selected on the canvas, at most 500. They are listed in the alias
/// table before the rest of a large model. An ID that names nothing is ignored.
/// </param>
public sealed record CreateFlowProposalRequest(string? Request, Guid? BranchId, Guid? ReviseScenarioId, IReadOnlyList<Guid>? Selection = null);

/// <summary>
/// A flow proposal that the caller wrote, for the main model. The server checks it as it
/// checks an answer of the assistant, stores it as a draft and changes nothing else.
/// </summary>
/// <param name="Request">What the flow shows and why it is proposed, for the person who decides. At most 4,000 characters.</param>
/// <param name="ReviseScenarioId">A scenario of the project that the flow replaces when a person accepts it. Null proposes a new scenario.</param>
/// <param name="Flow">
/// The flow in the flat form of <see cref="FlowOutput"/>. A reference (<c>from</c>,
/// <c>operation</c>, <c>channel</c>, <c>message</c>, <c>by</c>, <c>store</c>, the
/// <c>slotKey</c> of a handler, a <c>target</c>) is the ID of a thing of the model, or the
/// alias (<c>N1</c>, <c>N2</c>, …) of an entry of <c>missing</c>. A payload is JSON text.
/// </param>
public sealed record SubmitFlowProposalRequest(string? Request, Guid? ReviseScenarioId, FlowOutput? Flow);

/// <param name="AcceptWithIssues">
/// True accepts the proposal as it resolves against the model now, when that differs from
/// what the proposal showed when it was last stored. Without it such an accept is refused
/// with <c>proposal_stale</c>.
/// </param>
public sealed record AcceptFlowProposalRequest(string? Name, bool AcceptWithIssues = false);

public sealed record AcceptFlowProposalResponse(Guid ScenarioId);

/// <summary>
/// The answer to an accept (409) when the model changed after the proposal was stored and
/// the proposal now resolves differently. Nothing was written. Read the proposal again, or
/// send the accept with <c>acceptWithIssues: true</c>.
/// </summary>
/// <param name="Type">Always <c>proposal_stale</c>.</param>
/// <param name="Issues">The issues of the proposal against the model as it is now.</param>
/// <param name="Preview">The playback against the model as it is now, or null when there is none.</param>
/// <param name="ModelVersion">The version of the branch that the issues and the preview were computed against.</param>
public sealed record ProposalStale(string Type, string Title, IReadOnlyList<ScenarioIssue> Issues, FlowPreview? Preview, long ModelVersion);

/// <summary>What a list of proposals shows for one proposal.</summary>
/// <param name="Origin"><c>ai</c> for a proposal the in-app assistant generated. For a submitted proposal: <c>agent-token:&lt;id&gt;</c> or <c>user:&lt;id&gt;</c>.</param>
public sealed record ProposalSummary(
    Guid Id, string Status, string Name, string Request, Guid? BranchId,
    string CreatedBy, DateTimeOffset CreatedAt, string? DecidedBy, DateTimeOffset? DecidedAt, string Origin);

/// <summary>
/// A playback of the proposed scenario on the branch. It is declared behaviour replayed over
/// declared contracts, not a measurement.
/// </summary>
public sealed record FlowPreview(string Hash, int EventCount, IReadOnlyDictionary<string, StepOutcome> Outcomes);

/// <param name="Id">Null for a missing thing that is not created yet.</param>
public sealed record AliasTarget(string Alias, Guid? Id, string? Name);

/// <summary>
/// A thing the flow needs that the model does not hold. <c>POST …/missing/{alias}/create</c>
/// adds it to the model as an inferred fact; until then the steps that use it are blocked.
/// </summary>
/// <param name="Owner">The owner of an operation, or the producer of a message.</param>
/// <param name="CreatedId">The ID in the model once the thing is created, else null.</param>
/// <param name="CreateAfter">The missing aliases that must be created before this one.</param>
public sealed record FlowMissingDto(
    string Alias, string Kind, string Name, string? ElementKind, string? Transport, string? ChannelKind,
    AliasTarget? Parent, AliasTarget? Owner, AliasTarget? Channel, AliasTarget? Consumer,
    string Reason, double Confidence, Guid? CreatedId, IReadOnlyList<string> CreateAfter);

public sealed record FlowEvidenceDto(string Alias, Guid Id, string Name, string Why);

/// <param name="Scenario">The scenario document with real IDs. It holds only the steps whose references all resolve.</param>
/// <param name="ResolvedToExisting">Things the model proposed as new that the branch holds already. The proposal uses the existing thing.</param>
/// <param name="Issues">What the answer's structure and the scenario validator report. A scenario with issues has no preview.</param>
/// <param name="Preview">Null when the scenario has an issue or no step.</param>
/// <param name="Diff">Null unless the proposal revises a scenario.</param>
/// <param name="ModelVersion">The version of the branch when the proposal was last stored: when it was made, or when a missing thing was created.</param>
/// <param name="Stale">
/// True for a draft when the branch is at another version now. The scenario, the issues, the
/// preview and the lists of this answer are then computed against the model as it is now.
/// </param>
/// <param name="Omitted">How many things the alias table of the request left out because the model is large.</param>
/// <param name="OmittedPrivate">Set when private-sourced things were left out of the prompt because a setting forbids sending them.</param>
/// <param name="Origin">
/// <c>ai</c> for a proposal the in-app assistant generated. For a submitted proposal: who
/// submitted it, <c>agent-token:&lt;id&gt;</c> or <c>user:&lt;id&gt;</c>; <c>provider</c> then
/// holds that kind and that ID, and <c>usage</c> holds nothing.
/// </param>
public sealed record FlowProposalDto(
    Guid Id, Guid ProjectId, Guid? BranchId, string Request, string Status,
    string Name, string Summary, string ReasoningSummary, IReadOnlyList<string> OpenQuestions,
    Scenario Scenario,
    IReadOnlyList<BlockedStep> BlockedSteps,
    IReadOnlyList<FlowMissingDto> Missing,
    IReadOnlyList<ResolvedToExisting> ResolvedToExisting,
    IReadOnlyList<RejectedReference> RejectedReferences,
    IReadOnlyList<ScenarioIssue> Issues,
    FlowPreview? Preview,
    FlowDiff? Diff,
    IReadOnlyList<FlowEvidenceDto> Evidence,
    Guid? ReviseScenarioId,
    long ModelVersion,
    bool Stale,
    AliasOmitted Omitted,
    OmittedPrivate? OmittedPrivate,
    ProposalProvider Provider, string PromptTemplateVersion, ProposalUsage Usage,
    string CreatedBy, DateTimeOffset CreatedAt, string? DecidedBy, DateTimeOffset? DecidedAt, string Origin);

/// <summary>What a flow proposal resolves to against one version of a branch.</summary>
internal sealed record FlowView(
    Scenario Scenario,
    IReadOnlyList<BlockedStep> BlockedSteps,
    IReadOnlyList<FlowMissingDto> Missing,
    IReadOnlyList<ResolvedToExisting> ResolvedToExisting,
    IReadOnlyList<RejectedReference> RejectedReferences,
    IReadOnlyList<ScenarioIssue> Issues,
    FlowPreview? Preview, FlowDiff? Diff, IReadOnlyList<FlowEvidenceDto> Evidence);

/// <summary>
/// The stored form of a flow proposal. It holds the proposal once.
/// <para>
/// A draft holds what it resolves from: the model's bounded answer (<see cref="Output"/>),
/// the part of the alias table the answer names, the missing aliases a person created and
/// the carried parts of a revised scenario. The scenario, the issues and the preview are not
/// stored; <see cref="FlowProposals.Resolve"/> computes them from these and a branch.
/// <see cref="Fingerprint"/> identifies what that gave at <see cref="ModelVersion"/>, so a
/// later resolution can be compared with it.
/// </para>
/// <para>
/// A decided proposal holds what it resolved to when it was decided (<see cref="Decided"/>)
/// and only the texts of the answer. It is not resolved again.
/// </para>
/// A proposal written before this form holds the answer and the alias table under the same
/// names, so it reads as a draft's inputs; the members it lacks are null.
/// <para>
/// <see cref="Origin"/> is null for a proposal the assistant generated. A submitted proposal
/// names who submitted it, and its table lists things under their IDs.
/// </para>
/// </summary>
internal sealed record FlowDocument(
    Guid ScenarioId, Guid? ReviseScenarioId, string? BaseScenarioHash,
    FlowOutput Output, IReadOnlyList<ScenarioIssue>? IngestIssues,
    IReadOnlyDictionary<string, AliasRef>? Aliases, IReadOnlyDictionary<string, Guid>? Created,
    FlowCarried? Carried, FlowView? Decided, string? Fingerprint,
    long ModelVersion,
    ProposalProvider Provider, string PromptTemplateVersion, ProposalUsage Usage,
    AliasOmitted? Omitted, OmittedPrivate? OmittedPrivate, string? Origin = null);

/// <summary>A branch's model at one version, with the content of the schemas it names.</summary>
internal sealed record BranchSnapshot(ModelState Model, long Version, IReadOnlyDictionary<SchemaId, string> Schemas)
{
    public static async Task<BranchSnapshot> LoadAsync(
        IEventStore store, AppDbContext db, Guid projectId, Guid streamId, CancellationToken ct)
    {
        var (model, version) = await store.Load<ModelState>(StreamId.From(streamId), ct);
        return await OfAsync(model, version, db, projectId, ct);
    }

    /// <summary>
    /// Locks the branch stream in the transaction of <paramref name="pinned"/> and reads it.
    /// The model cannot change until that transaction ends.
    /// </summary>
    public static async Task<BranchSnapshot> LockAsync(
        IEventStore pinned, AppDbContext db, Guid projectId, Guid streamId, CancellationToken ct)
    {
        // Execute with no events locks the stream and returns its state.
        var locked = await pinned.Execute<ModelState>(StreamId.From(streamId), _ => [], ct);
        return await OfAsync(locked.State, locked.Version, db, projectId, ct);
    }

    private static async Task<BranchSnapshot> OfAsync(
        ModelState model, long version, AppDbContext db, Guid projectId, CancellationToken ct)
    {
        // Only the project's own schemas count, as in a scenario run.
        var referenced = ContractDecider.ReferencedSchemaIds(model);
        var schemas = await db.Schemas.AsNoTracking()
            .Where(s => s.ProjectId == projectId && referenced.Contains(s.Id))
            .Select(s => new { s.Id, s.Content })
            .ToListAsync(ct);
        return new BranchSnapshot(model, version, schemas.ToDictionary(s => new SchemaId(s.Id), s => s.Content));
    }
}

/// <summary>
/// Natural language to a scenario, as a proposal. The model's answer is checked and stored as
/// a draft; nothing else changes. A person previews it, creates the missing things one request
/// at a time, and accepts or rejects it. Only an accept writes a scenario, through the same
/// decision as a person's own save. A generation that fails stores nothing.
/// <para>
/// A write (accept, create a missing thing) is one transaction: it locks the branch stream,
/// locks the proposal row, resolves the proposal against the locked model, appends, and
/// updates the row. So what a person accepts is what the model holds at that moment, and the
/// append and the row commit together or not at all.
/// </para>
/// </summary>
public static class FlowProposals
{
    public const string Kind = "flow";
    public const string Purpose = "propose-flow";
    public const int MaxRequestChars = 4_000;
    public const int MaxOutputTokens = 8_000;
    public const int MaxSelection = 500;

    public static async Task<IResult> Create(
        Guid projectId, CreateFlowProposalRequest body, HttpContext http,
        AiClient ai, IEventStore store, AppDbContext db, PrivateSources privateSources,
        TimeProvider time, CancellationToken ct)
    {
        var (caller, refusal) = await DesignAccess.ResolveAsync(http, projectId, body.BranchId, WorkspaceRole.Editor, forWrite: true, ct);
        if (caller is null)
            return refusal!;
        if (RequestText(body.Request) is { } invalid)
            return invalid;
        if (body.Selection is { Count: > MaxSelection })
            return Invalid("selection", $"A selection holds at most {MaxSelection} IDs");
        // Asked before the provider is called, so a full project spends nothing.
        if (await ProposalStore.DraftsFullAsync(db, projectId, ct))
            return ProposalStore.TooManyDrafts();

        Scenario? revised = null;
        if (body.ReviseScenarioId is { } reviseId)
        {
            revised = (await ScenarioStreams.LoadAsync(store, reviseId, ct)).DocumentIn(projectId);
            if (revised is null)
                return NotFound("Scenario");
        }

        var snapshot = await BranchSnapshot.LoadAsync(store, db, projectId, caller.StreamId, ct);
        var gate = await privateSources.LoadAsync(caller.WorkspaceId, projectId, snapshot.Model, ct);
        var aliases = ModelAliases.Build(
            snapshot.Model, snapshot.Schemas,
            AliasNeeds.For(body.Request!, revised is null ? null : References(revised), body.Selection), gate);

        var prompt = DesignPrompts.ProposeFlow;
        var parts = new List<AiPart> { AiPart.Instruction(prompt["table"]), AiPart.Untrusted(aliases.Table, aliases.DataClass) };
        var carried = FlowCarried.None;
        if (revised is not null)
        {
            var split = FlowAssembler.SplitRevised(revised, aliases.AliasOf, snapshot.Model);
            carried = split.Carried;
            parts.Add(AiPart.Instruction(prompt["scenario"]));
            // The scenario holds aliases and the payloads a person saved. It holds no ID and no text of a hidden thing.
            parts.Add(AiPart.Untrusted(RevisedAsData(revised, split), AiDataClass.Public));
        }
        // The request is what a person typed or pasted. It is data too, in a part of its own.
        parts.Add(AiPart.Instruction(prompt["request"]));
        parts.Add(AiPart.Untrusted(body.Request!, AiDataClass.Public));

        var result = await ai.CompleteStructuredAsync<FlowOutput>(new AiRequest(
            caller.WorkspaceId, AiTask.ProposeFlow, Purpose, DesignPrompts.ProposeFlowVersion, prompt["system"],
            [new AiMessage(AiRole.User, parts)], DesignSchemas.Flow, MaxOutputTokens, caller.UserId), ct);
        if (result is not AiResult<FlowOutput>.Succeeded answer)
            return Results.Ok(AiUnavailable.From(((AiResult<FlowOutput>.Failed)result).Failure));

        var (output, ingestIssues) = FlowAssembler.Normalize(answer.Value);
        var document = new FlowDocument(
            body.ReviseScenarioId ?? Guid.NewGuid(), body.ReviseScenarioId, revised is null ? null : ScenarioHash(revised),
            output, ingestIssues, Referenced(output, aliases), new Dictionary<string, Guid>(),
            carried, null, null, snapshot.Version,
            new ProposalProvider(answer.Usage.ProviderKind, answer.Usage.Model, answer.Usage.ReportedModel),
            DesignPrompts.ProposeFlowVersion, ProposalUsage.From(answer.Usage),
            aliases.Omitted, OmittedPrivate.Of(aliases.OmittedPrivateCount));
        var view = Resolve(document, snapshot, revised);
        document = document with { Fingerprint = Fingerprint(view) };

        var row = new ProposalEntity
        {
            Id = Guid.NewGuid(),
            Kind = Kind,
            WorkspaceId = caller.WorkspaceId,
            ProjectId = projectId,
            BranchId = body.BranchId,
            Status = "draft",
            Name = output.Name,
            Request = body.Request!,
            Document = Write(document),
            CreatedBy = caller.UserId,
            CreatedAt = time.GetUtcNow(),
        };
        if (!await ProposalStore.InsertDraftAsync(db, row, ct))
            return ProposalStore.TooManyDrafts();

        return Results.Created($"/api/projects/{projectId}/ai/flow-proposals/{row.Id}", ToDto(row, document, view, stale: false));
    }

    /// <summary>
    /// Stores a flow that the caller wrote as a draft proposal of the main model. The flow goes
    /// through the checks of <see cref="Create"/>: every reference must name a thing of the
    /// model or an entry of <c>missing</c>, and a reference that names nothing is dropped
    /// with what holds it and listed in <c>rejectedReferences</c>. Nothing but the proposal
    /// row is written: no scenario, and nothing in the model. A person accepts or rejects it.
    /// </summary>
    public static async Task<IResult> Submit(
        Guid projectId, SubmitFlowProposalRequest body, HttpContext http,
        IEventStore store, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var (caller, refusal) = await DesignAccess.ResolveAsync(http, projectId, null, WorkspaceRole.Editor, forWrite: true, ct);
        if (caller is null)
            return refusal!;
        if (RequestText(body.Request) is { } invalid)
            return invalid;
        if (body.Flow is null)
            return Invalid("flow", "A flow is required: an object with name, summary, steps, faults, assertions, missing, openQuestions, reasoningSummary and evidence");
        if (await ProposalStore.DraftsFullAsync(db, projectId, ct))
            return ProposalStore.TooManyDrafts();

        Scenario? revised = null;
        if (body.ReviseScenarioId is { } reviseId)
        {
            revised = (await ScenarioStreams.LoadAsync(store, reviseId, ct)).DocumentIn(projectId);
            if (revised is null)
                return NotFound("Scenario");
        }

        var snapshot = await BranchSnapshot.LoadAsync(store, db, projectId, caller.StreamId, ct);
        var (output, ingestIssues) = FlowAssembler.Normalize(body.Flow);
        var origin = SubmittedProposal.OriginOf(http.User);
        var document = new FlowDocument(
            body.ReviseScenarioId ?? Guid.NewGuid(), body.ReviseScenarioId, revised is null ? null : ScenarioHash(revised),
            output, ingestIssues, SubmittedProposal.References(Named(output), snapshot.Model), new Dictionary<string, Guid>(),
            FlowCarried.None, null, null, snapshot.Version,
            SubmittedProposal.Provider(origin), SubmittedProposal.NoTemplate, SubmittedProposal.NoUsage,
            AliasOmitted.None, null, origin);
        var view = Resolve(document, snapshot, revised);
        document = document with { Fingerprint = Fingerprint(view) };

        var row = new ProposalEntity
        {
            Id = Guid.NewGuid(),
            Kind = Kind,
            WorkspaceId = caller.WorkspaceId,
            ProjectId = projectId,
            BranchId = null,
            Status = "draft",
            Name = output.Name,
            Request = body.Request!,
            Document = Write(document),
            CreatedBy = caller.UserId,
            Origin = origin,
            CreatedAt = time.GetUtcNow(),
        };
        if (!await ProposalStore.InsertDraftAsync(db, row, ct))
            return ProposalStore.TooManyDrafts();

        return Results.Created($"/api/projects/{projectId}/ai/flow-proposals/{row.Id}", ToDto(row, document, view, stale: false));
    }

    public static async Task<IResult> List(
        Guid projectId, string? cursor, int? limit, string? status, bool? submitted, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        await DesignAccess.RequireAsync(http, projectId, WorkspaceRole.Viewer, ct);
        return ProposalStore.StatusFilter(status) ?? Results.Ok(await ProposalStore.ListAsync(db, projectId, Kind, cursor, limit, status, submitted, ct));
    }

    /// <summary>
    /// A decided proposal reads as it was decided. A draft is resolved against the branch as
    /// it is now; <c>stale</c> says whether that is another version than the stored one.
    /// </summary>
    public static async Task<IResult> Get(
        Guid projectId, Guid proposalId, HttpContext http,
        IEventStore store, AppDbContext db, CancellationToken ct)
    {
        await DesignAccess.RequireAsync(http, projectId, WorkspaceRole.Viewer, ct);
        var row = await db.DesignProposals.AsNoTracking().FirstOrDefaultAsync(p => p.Id == proposalId && p.ProjectId == projectId && p.Kind == Kind, ct);
        if (row is null)
            return NotFound("Proposal");

        var document = Read(row);
        if (document.Decided is { } decided)
            return Results.Ok(ToDto(row, document, decided, stale: false));

        var streamId = await DesignAccess.StreamOfAsync(http, projectId, row.BranchId, ct);
        var snapshot = await BranchSnapshot.LoadAsync(store, db, projectId, streamId, ct);
        var view = Resolve(document, snapshot, await RevisedAsync(store, projectId, document, ct));
        return Results.Ok(ToDto(row, document, view, stale: row.Status == "draft" && snapshot.Version != document.ModelVersion));
    }

    /// <summary>
    /// Writes the proposed scenario: a new one, or the scenario the proposal revises when that
    /// scenario is as it was when the proposal was made. The proposal is resolved against the
    /// locked model first. When that differs from what the proposal showed when it was stored,
    /// the accept is refused with <see cref="ProposalStale"/> unless the request says
    /// <c>acceptWithIssues</c>; the scenario that is written is always the fresh resolution.
    /// </summary>
    public static async Task<IResult> Accept(
        Guid projectId, Guid proposalId, AcceptFlowProposalRequest? body, HttpContext http, TimeProvider time, CancellationToken ct)
    {
        var (_, userId) = await DesignAccess.RequireAsync(http, projectId, WorkspaceRole.Editor, ct);
        if (body?.Name is { } requested && (string.IsNullOrWhiteSpace(requested) || TextRule.Line("name", requested) is not null))
        {
            return Invalid("name",
                TextRule.Line("name", requested)?.Message ?? "A name must not be blank");
        }

        var branch = await ProposalStore.BranchOfAsync(http, projectId, proposalId, Kind, ct);
        if (branch is null)
            return NotFound("Proposal");
        var streamId = await DesignAccess.StreamOfAsync(http, projectId, branch.Value.BranchId, ct);

        return await SharedTransaction.RunAsync(http.RequestServices, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var store = services.GetRequiredService<IEventStore>().UseDbContext(db);

            // The stream lock comes before the row lock in every write of this feature.
            var snapshot = await BranchSnapshot.LockAsync(store, services.GetRequiredService<AppDbContext>(), projectId, streamId, ct);
            if (await ProposalStore.LockAsync(db, projectId, proposalId, Kind, ct) is not { } row)
                return NotFound("Proposal");
            if (row.Status != "draft")
                return Conflict($"The proposal is {row.Status}. A decided proposal does not change.");

            var document = Read(row);
            var revised = await RevisedAsync(store, projectId, document, ct);
            var view = Resolve(document, snapshot, revised);
            if (IsStale(document, snapshot, view) && body?.AcceptWithIssues != true)
            {
                return Results.Json(
                    new ProposalStale("proposal_stale",
                        "The model changed after the proposal was stored, and the proposal resolves differently now. Nothing was saved. Read the proposal again, or accept with acceptWithIssues.",
                        view.Issues, view.Preview, snapshot.Version),
                    statusCode: StatusCodes.Status409Conflict);
            }

            if (view.Scenario.Steps.Count == 0)
            {
                return Conflict(
                    "The proposal has no step whose references all resolve, so there is no scenario to save. Create the missing things, or reject the proposal.");
            }
            if (view.Issues.FirstOrDefault(i => i.Code == "not-storable") is { } unstorable)
                return Conflict($"The proposed scenario cannot be stored. {unstorable.Message}");

            var scenario = view.Scenario with { Name = body?.Name ?? row.Name };
            var origin = new ScenarioOrigin(
                document.Origin is null ? ScenarioOrigin.AiProposal : ScenarioOrigin.SubmittedProposal, row.Id, document.Provider.Kind, document.Provider.Model);
            CommandResult<Scenario> written;
            if (document.ReviseScenarioId is { } reviseId)
            {
                try
                {
                    written = await store.DecideInTransaction<ScenarioState, Scenario>(
                        reviseId,
                        state => state.DocumentIn(projectId) switch
                        {
                            null => new CommandResult<IReadOnlyList<object>>.NotFound("The scenario that the proposal revises no longer exists"),
                            { } current when ScenarioHash(current) != document.BaseScenarioHash => new CommandResult<IReadOnlyList<object>>.Conflict(
                                "The scenario changed after the proposal was made. The proposal no longer applies to it. Ask for a new proposal."),
                            _ => ScenarioDecider.Update(state, projectId, scenario, origin),
                        },
                        state => state.Document!,
                        ct);
                }
                // The ID names a stream of another kind. It was a scenario when the proposal was made.
                catch (DeedboxException e) when (e.Code == DeedboxError.StreamTypeMismatch)
                {
                    written = new CommandResult<Scenario>.NotFound("The scenario that the proposal revises no longer exists");
                }
            }
            else
            {
                written = await SaveScenario.CreateInTransactionAsync(db, store, projectId, scenario, origin, ct);
            }

            if (written is not CommandResult<Scenario>.Success)
                return written is CommandResult<Scenario>.NotFound gone ? Conflict(gone.Message) : written.ToHttpResult();

            row.Status = "accepted";
            row.Name = scenario.Name;
            row.DecidedBy = userId;
            row.DecidedAt = time.GetUtcNow();
            row.Document = Write(Decide(document, view));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new AcceptFlowProposalResponse(scenario.Id));
        }, ct);
    }

    public static async Task<IResult> Reject(
        Guid projectId, Guid proposalId, HttpContext http,
        IEventStore store, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var (_, userId) = await DesignAccess.RequireAsync(http, projectId, WorkspaceRole.Editor, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await ProposalStore.LockAsync(db, projectId, proposalId, Kind, ct) is not { } row)
            return NotFound("Proposal");
        if (row.Status != "draft")
            return Conflict($"The proposal is {row.Status}. A decided proposal does not change.");

        // A rejection writes nothing to the model, so it reads the branch without a lock.
        var document = Read(row);
        var streamId = await DesignAccess.StreamOfAsync(http, projectId, row.BranchId, ct);
        var snapshot = await BranchSnapshot.LoadAsync(store, db, projectId, streamId, ct);
        var view = Resolve(document, snapshot, await RevisedAsync(store, projectId, document, ct));

        row.Status = "rejected";
        row.DecidedBy = userId;
        row.DecidedAt = time.GetUtcNow();
        document = Decide(document, view);
        row.Document = Write(document);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(ToDto(row, document, view, stale: false));
    }

    /// <summary>Removes a draft. A decided proposal is a record of a decision and stays.</summary>
    public static async Task<IResult> Delete(Guid projectId, Guid proposalId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        await DesignAccess.RequireAsync(http, projectId, WorkspaceRole.Editor, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await ProposalStore.LockAsync(db, projectId, proposalId, Kind, ct) is not { } row)
            return NotFound("Proposal");
        if (row.Status != "draft")
            return Conflict($"The proposal is {row.Status}. Only a draft can be deleted.");

        db.DesignProposals.Remove(row);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Adds one missing thing to the model as an inferred fact, then resolves the proposal
    /// again: steps that waited only for it move into the scenario, and the validation and
    /// the preview run again. One request creates one thing. A thing that the model holds by
    /// now is not created: the request is refused with the existing thing's name and ID.
    /// </summary>
    public static async Task<IResult> CreateMissing(
        Guid projectId, Guid proposalId, string alias, HttpContext http, TimeProvider time, CancellationToken ct)
    {
        await DesignAccess.RequireAsync(http, projectId, WorkspaceRole.Editor, ct);
        var branch = await ProposalStore.BranchOfAsync(http, projectId, proposalId, Kind, ct);
        if (branch is null)
            return NotFound("Proposal");
        var (caller, refusal) = await DesignAccess.ResolveAsync(http, projectId, branch.Value.BranchId, WorkspaceRole.Editor, forWrite: true, ct);
        if (caller is null)
            return refusal!;

        return await SharedTransaction.RunAsync(http.RequestServices, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var store = services.GetRequiredService<IEventStore>().ForProject(projectId).UseDbContext(db);

            var snapshot = await BranchSnapshot.LockAsync(store, db, projectId, caller.StreamId, ct);
            if (await ProposalStore.LockAsync(db, projectId, proposalId, Kind, ct) is not { } row)
                return NotFound("Proposal");
            if (row.Status != "draft")
                return Conflict($"The proposal is {row.Status}. A decided proposal does not change.");

            var document = Read(row);
            var revised = await RevisedAsync(store, projectId, document, ct);
            var view = Resolve(document, snapshot, revised);
            if (view.ResolvedToExisting.FirstOrDefault(r => r.Alias == alias) is { } existing)
                return Conflict($"{alias} exists in the model already: '{existing.Name}' ({existing.Id}). The proposal uses it. Nothing was created.");
            if (view.Missing.FirstOrDefault(m => m.Alias == alias) is not { } entry)
                return NotFound("Missing entry");
            if (entry.CreatedId is not null)
                return Conflict($"{alias} is created already.");
            if (entry.CreateAfter.Count > 0)
                return Conflict($"{alias} needs {string.Join(", ", entry.CreateAfter)} to exist. Create {(entry.CreateAfter.Count == 1 ? "it" : "them")} first.");

            // The ID follows from the proposal and the alias, so it is the same in every attempt of this unit.
            var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{row.Id:N}:{alias}")).AsSpan(0, 16));
            if (ToAddition(entry, id) is not { } addition)
            {
                return Conflict(
                    $"{alias} cannot be created from the proposal: its kind, transport or references are not usable. Add it to the model by hand.");
            }

            var refused = await InferredWrites.ApplyAsync(
                store, caller.StreamId, [(addition, entry.Confidence)],
                InferredWrites.Source(document.Provider, document.Origin, Kind, row.Id), caller.UserId, time.GetUtcNow(), ct);
            if (refused is not null)
                return refused.Conflict ? Conflict(refused.Reason) : Invalid(alias, refused.Reason);

            // The stream is locked already; this reads the model with the addition in it.
            snapshot = await BranchSnapshot.LockAsync(store, db, projectId, caller.StreamId, ct);
            document = document with { Created = new Dictionary<string, Guid>(document.Created ?? new Dictionary<string, Guid>()) { [alias] = id } };
            view = Resolve(document, snapshot, revised);
            document = document with { ModelVersion = snapshot.Version, Fingerprint = Fingerprint(view) };

            row.Document = Write(document);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(ToDto(row, document, view, stale: false));
        }, ct);
    }

    /// <summary>
    /// Maps the stored answer through its alias table again and checks the result against the
    /// model of the snapshot. It is a function of the stored inputs and the snapshot, so the
    /// same inputs give the same view. Nothing here throws for what a model wrote.
    /// </summary>
    internal static FlowView Resolve(FlowDocument document, BranchSnapshot snapshot, Scenario? revised)
    {
        var created = document.Created ?? new Dictionary<string, Guid>();
        var (withCarried, table, carryIssues) = FlowAssembler.WithCarried(
            document.Output, document.Aliases ?? new Dictionary<string, AliasRef>(), document.Carried);
        var (output, aliases, resolved, matchRejected) = FlowAssembler.MatchExisting(withCarried, table, created, snapshot.Model);
        var build = FlowAssembler.Build(output, aliases, created, document.ScenarioId, submitted: document.Origin is not null);

        // "not-storable" decides whether the proposal can be accepted. It comes first, so the bound below does not cut it.
        var issues = new List<ScenarioIssue>([
            .. build.Issues.Where(i => i.Code == "not-storable"),
            .. document.IngestIssues ?? [], .. carryIssues,
            .. build.Issues.Where(i => i.Code != "not-storable")]);
        FlowPreview? preview = null;
        if (!build.Issues.Any(i => i.Code == "not-storable"))
        {
            var trace = TraceEngine.Run(TraceModelBuilder.Build(snapshot.Model, snapshot.Schemas), build.Scenario);
            issues.AddRange(trace.Issues);
            if (trace.IsValid && build.Scenario.Steps.Count > 0)
                preview = new FlowPreview(trace.Hash, trace.Events.Count, new SortedDictionary<string, StepOutcome>(
                    trace.Outcomes.ToDictionary(o => o.Key, o => o.Value), StringComparer.Ordinal));
        }
        if (issues.Count > FlowAssembler.MaxFindings)
        {
            issues = [.. issues.Take(FlowAssembler.MaxFindings - 1),
                new ScenarioIssue("", "more-issues", $"{issues.Count - FlowAssembler.MaxFindings + 1} more issues exist; they are not listed.")];
        }

        var rejected = new List<RejectedReference>([.. matchRejected, .. build.Rejected]);
        if (rejected.Count > FlowAssembler.MaxFindings)
            rejected = [.. rejected.Take(FlowAssembler.MaxFindings - 1), new RejectedReference("", "", "More references were rejected; they are not listed.")];

        AliasTarget? Target(string? alias)
        {
            if (alias is null)
                return null;
            if (aliases.TryGetValue(alias, out var named))
                return new AliasTarget(alias, named.Id, named.Name);
            return build.ValidMissing.TryGetValue(alias, out var declared)
                ? new AliasTarget(alias, created.TryGetValue(alias, out var id) ? id : null, declared.Name)
                : null;
        }

        var missing = build.ValidMissing.Values
            .OrderBy(m => int.Parse(m.Alias.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture))
            .Select(m => new FlowMissingDto(
                m.Alias, m.Kind,
                m.Kind == AliasKinds.Subscription
                    ? $"{Target(m.ConsumerAlias)?.Name} on {Target(m.ChannelAlias)?.Name}"
                    : m.Name,
                m.Kind == AliasKinds.Element ? m.ElementKind : null,
                m.Kind == AliasKinds.Operation ? m.Transport ?? "http" : null,
                m.Kind == AliasKinds.Channel ? m.ChannelKind ?? "topic" : null,
                m.Kind == AliasKinds.Element ? Target(m.ParentAlias) : null,
                m.Kind is AliasKinds.Operation or AliasKinds.Message ? Target(m.OwnerAlias) : null,
                m.Kind == AliasKinds.Subscription ? Target(m.ChannelAlias) : null,
                m.Kind == AliasKinds.Subscription ? Target(m.ConsumerAlias) : null,
                m.Reason, m.Confidence,
                created.TryGetValue(m.Alias, out var createdId) ? createdId : null,
                [.. FlowAssembler.MissingReferences(m)
                    .Where(r => r.Alias is not null && build.ValidMissing.ContainsKey(r.Alias) && !created.ContainsKey(r.Alias))
                    .Select(r => r.Alias!).Distinct(StringComparer.Ordinal)]))
            .ToList();

        var evidence = output.Evidence
            .Where(e => aliases.ContainsKey(e.Alias))
            .Select(e => new FlowEvidenceDto(e.Alias, aliases[e.Alias].Id, aliases[e.Alias].Name, e.Why))
            .ToList();

        return new FlowView(
            build.Scenario, build.Blocked, missing, resolved, rejected, issues, preview,
            revised is null ? null : FlowAssembler.Diff(revised, build.Scenario), evidence);
    }

    /// <summary>
    /// Identifies what a proposal resolves to: the scenario, the issues, the preview and the
    /// lists a person decides on. Two resolutions with the same fingerprint show the same proposal.
    /// </summary>
    internal static string Fingerprint(FlowView view) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        scenario = ScenarioHash(view.Scenario),
        view.Issues,
        preview = view.Preview?.Hash,
        view.BlockedSteps,
        missing = view.Missing.Select(m => new { m.Alias, m.CreatedId, m.CreateAfter }),
        view.ResolvedToExisting,
        view.RejectedReferences,
    }, DesignJson.Options)));

    /// <summary>
    /// True when the proposal resolves to something else than it did when it was stored. A
    /// proposal stored before fingerprints existed is compared by the version of the branch.
    /// </summary>
    private static bool IsStale(FlowDocument document, BranchSnapshot snapshot, FlowView view) =>
        document.Fingerprint is { } stored ? stored != Fingerprint(view) : snapshot.Version != document.ModelVersion;

    /// <summary>The stored form of a decided proposal: the view it was decided with and the texts of the answer.</summary>
    private static FlowDocument Decide(FlowDocument document, FlowView view) => document with
    {
        Output = document.Output with { Steps = [], Faults = [], Assertions = [], Missing = [], Evidence = [] },
        IngestIssues = null,
        Aliases = null,
        Carried = null,
        Decided = view,
        Fingerprint = null,
    };

    private static async Task<Scenario?> RevisedAsync(IEventStore store, Guid projectId, FlowDocument document, CancellationToken ct) =>
        document.ReviseScenarioId is { } reviseId
            ? (await ScenarioStreams.LoadAsync(store, reviseId, ct)).DocumentIn(projectId)
            : null;

    /// <summary>The addition a missing entry asks for, or null when the entry does not describe one.</summary>
    private static Addition? ToAddition(FlowMissingDto entry, Guid id)
    {
        switch (entry.Kind)
        {
            case AliasKinds.Element:
                return ModelEnum.TryParseDefined(entry.ElementKind, out ElementKind elementKind)
                    ? new Addition.Element(id, elementKind, entry.Name, null, null, entry.Parent?.Id)
                    : null;
            case AliasKinds.Operation:
                return entry.Owner?.Id is { } owner && ModelEnum.TryParseDefined(entry.Transport, out Transport transport)
                    ? new Addition.Operation(id, owner, entry.Name, transport)
                    : null;
            case AliasKinds.Channel:
                return ModelEnum.TryParseDefined(entry.ChannelKind, out ChannelKind channelKind)
                    ? new Addition.Channel(id, entry.Name, channelKind)
                    : null;
            case AliasKinds.Subscription:
                return entry.Channel?.Id is { } channel && entry.Consumer?.Id is { } consumer
                    ? new Addition.Subscription(id, channel, consumer)
                    : null;
            case AliasKinds.Message:
                return entry.Owner?.Id is { } producer ? new Addition.Message(id, entry.Name, null, producer, []) : null;
            default:
                return null;
        }
    }

    /// <summary>Every reference of an answer, as it is written.</summary>
    private static IEnumerable<string?> Named(FlowOutput output) =>
    [
        .. output.Steps.SelectMany(s => new[] { s.From, s.Operation, s.Channel, s.Message, s.By, s.Store, s.SlotKey }),
        .. output.Faults.Select(f => f.Target),
        .. output.Assertions.Select(a => a.Target),
        .. output.Missing.SelectMany(m => new[] { m.ParentAlias, m.OwnerAlias, m.ChannelAlias, m.ConsumerAlias }),
        .. output.Evidence.Select(e => e.Alias),
    ];

    /// <summary>The part of the alias table that the answer names. Only that part is stored.</summary>
    private static Dictionary<string, AliasRef> Referenced(FlowOutput output, ModelAliases aliases)
    {
        var referenced = new Dictionary<string, AliasRef>(StringComparer.Ordinal);
        foreach (var alias in Named(output))
        {
            if (aliases.Find(alias) is { } found)
                referenced[alias!] = found;
        }

        return referenced;
    }

    /// <summary>Every thing of the model that a scenario references.</summary>
    private static HashSet<Guid> References(Scenario scenario)
    {
        var ids = new HashSet<Guid>();
        string Note(Guid id)
        {
            ids.Add(id);
            return "";
        }

        FlowAssembler.Flatten(scenario, Note);
        FlowAssembler.FlattenChecks(scenario, Note);
        return ids;
    }

    /// <summary>
    /// The scenario to revise as the model reads it: flat steps, aliases for IDs, no ID of the
    /// model. <c>kept</c> lists the steps that are not shown and stay as they are.
    /// </summary>
    private static string RevisedAsData(Scenario scenario, RevisedFlow split) => JsonSerializer.Serialize(new
    {
        name = scenario.Name,
        steps = split.Steps,
        faults = split.Faults,
        assertions = split.Assertions,
        kept = split.Carried.Steps.Select(s => new { id = s.Id, parentStepId = s.ParentStepId, slot = s.Slot, order = s.Order }),
    }, DesignJson.Options);

    /// <summary>A hash of a scenario document that does not depend on how its payloads are spelled.</summary>
    internal static string ScenarioHash(Scenario scenario)
    {
        using var document = JsonDocument.Parse(
            JsonSerializer.SerializeToUtf8Bytes(scenario, DesignJson.Options), new JsonDocumentOptions { MaxDepth = ScenarioShape.MaxJsonDepth });
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = ScenarioShape.MaxJsonDepth }))
            CanonicalJson.Write(writer, document.RootElement);
        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    internal static IResult? RequestText(string? request)
    {
        if (string.IsNullOrWhiteSpace(request))
            return Invalid("request", "A request is required");
        return TextRule.Lines("request", request, MaxRequestChars) is { } error ? Invalid(error.Field, error.Message) : null;
    }

    private static FlowDocument Read(ProposalEntity row) => JsonSerializer.Deserialize<FlowDocument>(row.Document, DesignJson.Options)!;

    private static string Write(FlowDocument document) => JsonSerializer.Serialize(document, DesignJson.Options);

    private static FlowProposalDto ToDto(ProposalEntity row, FlowDocument document, FlowView view, bool stale) => new(
        row.Id, row.ProjectId, row.BranchId, row.Request, row.Status,
        row.Name, document.Output.Summary, document.Output.ReasoningSummary, document.Output.OpenQuestions,
        view.Scenario with { Name = row.Name }, view.BlockedSteps, view.Missing, view.ResolvedToExisting, view.RejectedReferences, view.Issues,
        view.Preview, view.Diff, view.Evidence, document.ReviseScenarioId, document.ModelVersion, stale,
        document.Omitted ?? AliasOmitted.None, document.OmittedPrivate,
        document.Provider, document.PromptTemplateVersion, document.Usage,
        row.CreatedBy, row.CreatedAt, row.DecidedBy, row.DecidedAt, document.Origin ?? SubmittedProposal.AiOrigin);
}

/// <summary>Reads and limits that both kinds of proposal share.</summary>
internal static class ProposalStore
{
    /// <summary>
    /// The most undecided proposals of both kinds that one project holds. A draft holds a
    /// model's answer, and every generation costs money, so drafts do not pile up without end.
    /// </summary>
    public const int MaxDraftsPerProject = 50;

    // Any constant works; it keeps this lock apart from other advisory locks in the database.
    private const int DraftLockSpace = 0x41494450;

    public static async Task<bool> DraftsFullAsync(AppDbContext db, Guid projectId, CancellationToken ct) =>
        await db.DesignProposals.CountAsync(p => p.ProjectId == projectId && p.Status == "draft", ct) >= MaxDraftsPerProject;

    public static IResult TooManyDrafts() => Results.Conflict(new ErrorResponse(
        "too_many_drafts",
        $"The project holds {MaxDraftsPerProject} undecided proposals, which is the most it can hold.",
        "Accept, reject or delete a draft proposal in the app, then ask again. Only a person does that. Nothing was generated and nothing was stored."));

    /// <summary>
    /// Stores a proposal. A draft is counted and inserted under a lock on the project, so two
    /// requests cannot both take the last place.
    /// </summary>
    /// <returns>False when the project holds the most drafts already. Nothing was stored.</returns>
    public static async Task<bool> InsertDraftAsync(AppDbContext db, ProposalEntity row, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({DraftLockSpace}, hashtext({row.ProjectId.ToString()}))", ct);
        if (row.Status == "draft" && await DraftsFullAsync(db, row.ProjectId, ct))
            return false;

        db.DesignProposals.Add(row);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private static readonly string[] Statuses = ["draft", "accepted", "rejected", "decided"];

    /// <summary>Null when <paramref name="status"/> is absent or is a status a proposal can have, else the 400.</summary>
    public static IResult? StatusFilter(string? status) =>
        status is null || Statuses.Contains(status, StringComparer.Ordinal)
            ? null
            : Invalid("status", $"A status is one of: {string.Join(", ", Statuses)}");

    /// <summary>The newest first. The cursor is the creation time and the ID of the last item of a page.</summary>
    /// <param name="status">Only the proposals with this status.</param>
    /// <param name="submitted">True: only the proposals that a caller submitted. False: only those the assistant generated.</param>
    public static async Task<Page<ProposalSummary>> ListAsync(
        AppDbContext db, Guid projectId, string kind, string? cursor, int? limit, string? status, bool? submitted, CancellationToken ct)
    {
        var pageSize = Math.Clamp(limit ?? 50, 1, 200);
        var query = db.DesignProposals.AsNoTracking().Where(p => p.ProjectId == projectId && p.Kind == kind);
        if (status is not null)
            query = query.Where(p => p.Status == status);
        if (submitted is { } wanted)
            query = query.Where(p => (p.Origin != null) == wanted);

        if (CursorEncoder.DecodeNumber(cursor) is { } decoded)
        {
            if (decoded.Number > DateTimeOffset.MaxValue.UtcTicks)
                throw new InvalidCursorException();
            var before = new DateTimeOffset(decoded.Number, TimeSpan.Zero);
            var afterId = decoded.Id;
            query = query.Where(p => p.CreatedAt < before || (p.CreatedAt == before && p.Id.CompareTo(afterId) > 0));
        }

        var items = await query
            .OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id)
            .Take(pageSize + 1)
            .Select(p => new ProposalSummary(
                p.Id, p.Status, p.Name, p.Request, p.BranchId, p.CreatedBy, p.CreatedAt, p.DecidedBy, p.DecidedAt, p.Origin ?? SubmittedProposal.AiOrigin))
            .ToListAsync(ct);

        string? next = null;
        if (items.Count > pageSize)
        {
            items = items[..pageSize];
            next = CursorEncoder.Encode(
                items[^1].CreatedAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture), items[^1].Id);
        }

        return new Page<ProposalSummary>(items, next);
    }

    /// <summary>
    /// The branch of a proposal, read without a lock. A proposal keeps its branch for life, so
    /// a write can find the stream to lock before it locks the row. Null when the project has
    /// no such proposal.
    /// </summary>
    public static async Task<(Guid? BranchId, bool Found)?> BranchOfAsync(
        HttpContext http, Guid projectId, Guid proposalId, string kind, CancellationToken ct)
    {
        var found = await http.RequestServices.GetRequiredService<AppDbContext>().DesignProposals.AsNoTracking()
            .Where(p => p.Id == proposalId && p.ProjectId == projectId && p.Kind == kind)
            .Select(p => new { p.BranchId })
            .FirstOrDefaultAsync(ct);
        return found is null ? null : (found.BranchId, true);
    }

    /// <summary>The proposal row, locked until the caller's transaction ends. Null when the project has no such proposal.</summary>
    public static async Task<ProposalEntity?> LockAsync(AppDbContext db, Guid projectId, Guid proposalId, string kind, CancellationToken ct) =>
        // Read as a list: a further operator would wrap the statement, and the lock clause belongs to the outer query.
        (await db.DesignProposals
            .FromSql($"SELECT * FROM ai.design_proposals WHERE id = {proposalId} AND project_id = {projectId} AND kind = {kind} FOR UPDATE")
            .ToListAsync(ct)).FirstOrDefault();
}
