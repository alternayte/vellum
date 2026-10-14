using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Workspaces;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Import.Proposals;

/// <param name="Pointer">The form stored on a fact: <c>github.com/acme/orders@&lt;sha&gt;:src/Api/Program.cs:41-44</c>.</param>
/// <param name="Url">The permanent link to the lines at the commit.</param>
public sealed record EvidenceDto(string Path, int StartLine, int EndLine, string Rule, string Pointer, string Url);

public sealed record ProposalChangeDto(string Field, string? From, string? To);

/// <param name="Action"><c>add</c>, <c>change</c>, <c>remove</c>, <c>conflict</c> or <c>relink</c>.</param>
/// <param name="Subject"><c>fact</c>, or <c>repository</c> for the proposal about the repository itself, whose fact key is empty.</param>
/// <param name="Proposed">The values the source establishes, by model field name. Null for <c>remove</c>. For <c>relink</c>: <c>{ candidates: [{ factKey, name, evidence }] }</c>.</param>
/// <param name="Current">The values of the entity when the scan completed. Null for <c>add</c>.</param>
/// <param name="Partial">True when the proposal comes from a scan that a limit or a failure cut short.</param>
/// <param name="CanDecide">False when the proposal is a preview: it is not pending, or its scan is a comparison scan.</param>
/// <param name="CanDecideReason">Why a pending proposal cannot be decided.</param>
/// <param name="Impact">What accepting would break, as the model and the scenarios are now. Null when nothing.</param>
/// <param name="Truncated">True when long values of <paramref name="Proposed"/>, <paramref name="Current"/> or <paramref name="Changes"/> are cut in this list. The whole proposal is at <c>GET …/proposals/{id}</c>.</param>
public sealed record ProposalDto(
    Guid Id, string Action, string EntityKind, Guid EntityId, string Subject, string FactKey, string Name,
    JsonObject? Proposed, JsonObject? Current, IReadOnlyList<ProposalChangeDto> Changes,
    IReadOnlyList<EvidenceDto> Evidence, Guid[] DependsOn, string Status, bool Partial,
    bool CanDecide, string? CanDecideReason, ProposalImpactDto? Impact, bool Truncated);

/// <param name="NotCovered">
/// The accepted facts of the repository that this scan could not judge, by reason. They are
/// why a fact that looks gone has no <c>remove</c> proposal here. Null when the scan has none.
/// </param>
public sealed record ProposalPage(IReadOnlyList<ProposalDto> Items, string? Cursor, LinkedFactCoverageDto? NotCovered);

/// <param name="Reason">Why the fact is not a proposal: a fixed code such as <c>no-model-home</c>.</param>
public sealed record EvidenceItemDto(
    string FactKey, string Kind, string Name, IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<EvidenceDto> Evidence, string Reason);

public static class ProposalReads
{
    /// <summary>A proposal whose bodies are larger than this is cut in the list.</summary>
    public const int MaxListedBody = 16 * 1024;
    /// <summary>The most characters of one value in a proposal that is cut.</summary>
    public const int MaxListedValue = 256;
    /// <summary>A page of the list ends when its bodies reach this size, whatever <c>limit</c> says.</summary>
    public const int MaxPageBytes = 1024 * 1024;

    public static async Task<IResult> List(
        Guid projectId, Guid importId, string? status, string? action, string? entityKind,
        string? cursor, int? limit, ClaimsPrincipal user, HttpContext http,
        WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);
        var scan = await db.Scans.AsNoTracking().FirstOrDefaultAsync(s => s.Id == importId && s.ProjectId == projectId, ct);
        if (scan is null)
            return ScanRequests.NotFound();

        var query = db.ImportProposals.AsNoTracking().Where(p => p.ScanId == importId);
        if (status is not null) query = query.Where(p => p.Status == status);
        if (action is not null) query = query.Where(p => p.Action == action);
        if (entityKind is not null) query = query.Where(p => p.EntityKind == entityKind);
        if (After(cursor) is { } after)
            query = query.Where(p => p.Seq > after);

        var pageSize = Math.Clamp(limit ?? 50, 1, 200);
        var rows = await query.OrderBy(p => p.Seq).Take(pageSize + 1).ToListAsync(ct);
        var more = rows.Count > pageSize;
        if (more)
            rows.RemoveAt(pageSize);

