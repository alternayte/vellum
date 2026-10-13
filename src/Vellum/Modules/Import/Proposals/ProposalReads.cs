using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Import.Proposals;

/// <param name="Pointer">The form stored on a fact: <c>github.com/acme/orders@&lt;sha&gt;:src/Api/Program.cs:41-44</c>.</param>
/// <param name="Url">The permanent link to the lines at the commit.</param>
public sealed record EvidenceDto(string Path, int StartLine, int EndLine, string Rule, string Pointer, string Url);

public sealed record ProposalChangeDto(string Field, string? From, string? To);

/// <param name="Proposed">The values the source establishes, by model field name. Null for <c>remove</c>.</param>
/// <param name="Current">The values of the entity when the scan completed. Null for <c>add</c>.</param>
/// <param name="Partial">True when the proposal comes from a partial scan: one that did not read everything in its scope.</param>
public sealed record ProposalDto(
    Guid Id, string Action, string EntityKind, Guid EntityId, string FactKey, string Name,
    JsonObject? Proposed, JsonObject? Current, IReadOnlyList<ProposalChangeDto> Changes,
    IReadOnlyList<EvidenceDto> Evidence, Guid[] DependsOn, string Status, bool Partial);

/// <param name="Reason">Why the fact is not a proposal: a fixed code such as <c>no-model-home</c>.</param>
public sealed record EvidenceItemDto(
    string FactKey, string Kind, string Name, IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<EvidenceDto> Evidence, string Reason);

public static class ProposalReads
{
    public static async Task<IResult> List(
        Guid projectId, Guid importId, string? status, string? action, string? entityKind,
        string? cursor, int? limit, ClaimsPrincipal user,
        WorkspaceAuthorizationService auth, ImportDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.FindFirstValue(ClaimTypes.NameIdentifier)!, WorkspaceRole.Viewer, ct);
        var scan = await db.Scans.AsNoTracking().FirstOrDefaultAsync(s => s.Id == importId && s.ProjectId == projectId, ct);
        if (scan is null)
            return ScanRequests.NotFound();

        var query = db.Proposals.AsNoTracking().Where(p => p.ScanId == importId);
        if (status is not null) query = query.Where(p => p.Status == status);
        if (action is not null) query = query.Where(p => p.Action == action);
        if (entityKind is not null) query = query.Where(p => p.EntityKind == entityKind);
        if (After(cursor) is { } after)
            query = query.Where(p => p.Seq > after);

        var pageSize = Math.Clamp(limit ?? 50, 1, 200);
        var rows = await query.OrderBy(p => p.Seq).Take(pageSize + 1).ToListAsync(ct);
        string? next = null;
        if (rows.Count > pageSize)
        {
            rows.RemoveAt(pageSize);
            next = CursorEncoder.Encode(rows[^1].Seq.ToString(CultureInfo.InvariantCulture), rows[^1].Id);
        }

        var repository = $"{scan.Host}/{scan.Repository}";
        return Results.Ok(new Page<ProposalDto>([.. rows.Select(p => new ProposalDto(
            p.Id, p.Action, p.EntityKind, p.EntityId, p.FactKey, p.Name,
            p.Proposed is null ? null : JsonNode.Parse(p.Proposed)!.AsObject(),
            p.Current is null ? null : JsonNode.Parse(p.Current)!.AsObject(),
            [.. ProposalJson.ReadChanges(p.Changes).Select(c => new ProposalChangeDto(c.Field, c.From, c.To))],
            Evidence(repository, scan.CommitSha!, p.Evidence), p.DependsOn, p.Status, p.Partial))], next));
    }

    /// <summary>
    /// The facts of the snapshot that are not proposals: those the model has no home for, and
    /// those that need something the source does not state. They stay readable with their
    /// evidence, so a later feature or a person can use them.
    /// </summary>
    public static async Task<IResult> Evidence(
        Guid projectId, Guid importId, string? factKey, string? cursor, int? limit, ClaimsPrincipal user,
        WorkspaceAuthorizationService auth, ImportDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.FindFirstValue(ClaimTypes.NameIdentifier)!, WorkspaceRole.Viewer, ct);
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
        CursorEncoder.Decode(cursor) is { } decoded
        && int.TryParse(decoded.SortKey, NumberStyles.None, CultureInfo.InvariantCulture, out var seq)
            ? seq
            : null;

    private static List<EvidenceDto> Evidence(string repository, string commitSha, string json) =>
        [.. ProposalJson.ReadEvidence(json).Select(e => new EvidenceDto(
            e.Path, e.StartLine, e.EndLine, e.Rule,
            EvidenceLinks.Pointer(repository, commitSha, e), EvidenceLinks.Url(repository, commitSha, e)))];
}
