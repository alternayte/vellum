using System.Text.Json;
using System.Text.Json.Nodes;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Ai;
using Vellum.Modules.Ai.Design;
using Vellum.Modules.Modelling;
using Vellum.Modules.Promises;
using Vellum.Modules.Review.Decisions;
using Vellum.Modules.Review.Rules;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Review.Critique;

/// <param name="Question">What the person asks, at most 2,000 characters. Without one, the design is reviewed in general.</param>
/// <param name="Focus">The IDs of the things to look at first, at most 50. An ID that names nothing is ignored.</param>
/// <param name="BranchId">The draft or variant to read. Null means the main model.</param>
public sealed record CritiqueRequest(string? Question = null, IReadOnlyList<Guid>? Focus = null, Guid? BranchId = null);

/// <summary>
/// One answer of the assistant, after the server checked it (see <c>CritiqueGrounding</c>).
/// Everything in it is a suggestion. <paramref name="Risks"/> holds only risks that a
/// finding of a rule supports; a risk with no finding behind it is in
/// <paramref name="Questions"/> with the source <c>unsupported-risk</c>.
/// </summary>
/// <param name="LeftOut">What the server removed from the answer, and why.</param>
/// <param name="ModelVersion">The version of the branch when the critique was made.</param>
/// <param name="FindingsHash">The hash of the findings the assistant was given.</param>
/// <param name="Stale">True when the branch is at another version now.</param>
/// <param name="FindingsSent">How many findings (concerns and unknowns) the prompt held.</param>
/// <param name="FindingsLeftOut">How many it left out: their subject has no alias, they name a private-sourced thing, or the prompt was full.</param>
/// <param name="OmittedPrivate">How many things the prompt left out because of the private-code setting: parts of the model, and scenarios and promises whose name holds the name of such a part.</param>
/// <param name="Disclaimer">Always <c>suggestions</c>: nothing here is a finding, and nothing here was verified.</param>
public sealed record CritiqueDto(
    Guid Id, Guid ProjectId, Guid? BranchId, string Question, IReadOnlyList<Guid> Focus,
    IReadOnlyList<CritiqueQuestion> Questions, IReadOnlyList<CritiqueRisk> Risks,
    IReadOnlyList<CritiqueProposedDecision> ProposedDecisions, IReadOnlyList<CritiqueLeftOut> LeftOut,
    long ModelVersion, string FindingsHash, bool Stale, int FindingsSent, int FindingsLeftOut,
    AliasOmitted Omitted, OmittedPrivate? OmittedPrivate,
    ProposalProvider Provider, string PromptTemplateVersion, ProposalUsage Usage, string Disclaimer,
    string CreatedBy, DateTimeOffset CreatedAt);

public sealed record CritiqueSummary(Guid Id, Guid? BranchId, string Question, string CreatedBy, DateTimeOffset CreatedAt);

internal sealed record CritiqueDocument(
    IReadOnlyList<Guid> Focus, CritiqueContent Content, long ModelVersion, string FindingsHash, int FindingsSent, int FindingsLeftOut,
    AliasOmitted Omitted, OmittedPrivate? OmittedPrivate, ProposalProvider Provider, string PromptTemplateVersion, ProposalUsage Usage);

internal static class ReviewPrompts
{
    public const string CritiqueVersion = "review/critique/1";

    public static readonly IReadOnlyDictionary<string, string> Critique = DesignPrompts.Load("critique.md", ".Review.Critique.Prompts.");
}