        // The page is bounded in bytes as well as in rows.
        var context = await ContextAsync(http.RequestServices, db, scan, rows, ct);
        var items = new List<ProposalDto>(rows.Count);
        var bytes = 0;
        foreach (var row in rows)
        {
            var dto = ToDto(row, context, cut: true, out var size);
            if (items.Count > 0 && bytes + size > MaxPageBytes)
            {
                more = true;
                break;
            }
            bytes += size;
            items.Add(dto);
        }
        var next = more && items.Count > 0
            ? CursorEncoder.Encode(rows[items.Count - 1].Seq.ToString(CultureInfo.InvariantCulture), rows[items.Count - 1].Id)
            : null;

        var coverage = LinkedFactCoverageDto.From(LinkCoverage.FromJson(scan.Coverage));
        return Results.Ok(new ProposalPage(items, next, coverage is { NotCovered: > 0 } ? coverage : null));
    }

    /// <summary>One proposal, whole.</summary>
    public static async Task<IResult> Get(
        Guid projectId, Guid importId, Guid proposalId, ClaimsPrincipal user, HttpContext http,
        WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);
        var scan = await db.Scans.AsNoTracking().FirstOrDefaultAsync(s => s.Id == importId && s.ProjectId == projectId, ct);
        if (scan is null)
            return ScanRequests.NotFound();
        var row = await db.ImportProposals.AsNoTracking().FirstOrDefaultAsync(p => p.Id == proposalId && p.ScanId == importId, ct);
        if (row is null)
            return Results.NotFound(new ErrorResponse("not_found", "Proposal not found"));

        var context = await ContextAsync(http.RequestServices, db, scan, [row], ct);
        return Results.Ok(ToDto(row, context, cut: false, out _));
    }

    private sealed record ReadContext(string Repository, string CommitSha, bool Decidable, string? Reason, ProposalImpact? Impact);

    private static async Task<ReadContext> ContextAsync(
        IServiceProvider services, AppDbContext db, ScanEntity scan, IReadOnlyList<ProposalEntity> rows, CancellationToken ct)
    {
        var tracked = await db.TrackedRefs.AsNoTracking()
            .FirstOrDefaultAsync(t => t.ProjectId == scan.ProjectId && t.Host == scan.Host && t.Repository == scan.Repository, ct);
        var kind = ScanKindInfo.Of(scan, tracked);

        // The impact is read from the model and the scenarios as they are now, and only
        // when the page holds a pending proposal that can have one.
        ProposalImpact? impact = null;
        if (rows.Any(r => r.Status == ProposalStatus.Pending && r.Action is ProposalAction.Remove or ProposalAction.Change or ProposalAction.Conflict)
            && await services.GetRequiredService<AppDbContext>().Projects.AsNoTracking()
                .Where(p => p.Id == scan.ProjectId).Select(p => (Guid?)p.StreamId).FirstOrDefaultAsync(ct) is { } streamId)
        {
            var store = services.GetRequiredService<IEventStore>().ForProject(scan.ProjectId);
            var (model, _) = await store.Load<ModelState>(StreamId.From(streamId), ct);
            impact = await ProposalImpact.LoadAsync(services, store, scan.ProjectId, model, ct);
        }

        return new ReadContext(
            $"{scan.Host}/{scan.Repository}", scan.CommitSha ?? "", kind.Tracking, ScanKindInfo.Describe(kind, tracked), impact);
    }

    private static ProposalDto ToDto(ProposalEntity p, ReadContext context, bool cut, out int size)
    {
        var proposed = p.Proposed is null ? null : JsonNode.Parse(p.Proposed)!.AsObject();
        var current = p.Current is null ? null : JsonNode.Parse(p.Current)!.AsObject();
        var changes = ProposalJson.ReadChanges(p.Changes);

        size = (p.Proposed?.Length ?? 0) + (p.Current?.Length ?? 0) + p.Changes.Length;
        var truncated = cut && size > MaxListedBody;
        if (truncated)
        {
            Cut(proposed);
            Cut(current);
            changes = [.. changes.Select(c => c with { From = Cut(c.From), To = Cut(c.To) })];
            size = (proposed?.ToJsonString().Length ?? 0) + (current?.ToJsonString().Length ?? 0) + changes.Sum(c => (c.From?.Length ?? 0) + (c.To?.Length ?? 0) + c.Field.Length);
        }

        var pending = p.Status == ProposalStatus.Pending;
        ProposalImpactDto? impact = null;
        if (pending && context.Impact is not null)
        {
            var found = context.Impact.Of(p.Action, p.EntityKind, p.EntityId, [.. changes.Select(c => c.Field)]);
            impact = found.IsEmpty ? null : found;
        }

        return new ProposalDto(
            p.Id, p.Action, p.EntityKind, p.EntityId, p.Subject, p.FactKey, p.Name, proposed, current,
            [.. changes.Select(c => new ProposalChangeDto(c.Field, c.From, c.To))],
            Evidence(context.Repository, context.CommitSha, p.Evidence), p.DependsOn, p.Status, p.Partial,
            pending && context.Decidable, pending ? context.Reason : null, impact, truncated);
    }

    private static string? Cut(string? value) =>
        value is { Length: > MaxListedValue } ? value[..MaxListedValue] + "…" : value;

    /// <summary>Cuts every long text value of the object, at any depth, in place.</summary>
    private static void Cut(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject fields:
                foreach (var name in fields.Select(f => f.Key).ToList())
                {
                    if (fields[name] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        if (text.Length > MaxListedValue)
                            fields[name] = Cut(text);
                    }
                    else
                        Cut(fields[name]);
                }
                break;
            case JsonArray items:
                for (var i = 0; i < items.Count; i++)
                {
                    if (items[i] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        if (text.Length > MaxListedValue)
                            items[i] = Cut(text);
                    }
                    else
                        Cut(items[i]);
                }
                break;
        }
    }

    /// <summary>
    /// The facts of the snapshot that are not proposals: those the model has no home for, and
    /// those that need something the source does not state. They stay readable with their
    /// evidence, so a later feature or a person can use them.
    /// </summary>
    public static async Task<IResult> Evidence(
        Guid projectId, Guid importId, string? factKey, string? cursor, int? limit, ClaimsPrincipal user,
        WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);
        var scan = await db.Scans.AsNoTracking().FirstOrDefaultAsync(s => s.Id == importId && s.ProjectId == projectId, ct);
        if (scan is null)
            return ScanRequests.NotFound();

        var query = db.SnapshotFacts.AsNoTracking().Where(f =>
            f.ScanId == importId && (f.Disposition == FactDisposition.EvidenceOnly || f.Disposition == FactDisposition.Blocked));
        if (factKey is not null) query = query.Where(f => f.FactKey == factKey);
        if (After(cursor) is { } after)
            query = query.Where(f => f.Seq > after);

        var pageSize = Math.Clamp(limit ?? 50, 1, 200);
        var rows = await query.OrderBy(f => f.Seq).Take(pageSize + 1).ToListAsync(ct);
        string? next = null;
        if (rows.Count > pageSize)
        {
            rows.RemoveAt(pageSize);
            next = CursorEncoder.Encode(rows[^1].Seq.ToString(CultureInfo.InvariantCulture), importId);
        }

        var repository = $"{scan.Host}/{scan.Repository}";
        return Results.Ok(new Page<EvidenceItemDto>([.. rows.Select(f => new EvidenceItemDto(
            f.FactKey, f.Kind, f.Name, ProposalJson.ReadAttributes(f.Attributes),
            Evidence(repository, scan.CommitSha!, f.Evidence), f.Reason ?? FactMapping.NoModelHome))], next));
    }

    private static int? After(string? cursor) =>
        CursorEncoder.DecodeNumber(cursor) is not { } decoded ? null
        : decoded.Number <= int.MaxValue ? (int)decoded.Number
        : throw new InvalidCursorException();

    private static List<EvidenceDto> Evidence(string repository, string commitSha, string json) =>
        [.. ProposalJson.ReadEvidence(json).Select(e => new EvidenceDto(
            e.Path, e.StartLine, e.EndLine, e.Rule,
            EvidenceLinks.Pointer(repository, commitSha, e), EvidenceLinks.Url(repository, commitSha, e)))];
}
