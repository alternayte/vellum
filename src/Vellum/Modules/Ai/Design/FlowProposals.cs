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

namespace Vellum.Modules.Ai.Design;

/// <param name="Request">What the flow must show, in plain language. At most 4,000 characters.</param>
/// <param name="BranchId">The draft or variant to design against. Null means the main model.</param>
/// <param name="ReviseScenarioId">A scenario of the project to revise. Null proposes a new scenario.</param>
public sealed record CreateFlowProposalRequest(string? Request, Guid? BranchId, Guid? ReviseScenarioId);

public sealed record AcceptFlowProposalRequest(string? Name);

public sealed record AcceptFlowProposalResponse(Guid ScenarioId);

/// <summary>What a list of proposals shows for one proposal.</summary>
public sealed record ProposalSummary(
    Guid Id, string Status, string Name, string Request, Guid? BranchId,
    string CreatedBy, DateTimeOffset CreatedAt, string? DecidedBy, DateTimeOffset? DecidedAt);

/// <summary>
/// A playback of the proposed scenario on the branch at <see cref="FlowProposalDto.ModelVersion"/>.
/// It is declared behaviour replayed over declared contracts, not a measurement.
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
/// <param name="Issues">What the answer's structure and the scenario validator report. A scenario with issues has no preview.</param>
/// <param name="Preview">Null when the scenario has an issue or no step.</param>
/// <param name="Diff">Null unless the proposal revises a scenario.</param>
/// <param name="ModelVersion">The version of the branch stream that the references, the issues and the preview were computed against.</param>
public sealed record FlowProposalDto(
    Guid Id, Guid ProjectId, Guid? BranchId, string Request, string Status,
    string Name, string Summary, string ReasoningSummary, IReadOnlyList<string> OpenQuestions,
    Scenario Scenario,
    IReadOnlyList<BlockedStep> BlockedSteps,
    IReadOnlyList<FlowMissingDto> Missing,
    IReadOnlyList<RejectedReference> RejectedReferences,
    IReadOnlyList<ScenarioIssue> Issues,
    FlowPreview? Preview,
    FlowDiff? Diff,
    IReadOnlyList<FlowEvidenceDto> Evidence,
    Guid? ReviseScenarioId,
    long ModelVersion,
    ProposalProvider Provider, string PromptTemplateVersion, ProposalUsage Usage,
    string CreatedBy, DateTimeOffset CreatedAt, string? DecidedBy, DateTimeOffset? DecidedAt);

/// <summary>
/// The stored form of a flow proposal: the model's bounded answer and the alias table it was
/// written against, so that the proposal can be resolved again after a missing thing is
/// created, plus what the last resolution gave.
/// </summary>
internal sealed record FlowDocument(
    Guid ScenarioId, Guid? ReviseScenarioId, string? BaseScenarioHash,
    FlowOutput Output, IReadOnlyList<ScenarioIssue> IngestIssues,
    IReadOnlyDictionary<string, AliasRef> Aliases, IReadOnlyDictionary<string, Guid> Created,
    Scenario Scenario,
    IReadOnlyList<BlockedStep> BlockedSteps,
    IReadOnlyList<FlowMissingDto> Missing,
    IReadOnlyList<RejectedReference> RejectedReferences,
    IReadOnlyList<ScenarioIssue> Issues,
    FlowPreview? Preview, FlowDiff? Diff, IReadOnlyList<FlowEvidenceDto> Evidence,
    long ModelVersion,
    ProposalProvider Provider, string PromptTemplateVersion, ProposalUsage Usage);