/// <summary>
/// Rubber Duck critique (SDD §3 and §8): the assistant reads the alias table of the model and
/// the deterministic findings as data, and answers with questions, risks and proposed
/// decisions. The call goes through <see cref="AiClient"/> with the task <c>critique</c>, so
/// the provider routing, the budget, the metering, the secret redaction and the private-code
/// setting are the same as for every other AI call. Without a provider the answer is
/// <see cref="AiUnavailable"/>; the findings do not need one.
/// <para>
/// The answer is stored as a critique and changes nothing else. A person keeps a question as
/// an open question of the project, or a proposed decision as a decision with the status
/// <c>proposed</c> and the origin <c>ai-suggested</c>. Only a person accepts a decision.
/// </para>
/// </summary>
public static class CritiqueEndpoints
{
    public const string Purpose = "critique";
    public const string Disclaimer = "suggestions";
    public const int MaxQuestionChars = 2_000;
    public const int MaxFocus = 50;
    public const int MaxOutputTokens = 6_000;
    /// <summary>The most critiques one project keeps. The oldest goes when a new one is stored.</summary>
    public const int MaxPerProject = 50;
    /// <summary>The most characters of findings one prompt holds. What does not fit is counted in <c>findingsLeftOut</c>.</summary>
    public const int MaxFindingsChars = 96 * 1024;

    public static async Task<IResult> Create(
        Guid projectId, CritiqueRequest body, HttpContext http, AiClient ai, AppDbContext db, PrivateSources privateSources, TimeProvider time, CancellationToken ct)
    {
        var (caller, refusal) = await DesignAccess.ResolveAsync(http, projectId, body.BranchId, WorkspaceRole.Editor, forWrite: false, ct);
        if (caller is null)
            return refusal!;
        if (body.Question is not null && TextRule.Lines("question", body.Question, MaxQuestionChars) is { } invalid)
            return Invalid(invalid.Field, invalid.Message);
        if (body.Focus is { Count: > MaxFocus })
            return Invalid("focus", $"A focus holds at most {MaxFocus} IDs");

        var (review, reviewRefusal) = await ReviewEngine.LoadAsync(http.RequestServices, projectId, null, body.BranchId, ct);
        if (review is null)
            return reviewRefusal!;

        var model = review.Input.Model;
        var focus = (body.Focus ?? []).Distinct().ToList();
        var fired = review.Findings.Where(f => f.Finding.Fired).ToList();
        var gate = await privateSources.LoadAsync(caller.WorkspaceId, projectId, model, ct);
        var table = ModelAliases.Build(
            model, review.Input.Schemas.ToDictionary(s => new SchemaId(s.Id), s => s.Content),
            AliasNeeds.For(body.Question ?? "", selected: focus.Concat(fired.Select(f => f.Finding.Subject.Id))), gate);

        // No import writes a scenario or a promise: a person, an agent or the assistant does.
        // Their text is free text, and it can hold the name of a private-sourced thing, as the
        // sentence of a finding can. Such a thing may be named by text that does not cite it,
        // so the names are compared as text. Text that holds one is left out while the
        // private-code setting forbids it, and makes the part private code when it is sent.
        var labels = Trace.TraceModelBuilder.Labels(model);
        var schemaNames = review.Input.Schemas.ToDictionary(s => s.Id, s => s.Name);
        var privateNames = gate.Ids.Select(id => labels.GetValueOrDefault(id) ?? schemaNames.GetValueOrDefault(id, "")).Where(name => name.Length > 0).ToList();
        bool NamesPrivate(params string?[] texts) =>
            privateNames.Any(name => texts.Any(text => text is not null && text.Contains(name, StringComparison.Ordinal)));
        var (namesPrivate, withheld) = (false, 0);
        bool Withheld(params string?[] texts)
        {
            var named = NamesPrivate(texts);
            if (named && !gate.MaySend)
            {
                withheld++;
                return true;
            }
            namesPrivate |= named;
            return false;
        }

        // Scenarios and promises have no alias in the model table. They get one here, by name.
        // One that is withheld gets none, so a finding about it is not sent either.
        var aliases = table.ByAlias.ToDictionary(a => a.Key, a => new Cited(a.Value.Kind, a.Value.Id, a.Value.Name), StringComparer.Ordinal);
        var byId = table.ByAlias.ToDictionary(a => a.Value.Id, a => a.Key);
        var more = new JsonObject { ["scenarios"] = new JsonArray(), ["promises"] = new JsonArray() };
        foreach (var (scenario, i) in review.Input.Scenarios.OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Id.Value).Select((s, i) => (s, i)))
        {
            if (Withheld(scenario.Name))
                continue;
            var alias = $"F{i + 1}";
            (aliases[alias], byId[scenario.Id.Value]) = (new Cited("scenario", scenario.Id.Value, scenario.Name), alias);
            more["scenarios"]!.AsArray().Add(new JsonObject { ["alias"] = alias, ["name"] = scenario.Name });
        }
        foreach (var (promise, i) in review.Input.Promises.Select((p, i) => (p, i)))
        {
            if (Withheld(promise.RequirementId, promise.Current.Title))
                continue;
            var alias = $"P{i + 1}";
            (aliases[alias], byId[promise.Id]) = (new Cited("promise", promise.Id, promise.RequirementId), alias);
            more["promises"]!.AsArray().Add(new JsonObject
            {
                ["alias"] = alias, ["requirementId"] = promise.RequirementId, ["title"] = promise.Current.Title, ["state"] = promise.State,
            });
        }

