using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Import.Extraction;
using Vellum.Modules.Import.Proposals;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Import.Scans;

/// <param name="StartLine">The first line returned. Lines count from 1, as the evidence of a fact does.</param>
/// <param name="EndLine">The last line returned.</param>
public sealed record SourceWindowDto(string Path, string CommitSha, int StartLine, int EndLine, IReadOnlyList<string> Lines);

/// <summary>
/// The lines of a file that are evidence of a fact, for showing next to a proposal. It reads
/// the stored blob and never asks GitHub. The text is repository content: it is returned as
/// data, in a JSON string, and nothing on the server interprets it.
/// <para>Text is served only after the extraction cleared it. Four conditions hold together:</para>
/// <list type="number">
/// <item>The scan is <c>completed</c>. A failed, cancelled or running scan serves nothing.</item>
/// <item>The scan extracted the file: its row is <c>read</c>. A file that was skipped,
/// withheld or failed has no text to serve, and withheld content was never stored.</item>
/// <item>The lines asked for lie inside the evidence of one fact or proposal of this scan
/// at this commit. No other window of the file is served.</item>
/// <item>Each line passes <see cref="SourceMask"/>.</item>
/// </list>
/// <para>
/// Who may read: for a repository that GitHub reports as public, any member of the project.
/// For a private repository, or one whose visibility is unknown, the Editor role and above.
/// A Viewer still gets the evidence metadata and the link to GitHub, which applies GitHub's
/// own access rules.
/// </para>
/// </summary>
public static class SourceWindow
{
    /// <summary>The most lines one request returns.</summary>
    public const int MaxLines = 200;
    /// <summary>The most characters of one line. A longer line is cut.</summary>
    public const int MaxLineLength = 2000;
    public const string Withheld = "[withheld: this line looks like a credential]";

    public static async Task<IResult> Handle(
        Guid projectId, Guid importId, string? path, int? startLine, int? endLine, ClaimsPrincipal user,
        WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);
        var scan = await db.Scans.AsNoTracking().FirstOrDefaultAsync(s => s.Id == importId && s.ProjectId == projectId, ct);
        if (scan is null)
            return ScanRequests.NotFound();
        if (scan.RepositoryIsPrivate != false)
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        if (string.IsNullOrEmpty(path) || path.Length > ScanFileEntity.MaxPath)
            return Invalid("path", "Name a file of the scan.");
        var start = startLine ?? 1;
        var end = endLine ?? start;
        if (start < 1 || end < start)
            return Invalid("startLine", "Lines count from 1, and endLine is not before startLine.");
        if (end - start + 1 > MaxLines)
            return Invalid("endLine", $"One request returns at most {MaxLines} lines.");

        if (scan.Status != ScanStatus.Completed || scan.CommitSha is null)
        {
            return Results.Conflict(new ErrorResponse("source_not_cleared",
                "The scan did not complete, so none of its text is cleared for reading. Resume the scan, or start a new one."));
        }

        var pathHash = KeyHash.Of(path);
        var file = await db.ScanFiles.AsNoTracking()
            .FirstOrDefaultAsync(f => f.ScanId == importId && f.PathHash == pathHash && f.State == ScanFileEntity.Read, ct);
        if (file is null || file.Path != path || PathFilter.SkipReason(path, out _) is not null)
            return Results.NotFound(new ErrorResponse("not_found", "The scan did not read a file with this path."));

        if (!await IsEvidenceAsync(db, scan, path, start, end, ct))
        {
            return Results.NotFound(new ErrorResponse("not_evidence",
                "These lines are not the evidence of a fact or a proposal of this scan. Ask for the lines of one evidence item."));
        }

        var content = await db.Blobs.AsNoTracking()
            .Where(b => b.WorkspaceId == scan.WorkspaceId && b.BlobSha == file.BlobSha)
            .Select(b => b.Content)
            .FirstOrDefaultAsync(ct);
        if (content is null)
        {
            return Results.NotFound(new ErrorResponse("source_not_kept",
                "The text of this file is no longer kept for this scan. Scan the repository again to read it."));
        }

        // The same line ends as the extractors use, so the numbers are those of the evidence.
        var lines = TextUtil.Normalize(Encoding.UTF8.GetString(content)).Split('\n');
        if (end > lines.Length)
            return Results.NotFound(new ErrorResponse("not_evidence", "The file is shorter than the lines asked for."));