/// <summary>A branch's model at one version, with the content of the schemas it names.</summary>
internal sealed record BranchSnapshot(ModelState Model, long Version, IReadOnlyDictionary<SchemaId, string> Schemas)
{
    public static async Task<BranchSnapshot> LoadAsync(
        IEventStore store, SchemasDbContext schemasDb, Guid projectId, Guid streamId, CancellationToken ct)
    {
        var (model, version) = await store.Load<ModelState>(StreamId.From(streamId), ct);

        // Only the project's own schemas count, as in a scenario run.
        var referenced = ContractDecider.ReferencedSchemaIds(model);
        var schemas = await schemasDb.Schemas.AsNoTracking()
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
/// </summary>
public static class FlowProposals
{
    public const string Kind = "flow";
    public const string Purpose = "propose-flow";
    public const int MaxRequestChars = 4_000;
    public const int MaxOutputTokens = 8_000;

    public static async Task<IResult> Create(
        Guid projectId, CreateFlowProposalRequest body, HttpContext http,
        AiClient ai, IEventStore store, SchemasDbContext schemasDb, AiDesignDbContext db, CancellationToken ct)
    {
        var (caller, refusal) = await DesignAccess.ResolveAsync(http, projectId, body.BranchId, WorkspaceRole.Editor, forWrite: true, ct);
        if (caller is null)
            return refusal!;
        if (RequestText(body.Request) is { } invalid)
            return invalid;

        Scenario? revised = null;
        if (body.ReviseScenarioId is { } reviseId)
        {
            revised = (await ScenarioStreams.LoadAsync(store, reviseId, ct)).DocumentIn(projectId);
            if (revised is null)
                return DesignAccess.NotFound("Scenario");
        }

        var snapshot = await BranchSnapshot.LoadAsync(store, schemasDb, projectId, caller.StreamId, ct);
        var aliases = ModelAliases.Build(snapshot.Model, snapshot.Schemas);

        var prompt = DesignPrompts.ProposeFlow;
        var parts = new List<AiPart> { AiPart.Instruction(prompt["table"]), AiPart.Untrusted(aliases.Table, AiDataClass.Public) };
        if (revised is not null)
        {
            parts.Add(AiPart.Instruction(prompt["scenario"]));
            parts.Add(AiPart.Untrusted(RevisedAsData(revised, aliases), AiDataClass.Public));
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
        var document = Resolve(
            new FlowDocument(
                body.ReviseScenarioId ?? Guid.NewGuid(), body.ReviseScenarioId, revised is null ? null : ScenarioHash(revised),
                output, ingestIssues, Referenced(output, aliases), new Dictionary<string, Guid>(),
                null!, [], [], [], [], null, null, [], snapshot.Version,
                new ProposalProvider(answer.Usage.ProviderKind, answer.Usage.Model, answer.Usage.ReportedModel),
                DesignPrompts.ProposeFlowVersion, ProposalUsage.From(answer.Usage)),
            snapshot, revised);

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
            Document = JsonSerializer.Serialize(document, DesignJson.Options),
            CreatedBy = caller.UserId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Proposals.Add(row);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/projects/{projectId}/ai/flow-proposals/{row.Id}", ToDto(row, document));
    }

    public static async Task<IResult> List(
        Guid projectId, string? cursor, int? limit, HttpContext http, AiDesignDbContext db, CancellationToken ct)
    {
        await DesignAccess.RequireAsync(http, projectId, WorkspaceRole.Viewer, ct);
        return Results.Ok(await ProposalStore.ListAsync(db, projectId, Kind, cursor, limit, ct));
    }

    public static async Task<IResult> Get(Guid projectId, Guid proposalId, HttpContext http, AiDesignDbContext db, CancellationToken ct)
    {
        await DesignAccess.RequireAsync(http, projectId, WorkspaceRole.Viewer, ct);
        var row = await db.Proposals.AsNoTracking().FirstOrDefaultAsync(p => p.Id == proposalId && p.ProjectId == projectId && p.Kind == Kind, ct);
        return row is null ? DesignAccess.NotFound("Proposal") : Results.Ok(ToDto(row, Read(row)));
    }

    /// <summary>
    /// Writes the proposed scenario: a new one, or the scenario the proposal revises when that
    /// scenario is as it was when the proposal was made. The proposal row is locked from the
    /// check of its status to the commit of the new status, so two accepts take turns.
    /// </summary>
    public static async Task<IResult> Accept(
        Guid projectId, Guid proposalId, AcceptFlowProposalRequest? body, HttpContext http,
        IEventStore store, AiDesignDbContext db, CancellationToken ct)
    {
        var (_, userId) = await DesignAccess.RequireAsync(http, projectId, WorkspaceRole.Editor, ct);
        if (body?.Name is { } requested && (string.IsNullOrWhiteSpace(requested) || TextRule.Line("name", requested) is not null))
        {
            return DesignAccess.Invalid("name",
                TextRule.Line("name", requested)?.Message ?? "A name must not be blank");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await ProposalStore.LockAsync(db, projectId, proposalId, Kind, ct) is not { } row)
            return DesignAccess.NotFound("Proposal");
        if (row.Status != "draft")
            return DesignAccess.Conflict($"The proposal is {row.Status}. A decided proposal does not change.");

        var document = Read(row);
        if (document.Scenario.Steps.Count == 0)
        {
            return DesignAccess.Conflict(
                "The proposal has no step whose references all resolve, so there is no scenario to save. Create the missing things, or reject the proposal.");
        }
        if (document.Issues.FirstOrDefault(i => i.Code == "not-storable") is { } unstorable)
            return DesignAccess.Conflict($"The proposed scenario cannot be stored. {unstorable.Message}");

        var scenario = document.Scenario with { Name = body?.Name ?? row.Name };
        CommandResult<Scenario> written;
        if (document.ReviseScenarioId is { } reviseId)
        {
            var proposed = ScenarioHash(scenario);
            written = await ScenarioStreams.DecideAsync(
                store, reviseId,
                state => state.DocumentIn(projectId) switch
                {
                    null => new CommandResult<IReadOnlyList<object>>.NotFound("The scenario that the proposal revises no longer exists"),
                    // The same accept, sent again after its answer was lost.
                    { } current when ScenarioHash(current) == proposed => new CommandResult<IReadOnlyList<object>>.Success([]),
                    { } current when ScenarioHash(current) != document.BaseScenarioHash => new CommandResult<IReadOnlyList<object>>.Conflict(
                        "The scenario changed after the proposal was made. The proposal no longer applies to it. Ask for a new proposal."),
                    _ => ScenarioDecider.Update(state, projectId, scenario),
                },
                state => state.Document!,
                new CommandResult<Scenario>.NotFound("The scenario that the proposal revises no longer exists"),
                ct);
        }
        else
        {
            written = await SaveScenario.CreateAsync(http.RequestServices, projectId, scenario, ct);
            // The scenario ID was made for this proposal. A scenario of this project with that
            // ID is the one an earlier accept wrote before its answer was lost.
            if (written is CommandResult<Scenario>.Conflict { Message: ScenarioDecider.IdInUse }
                && (await ScenarioStreams.LoadAsync(store, scenario.Id, ct)).DocumentIn(projectId) is { } existing)
                written = new CommandResult<Scenario>.Success(existing);
        }

        if (written is not CommandResult<Scenario>.Success)
            return written is CommandResult<Scenario>.NotFound gone ? DesignAccess.Conflict(gone.Message) : written.ToHttpResult();

        row.Status = "accepted";
        row.Name = scenario.Name;
        row.DecidedBy = userId;
        row.DecidedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new AcceptFlowProposalResponse(scenario.Id));
    }

    public static async Task<IResult> Reject(Guid projectId, Guid proposalId, HttpContext http, AiDesignDbContext db, CancellationToken ct)
    {
        var (_, userId) = await DesignAccess.RequireAsync(http, projectId, WorkspaceRole.Editor, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await ProposalStore.LockAsync(db, projectId, proposalId, Kind, ct) is not { } row)
            return DesignAccess.NotFound("Proposal");
        if (row.Status != "draft")
            return DesignAccess.Conflict($"The proposal is {row.Status}. A decided proposal does not change.");

        row.Status = "rejected";
        row.DecidedBy = userId;
        row.DecidedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(ToDto(row, Read(row)));
    }

    /// <summary>
    /// Adds one missing thing to the model as an inferred fact, then resolves the proposal
    /// again: steps that waited only for it move into the scenario, and the validation and
    /// the preview run again. One request creates one thing.
    /// </summary>
    public static async Task<IResult> CreateMissing(
        Guid projectId, Guid proposalId, string alias, HttpContext http,
        IEventStore store, SchemasDbContext schemasDb, AiDesignDbContext db, CancellationToken ct)
    {
        await DesignAccess.RequireAsync(http, projectId, WorkspaceRole.Editor, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await ProposalStore.LockAsync(db, projectId, proposalId, Kind, ct) is not { } row)
            return DesignAccess.NotFound("Proposal");
        if (row.Status != "draft")
            return DesignAccess.Conflict($"The proposal is {row.Status}. A decided proposal does not change.");

        var document = Read(row);
        if (document.Missing.FirstOrDefault(m => m.Alias == alias) is not { } entry)
            return DesignAccess.NotFound("Missing entry");
        if (entry.CreatedId is not null)
            return DesignAccess.Conflict($"{alias} is created already.");
        if (entry.CreateAfter.Count > 0)
            return DesignAccess.Conflict($"{alias} needs {string.Join(", ", entry.CreateAfter)} to exist. Create {(entry.CreateAfter.Count == 1 ? "it" : "them")} first.");

        var (caller, refusal) = await DesignAccess.ResolveAsync(http, projectId, row.BranchId, WorkspaceRole.Editor, forWrite: true, ct);
        if (caller is null)
            return refusal!;

        // The ID follows from the proposal and the alias, so a request that is sent again
        // after its answer was lost names the same thing and adds nothing.
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{row.Id:N}:{alias}")).AsSpan(0, 16));
        if (ToAddition(entry, id) is not { } addition)
        {
            return DesignAccess.Conflict(
                $"{alias} cannot be created from the proposal: its kind, transport or references are not usable. Add it to the model by hand.");
        }

        var refused = await InferredWrites.ApplyAsync(
            store, projectId, caller.StreamId, [(addition, entry.Confidence)],
            InferredWrites.Source(document.Provider, Kind, row.Id), caller.UserId, ct);
        if (refused is not null)
            return refused.Conflict ? DesignAccess.Conflict(refused.Reason) : DesignAccess.Invalid(alias, refused.Reason);

        var created = new Dictionary<string, Guid>(document.Created) { [alias] = id };
        var snapshot = await BranchSnapshot.LoadAsync(store, schemasDb, projectId, caller.StreamId, ct);
        var revised = document.ReviseScenarioId is { } reviseId
            ? (await ScenarioStreams.LoadAsync(store, reviseId, ct)).DocumentIn(projectId)
            : null;
        document = Resolve(document with { Created = created }, snapshot, revised);

        row.Document = JsonSerializer.Serialize(document, DesignJson.Options);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(ToDto(row, document));
    }

    /// <summary>
    /// Maps the stored answer through its alias table again and checks the result against the
    /// model as it is now. It is a function of the stored answer, the created aliases and the
    /// snapshot, so the same inputs give the same proposal.
    /// </summary>
    internal static FlowDocument Resolve(FlowDocument document, BranchSnapshot snapshot, Scenario? revised)
    {
        var build = FlowAssembler.Build(document.Output, document.Aliases, document.Created, document.ScenarioId);

        var issues = new List<ScenarioIssue>([.. document.IngestIssues, .. build.Issues]);
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

        AliasTarget? Target(string? alias)
        {
            if (alias is null)
                return null;
            if (document.Aliases.TryGetValue(alias, out var named))
                return new AliasTarget(alias, named.Id, named.Name);
            return build.ValidMissing.TryGetValue(alias, out var declared)
                ? new AliasTarget(alias, document.Created.TryGetValue(alias, out var id) ? id : null, declared.Name)
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
                document.Created.TryGetValue(m.Alias, out var createdId) ? createdId : null,
                [.. FlowAssembler.MissingReferences(m)
                    .Where(r => r.Alias is not null && build.ValidMissing.ContainsKey(r.Alias) && !document.Created.ContainsKey(r.Alias))
                    .Select(r => r.Alias!).Distinct(StringComparer.Ordinal)]))
            .ToList();

        var evidence = document.Output.Evidence
            .Where(e => document.Aliases.ContainsKey(e.Alias))
            .Select(e => new FlowEvidenceDto(e.Alias, document.Aliases[e.Alias].Id, document.Aliases[e.Alias].Name, e.Why))
            .ToList();

        return document with
        {
            Scenario = build.Scenario,
            BlockedSteps = build.Blocked,
            Missing = missing,
            RejectedReferences = build.Rejected,
            Issues = issues,
            Preview = preview,
            Diff = revised is null ? null : FlowAssembler.Diff(revised, build.Scenario),
            Evidence = evidence,
            ModelVersion = snapshot.Version,
        };
    }

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

    /// <summary>The part of the alias table that the answer names. Only that part is stored.</summary>
    private static Dictionary<string, AliasRef> Referenced(FlowOutput output, ModelAliases aliases)
    {
        IEnumerable<string?> named =
        [
            .. output.Steps.SelectMany(s => new[] { s.From, s.Operation, s.Channel, s.Message, s.By, s.Store, s.SlotKey }),
            .. output.Faults.Select(f => f.Target),
            .. output.Assertions.Select(a => a.Target),
            .. output.Missing.SelectMany(m => new[] { m.ParentAlias, m.OwnerAlias, m.ChannelAlias, m.ConsumerAlias }),
            .. output.Evidence.Select(e => e.Alias),
        ];

        var referenced = new Dictionary<string, AliasRef>(StringComparer.Ordinal);
        foreach (var alias in named)
        {
            if (aliases.Find(alias) is { } found)
                referenced[alias!] = found;
        }

        return referenced;
    }

    /// <summary>The scenario to revise as the model reads it: flat steps, aliases for IDs, no ID of the model.</summary>
    private static string RevisedAsData(Scenario scenario, ModelAliases aliases)
    {
        // An ID that names nothing in this branch has no alias. If the model repeats the
        // word, the server treats it as any other invented reference.
        string Alias(Guid id) => aliases.AliasOf(id) ?? "UNKNOWN";
        var (faults, assertions) = FlowAssembler.FlattenChecks(scenario, Alias);
        return JsonSerializer.Serialize(
            new { name = scenario.Name, steps = FlowAssembler.Flatten(scenario, Alias), faults, assertions }, DesignJson.Options);
    }

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
            return DesignAccess.Invalid("request", "A request is required");
        return TextRule.Lines("request", request, MaxRequestChars) is { } error ? DesignAccess.Invalid(error.Field, error.Message) : null;
    }