        // A finding goes to the provider only when its subject has an alias and its sentence
        // and its question name nothing that the private-code setting holds back.
        var sent = new JsonArray();
        var cited = new List<Guid>();
        var (chars, leftOut) = (0, 0);
        foreach (var view in fired)
        {
            var finding = view.Finding;
            var named = NamesPrivate(finding.Reason, finding.Question);
            if (!byId.TryGetValue(finding.Subject.Id, out var subject) || (named && !gate.MaySend))
            {
                leftOut++;
                continue;
            }
            var entry = new JsonObject
            {
                ["rule"] = finding.RuleId, ["category"] = finding.Category, ["severity"] = finding.Severity, ["result"] = finding.Result,
                ["status"] = view.NeedsReReview ? "needs-re-review" : view.Status, ["subject"] = subject,
                ["cites"] = new JsonArray([.. finding.Cites.Select(c => byId.GetValueOrDefault(c.Id)).Where(a => a is not null).Select(a => (JsonNode?)a)]),
                ["reason"] = finding.Reason, ["question"] = finding.Question,
            };
            var size = entry.ToJsonString().Length;
            if (chars + size > MaxFindingsChars)
            {
                leftOut++;
                continue;
            }
            chars += size;
            sent.Add(entry);
            namesPrivate |= named;
            // Only what has an alias is in the entry, so only that decides the class of the part.
            cited.Add(finding.Subject.Id);
            cited.AddRange(finding.Cites.Select(c => c.Id).Where(byId.ContainsKey));
        }
        more["findings"] = sent;

        var prompt = ReviewPrompts.Critique;
        var parts = new List<AiPart>
        {
            AiPart.Instruction(prompt["table"]), AiPart.Untrusted(table.Table, table.DataClass),
            AiPart.Instruction(prompt["findings"]), AiPart.Untrusted(more.ToJsonString(), namesPrivate ? AiDataClass.PrivateRepository : gate.ClassOf(cited)),
        };
        if (focus.Select(id => byId.GetValueOrDefault(id)).OfType<string>().ToList() is { Count: > 0 } focused)
        {
            parts.Add(AiPart.Instruction(prompt["focus"]));
            parts.Add(AiPart.Untrusted(string.Join(", ", focused), AiDataClass.Public));
        }
        if (string.IsNullOrWhiteSpace(body.Question))
            parts.Add(AiPart.Instruction(prompt["default"]));
        else
        {
            parts.Add(AiPart.Instruction(prompt["question"]));
            parts.Add(AiPart.Untrusted(body.Question, AiDataClass.Public));
        }

        var result = await ai.CompleteStructuredAsync<CritiqueOutput>(new AiRequest(
            caller.WorkspaceId, AiTask.Critique, Purpose, ReviewPrompts.CritiqueVersion, prompt["system"],
            [new AiMessage(AiRole.User, parts)], CritiqueSchema.Schema, MaxOutputTokens, caller.UserId), ct);
        if (result is not AiResult<CritiqueOutput>.Succeeded answer)
            return Results.Ok(AiUnavailable.From(((AiResult<CritiqueOutput>.Failed)result).Failure));

