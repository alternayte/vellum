using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Import.Extraction;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Import.Scans;

/// <param name="StartLine">The first line returned. Lines count from 1, as the evidence of a fact does.</param>
/// <param name="EndLine">The last line returned. It is before the line asked for when the file ends earlier.</param>
public sealed record SourceWindowDto(string Path, string CommitSha, int StartLine, int EndLine, IReadOnlyList<string> Lines);

/// <summary>
/// The lines of a file as the scan read them, for showing next to a proposal. It reads the
/// stored blob and never asks GitHub. The text is repository content: it is returned as
/// data, in a JSON string, and nothing on the server interprets it.
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
        WorkspaceAuthorizationService auth, ImportDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.FindFirstValue(ClaimTypes.NameIdentifier)!, WorkspaceRole.Viewer, ct);
        var scan = await db.Scans.AsNoTracking().FirstOrDefaultAsync(s => s.Id == importId && s.ProjectId == projectId, ct);
        if (scan is null)
            return ScanRequests.NotFound();

        if (string.IsNullOrEmpty(path) || path.Length > 1024)
            return Invalid("path", "Name a file of the scan.");
        var start = startLine ?? 1;
        var end = endLine ?? start;
        if (start < 1 || end < start)
            return Invalid("startLine", "Lines count from 1, and endLine is not before startLine.");
        if (end - start + 1 > MaxLines)
            return Invalid("endLine", $"One request returns at most {MaxLines} lines.");

        // Only a file that this scan read has a row with the state "read". A path that the
        // filter refused was never fetched, so there is nothing to return for it.
        var file = await db.ScanFiles.AsNoTracking()
            .FirstOrDefaultAsync(f => f.ScanId == importId && f.Path == path && f.State == ScanFileEntity.Read, ct);
        if (file is null || PathFilter.SkipReason(path, out _) is not null)
            return Results.NotFound(new ErrorResponse("not_found", "The scan did not read a file with this path."));

        var content = await db.Blobs.AsNoTracking()
            .Where(b => b.WorkspaceId == scan.WorkspaceId && b.BlobSha == file.BlobSha)
            .Select(b => b.Content)
            .FirstOrDefaultAsync(ct);
        if (content is null)
        {
            return Results.NotFound(new ErrorResponse("source_not_kept",
                "The text of this file is no longer kept for this scan. Scan the repository again to read it."));
        }
        if (Array.IndexOf(content, (byte)0) >= 0)
            return Results.NotFound(new ErrorResponse("not_found", "The file is not text."));

        // The same line ends as the extractors use, so the numbers are those of the evidence.
        var lines = TextUtil.Normalize(Encoding.UTF8.GetString(content)).Split('\n');
        if (start > lines.Length)
            return Invalid("startLine", $"The file has {lines.Length} lines.");
        end = Math.Min(end, lines.Length);

        var window = lines[(start - 1)..end].Select(line =>
            CredentialGuard.LooksSecret(line) ? Withheld
            : line.Length > MaxLineLength ? line[..MaxLineLength]
            : line).ToList();
        return Results.Ok(new SourceWindowDto(path, scan.CommitSha!, start, end, window));
    }

    private static IResult Invalid(string field, string message) =>
        Results.BadRequest(new ErrorResponse("validation_error", "Validation failed", Errors: [new FieldError(field, message)]));
}