    private static FlowDocument Read(ProposalEntity row) => JsonSerializer.Deserialize<FlowDocument>(row.Document, DesignJson.Options)!;

    private static FlowProposalDto ToDto(ProposalEntity row, FlowDocument document) => new(
        row.Id, row.ProjectId, row.BranchId, row.Request, row.Status,
        row.Name, document.Output.Summary, document.Output.ReasoningSummary, document.Output.OpenQuestions,
        document.Scenario with { Name = row.Name }, document.BlockedSteps, document.Missing, document.RejectedReferences, document.Issues,
        document.Preview, document.Diff, document.Evidence, document.ReviseScenarioId, document.ModelVersion,
        document.Provider, document.PromptTemplateVersion, document.Usage,
        row.CreatedBy, row.CreatedAt, row.DecidedBy, row.DecidedAt);
}

/// <summary>Reads that both kinds of proposal share.</summary>
internal static class ProposalStore
{
    /// <summary>The newest first. The cursor is the creation time and the ID of the last item of a page.</summary>
    public static async Task<Page<ProposalSummary>> ListAsync(
        AiDesignDbContext db, Guid projectId, string kind, string? cursor, int? limit, CancellationToken ct)
    {
        var pageSize = Math.Clamp(limit ?? 50, 1, 200);
        var query = db.Proposals.AsNoTracking().Where(p => p.ProjectId == projectId && p.Kind == kind);

        if (CursorEncoder.Decode(cursor) is { } decoded
            && long.TryParse(decoded.SortKey, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ticks)
            && ticks <= DateTimeOffset.MaxValue.UtcTicks)
        {
            var before = new DateTimeOffset(ticks, TimeSpan.Zero);
            var afterId = decoded.Id;
            query = query.Where(p => p.CreatedAt < before || (p.CreatedAt == before && p.Id.CompareTo(afterId) > 0));
        }

        var items = await query
            .OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id)
            .Take(pageSize + 1)
            .Select(p => new ProposalSummary(p.Id, p.Status, p.Name, p.Request, p.BranchId, p.CreatedBy, p.CreatedAt, p.DecidedBy, p.DecidedAt))
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

    /// <summary>The proposal row, locked until the caller's transaction ends. Null when the project has no such proposal.</summary>
    public static async Task<ProposalEntity?> LockAsync(AiDesignDbContext db, Guid projectId, Guid proposalId, string kind, CancellationToken ct) =>
        // Read as a list: a further operator would wrap the statement, and the lock clause belongs to the outer query.
        (await db.Proposals
            .FromSql($"SELECT * FROM ai.design_proposals WHERE id = {proposalId} AND project_id = {projectId} AND kind = {kind} FOR UPDATE")
            .ToListAsync(ct)).FirstOrDefault();
}