        var document = new CritiqueDocument(
            focus, CritiqueGrounding.Ground(answer.Value, aliases, review.Report.Findings),
            review.Input.Pin.Version, review.Report.Hash, sent.Count, leftOut,
            table.Omitted, OmittedPrivate.Of(table.OmittedPrivateCount + withheld),
            new ProposalProvider(answer.Usage.ProviderKind, answer.Usage.Model, answer.Usage.ReportedModel),
            ReviewPrompts.CritiqueVersion, ProposalUsage.From(answer.Usage));
        var row = new CritiqueEntity
        {
            Id = Guid.NewGuid(), WorkspaceId = caller.WorkspaceId, ProjectId = projectId, BranchId = body.BranchId,
            Question = body.Question?.Trim() ?? "", Document = JsonSerializer.Serialize(document, DesignJson.Options),
            CreatedBy = caller.UserId, CreatedAt = time.GetUtcNow(),
        };
        db.Critiques.Add(row);
        await db.SaveChangesAsync(ct);

        // A critique is an answer to read, not a record. The project keeps the newest ones.
        var old = await db.Critiques.Where(c => c.ProjectId == projectId)
            .OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id).Skip(MaxPerProject).Select(c => c.Id).ToListAsync(ct);
        if (old.Count > 0)
            await db.Critiques.Where(c => old.Contains(c.Id)).ExecuteDeleteAsync(ct);

        return Results.Created($"/api/projects/{projectId}/review/critiques/{row.Id}", ToDto(row, document, review.Input.Pin.Version));
    }

    public static async Task<IResult> List(Guid projectId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        await DesignAccess.RequireAsync(http, projectId, WorkspaceRole.Viewer, ct);
        var rows = await db.Critiques.AsNoTracking().Where(c => c.ProjectId == projectId)
            .OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id)
            .Select(c => new CritiqueSummary(c.Id, c.BranchId, c.Question, c.CreatedBy, c.CreatedAt)).ToListAsync(ct);
        return Results.Ok(new Page<CritiqueSummary>(rows, null));
    }

    public static async Task<IResult> Get(Guid projectId, Guid critiqueId, HttpContext http, AppDbContext db, IEventStore store, CancellationToken ct)
    {
        await DesignAccess.RequireAsync(http, projectId, WorkspaceRole.Viewer, ct);
        if (await db.Critiques.AsNoTracking().FirstOrDefaultAsync(c => c.Id == critiqueId && c.ProjectId == projectId, ct) is not { } row)
            return NotFound("Critique");
        return Results.Ok(ToDto(row, Read(row), await VersionAsync(http, store, projectId, row.BranchId, ct)));
    }

    public static async Task<IResult> Delete(Guid projectId, Guid critiqueId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        await DesignAccess.RequireAsync(http, projectId, WorkspaceRole.Editor, ct);
        return await db.Critiques.Where(c => c.Id == critiqueId && c.ProjectId == projectId).ExecuteDeleteAsync(ct) == 1
            ? Results.NoContent()
            : NotFound("Critique");
    }

    /// <summary>
    /// A person keeps a proposed decision: it becomes a decision of the project with the
    /// status <c>proposed</c> and the origin <c>ai-suggested</c>. The text is read from the
    /// stored critique. A critique of a branch that moved since is refused with
    /// <c>critique_stale</c>: its proposals were made for another model.
    /// </summary>
    public static async Task<IResult> KeepDecision(
        Guid projectId, Guid critiqueId, int index, HttpContext http, AppDbContext db, IEventStore store, TimeProvider time, CancellationToken ct)
    {
        var (_, userId) = await DesignAccess.RequireAsync(http, projectId, WorkspaceRole.Editor, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await LockAsync(db, projectId, critiqueId, ct) is not { } row)
            return NotFound("Critique");
        var document = Read(row);
        if (index < 0 || index >= document.Content.ProposedDecisions.Count)
            return NotFound("Proposed decision of the critique");
        var proposed = document.Content.ProposedDecisions[index];
        if (proposed.KeptAs is { } kept)
            return Conflict($"This proposal is kept already, as the decision {kept}.");
        if (await VersionAsync(http, store, projectId, row.BranchId, ct) != document.ModelVersion)
        {
            return Results.Conflict(new ErrorResponse("critique_stale",
                "The model changed after the critique was made. Nothing was saved.",
                "Ask the assistant again, then keep the proposal of the new answer."));
        }

        var created = await DecisionEndpoints.CreateAsync(
            http.RequestServices, projectId,
            new DecisionContent(proposed.Title, proposed.Context, proposed.Decision, proposed.Alternatives, null,
                [.. proposed.About.Select(a => a.Id)], proposed.Settles, null),
            DecisionOrigin.AiSuggested, userId, time.GetUtcNow(), ct);
        if (created.Dto is not { } decision)
            return created.Refusal!;

        var decisions = document.Content.ProposedDecisions.ToList();
        decisions[index] = proposed with { KeptAs = decision.Id };
        row.Document = JsonSerializer.Serialize(document with { Content = document.Content with { ProposedDecisions = decisions } }, DesignJson.Options);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Created($"/api/projects/{projectId}/decisions/{decision.Id}", decision);
    }

    /// <summary>A person keeps a question of the critique as an open question of the project, with the origin <c>ai-suggested</c>.</summary>
    public static async Task<IResult> KeepQuestion(
        Guid projectId, Guid critiqueId, int index, HttpContext http, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var (_, userId) = await DesignAccess.RequireAsync(http, projectId, WorkspaceRole.Editor, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await LockAsync(db, projectId, critiqueId, ct) is not { } row)
            return NotFound("Critique");
        var document = Read(row);
        if (index < 0 || index >= document.Content.Questions.Count)
            return NotFound("Question of the critique");
        var question = document.Content.Questions[index];
        if (question.KeptAs is { } kept)
            return Conflict($"This question is kept already, as the question {kept}.");

        var (added, refusal) = await Questions.AddAsync(
            db, projectId, question.Text, [.. question.About.Select(a => a.Id)], PromiseOrigin.AiSuggested, userId, time.GetUtcNow(), ct);
        if (added is null)
            return refusal!;

        var questions = document.Content.Questions.ToList();
        questions[index] = question with { KeptAs = added.Id };
        row.Document = JsonSerializer.Serialize(document with { Content = document.Content with { Questions = questions } }, DesignJson.Options);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Created($"/api/projects/{projectId}/questions/{added.Id}", Questions.ToDto(added));
    }

    /// <summary>The critique row, locked until the transaction ends, so one item is kept once.</summary>
    private static async Task<CritiqueEntity?> LockAsync(AppDbContext db, Guid projectId, Guid critiqueId, CancellationToken ct) =>
        (await db.Critiques.FromSql($"SELECT * FROM review.critiques WHERE id = {critiqueId} AND project_id = {projectId} FOR UPDATE").ToListAsync(ct)).FirstOrDefault();

    private static async Task<long> VersionAsync(HttpContext http, IEventStore store, Guid projectId, Guid? branchId, CancellationToken ct) =>
        (await store.Load<ModelState>(StreamId.From(await DesignAccess.StreamOfAsync(http, projectId, branchId, ct)), ct)).Version;

    private static CritiqueDocument Read(CritiqueEntity row) => JsonSerializer.Deserialize<CritiqueDocument>(row.Document, DesignJson.Options)!;

    private static CritiqueDto ToDto(CritiqueEntity row, CritiqueDocument document, long versionNow) => new(
        row.Id, row.ProjectId, row.BranchId, row.Question, document.Focus,
        document.Content.Questions, document.Content.Risks, document.Content.ProposedDecisions, document.Content.LeftOut,
        document.ModelVersion, document.FindingsHash, versionNow != document.ModelVersion, document.FindingsSent, document.FindingsLeftOut,
        document.Omitted, document.OmittedPrivate, document.Provider, document.PromptTemplateVersion, document.Usage, Disclaimer,
        row.CreatedBy, row.CreatedAt);
}