        var masked = SourceMask.Apply(path, lines);
        return Results.Ok(new SourceWindowDto(path, scan.CommitSha, start, end,
            [.. masked[(start - 1)..end].Select(line => line.Length > MaxLineLength ? line[..MaxLineLength] : line)]));
    }

    /// <summary>True when one evidence item of a fact or a proposal of the scan, at the scan's commit, holds all the lines.</summary>
    private static async Task<bool> IsEvidenceAsync(
        AppDbContext db, ScanEntity scan, string path, int start, int end, CancellationToken ct)
    {
        var filter = ProposalJson.Write(new[] { new { path } });
        var rows = await db.Database.SqlQuery<string>($"""
            SELECT f.evidence::text AS "Value" FROM import.snapshot_facts f
            WHERE f.scan_id = {scan.Id} AND f.evidence @> {filter}::jsonb
            UNION ALL
            SELECT p.evidence::text FROM import.proposals p
            WHERE p.scan_id = {scan.Id} AND p.evidence @> {filter}::jsonb
            """).ToListAsync(ct);
        return rows.SelectMany(ProposalJson.ReadEvidence).Any(e =>
            e.Path == path && (e.CommitSha is null || e.CommitSha == scan.CommitSha)
            && e.StartLine <= start && end <= e.EndLine);
    }

    private static IResult Invalid(string field, string message) =>
        Results.BadRequest(new ErrorResponse("validation_error", "Validation failed", Errors: [new FieldError(field, message)]));
}

/// <summary>
/// The second layer in front of repository text: what a line shows after the file was
/// cleared. It never adds text; it replaces.
/// <list type="bullet">
/// <item>A line that looks like a credential (<see cref="CredentialGuard"/>) is replaced whole.</item>
/// <item>In any file, a line of the form <c>key: value</c> or <c>key = value</c> whose key is
/// credential-like keeps the key and loses the value.</item>
/// <item>In a configuration file (<c>.json</c>, <c>.yaml</c>, <c>.yml</c>), every value under
/// a credential-like key loses its value too: the lines that are indented deeper than the
/// key, up to the next line at the key's depth.</item>
/// </list>
/// </summary>
internal static partial class SourceMask
{
    public const string Value = "[withheld]";

    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    [GeneratedRegex(
        @"pass(word|wd|phrase)?|pwd|secret|token|api[_\-.]?key|access[_\-.]?key|private[_\-.]?key|account[_\-.]?key"
        + @"|shared[_\-.]?access|client[_\-.]?secret|connection[_\-.]?strings?|credentials?|authorization|signature|dsn",
        Options | RegexOptions.IgnoreCase)]
    private static partial Regex CredentialKey();

    // Indent and an optional list dash, the key with optional quotes, the separator, the value.
    [GeneratedRegex(@"^(?<head>\s*(?:-\s+)?[""']?(?<key>[A-Za-z0-9_.\-:]+?)[""']?\s*(?::|=)\s*)(?<value>.*)$", Options)]
    private static partial Regex KeyValue();

    public static string[] Apply(string path, string[] lines)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var extension = PathFilter.Extension(name).ToLowerInvariant();
        var nested = extension is ".json" or ".yaml" or ".yml";
        var result = new string[lines.Length];
        var under = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var indent = line.Length - line.AsSpan().TrimStart().Length;
            var blank = indent == line.Length;
            if (under >= 0 && !blank && indent <= under)
                under = -1;

            var match = KeyValue().Match(line);
            var value = match.Success ? match.Groups["value"].Value.Trim() : "";
            var opens = value.TrimEnd(',') is "" or "{" or "[" or "|" or ">" or "|-" or ">-" or "|+" or ">+";

            if (under >= 0 && !blank)
            {
                // A value under a credential-like key. Brackets alone show nothing.
                result[i] = line.Trim().Trim(',') is "{" or "}" or "[" or "]" ? line
                    : match.Success ? (opens ? line : match.Groups["head"].Value + Value)
                    : line[..indent] + Value;
                continue;
            }

            if (match.Success && CredentialKey().IsMatch(match.Groups["key"].Value))
            {
                if (opens)
                {
                    result[i] = line;
                    if (nested)
                        under = indent;
                }
                else
                    result[i] = match.Groups["head"].Value + Value;
                continue;
            }

            result[i] = CredentialGuard.LooksSecret(line) ? SourceWindow.Withheld : line;
        }
        return result;
    }
}
