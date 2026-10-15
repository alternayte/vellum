using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.GitHub;
using Vellum.Modules.Import.Extraction;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Import.Sources;

/// <summary>A Markdown or text file of a Source's newest Snapshot, which the prose import can read.</summary>
/// <param name="IsAdr">True for a file that the ADR extractor reads: it becomes a proposed decision without AI.</param>
public sealed record SourceDocumentDto(string Path, long Size, bool IsAdr);

/// <summary>The text of one document, for a prompt: with its citation and whether it may leave the instance.</summary>
/// <param name="Citation">The <c>&lt;source&gt;@&lt;revision&gt;</c> part that a citation of its lines starts with.</param>
/// <param name="Lines">The lines, with every line that looks like a credential masked.</param>
/// <param name="IsPublic">True only for a file of a repository that the host reported as public.</param>
public sealed record SourceDocument(Guid SourceId, string Path, string Citation, IReadOnlyList<string> Lines, bool IsPublic);

/// <summary>
/// The prose of a Source: Markdown and text files (READMEs, design documents) of its newest
/// completed Snapshot. No extractor reads them, and a scan does not fetch them, so they take no
/// part of its bounds. The AI path reads one when a person asks for it
/// (<c>POST …/ai/architecture-proposals/from-document</c>, or a flow proposal with a document):
/// an uploaded document is stored already; a document of a GitHub Source is fetched then, at the
/// Snapshot's commit, by the blob SHA its listing recorded, and cleared like every other blob.
/// The text is untrusted.
/// </summary>
public static class SourceDocuments
{
    /// <summary>The largest document the prose import sends to a provider.</summary>
    public const int MaxBytes = 64 * 1024;

    public static async Task<IResult> List(
        Guid projectId, Guid sourceId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);
        if (!await db.Sources.AsNoTracking().AnyAsync(s => s.Id == sourceId && s.ProjectId == projectId, ct))
            return NotFound("Source");
        if (await LatestScanAsync(db, projectId, sourceId, ct) is not { } scan)
            return Results.Ok(new List<SourceDocumentDto>());
        var files = await db.ScanFiles.AsNoTracking()
            .Where(f => f.ScanId == scan.Id && (f.State == ScanFileState.Read || f.Reason == CoverageGap.NotRelevant))
            .Select(f => new { f.Path, f.Size }).ToListAsync(ct);
        return Results.Ok(files
            .Where(f => PathFilter.IsProse(f.Path[(f.Path.LastIndexOf('/') + 1)..]) && PathFilter.SkipReason(f.Path, out _) is null)
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .Take(1000)
            .Select(f => new SourceDocumentDto(f.Path, f.Size, PathFilter.IsRelevantPath(f.Path)))
            .ToList());
    }

    /// <summary>
    /// One document of the newest completed Snapshot of the Source. Null with a reason when the
    /// Source, the Snapshot or the file is not there, the file is not prose or too large, or its
    /// content cannot be read or is withheld.
    /// </summary>
    public static async Task<(SourceDocument? Document, string? Problem)> ReadAsync(
        IServiceProvider services, Guid projectId, Guid sourceId, string? path, CancellationToken ct)
    {
        var db = services.GetRequiredService<AppDbContext>();
        if (string.IsNullOrEmpty(path) || path.Length > ScanFileEntity.MaxPath)
            return (null, "Name a document of the Source.");
        if (!PathFilter.IsProse(path[(path.LastIndexOf('/') + 1)..]) || PathFilter.SkipReason(path, out _) is not null)
            return (null, "The file is not a Markdown or text document.");
        if (await LatestScanAsync(db, projectId, sourceId, ct) is not { } scan)
            return (null, "The Source has no completed Snapshot.");
        var pathHash = KeyHash.Of(path);
        var file = await db.ScanFiles.AsNoTracking()
            .FirstOrDefaultAsync(f => f.ScanId == scan.Id && f.PathHash == pathHash
                && (f.State == ScanFileState.Read || f.Reason == CoverageGap.NotRelevant), ct);
        if (file is null || file.Path != path)
            return (null, "The newest Snapshot of the Source has no document with this path.");
        if (file.Size > MaxBytes)
            return (null, $"The document holds {file.Size} bytes. The prose import reads at most {MaxBytes} bytes.");

        var content = await db.Blobs.AsNoTracking()
            .Where(b => b.WorkspaceId == scan.WorkspaceId && b.BlobSha == file.BlobSha).Select(b => b.Content).FirstOrDefaultAsync(ct);
        if (content is null)
        {
            var (fetched, problem) = await FetchAsync(services, db, scan, file, ct);
            if (fetched is null)
                return (null, problem);
            content = fetched;
        }

        var lines = TextUtil.Normalize(Encoding.UTF8.GetString(content)).Split('\n');
        return (new SourceDocument(sourceId, path, $"{scan.Host}/{scan.Repository}@{scan.CommitSha}",
            SourceMask.Apply(path, lines), scan.RepositoryIsPrivate == false), null);
    }

    /// <summary>The blob of a document of a GitHub Snapshot, from GitHub, checked against its SHA and cleared before it is stored.</summary>
    private static async Task<(byte[]? Content, string? Problem)> FetchAsync(
        IServiceProvider services, AppDbContext db, ScanEntity scan, ScanFileEntity file, CancellationToken ct)
    {
        var source = await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == scan.SourceId, ct);
        if (source?.Kind != SourceKind.Github)
            return (null, "The text of this document is no longer kept. Upload it again.");
        var connected = await services.GetRequiredService<GitHubReader>().ConnectAsync(scan.WorkspaceId, ct);
        if (connected.Value is not { } github)
            return (null, "GitHub cannot be reached with the workspace's settings, so the document cannot be read.");
        var names = scan.Repository.Split('/');
        var blob = await github.GetBlobAsync(names[0], names[1], file.BlobSha, MaxBytes, ct);
        if (blob.Value is not { } value || !ContentClearance.IsBlob(value.Content, file.BlobSha))
            return (null, "GitHub did not return the document at the Snapshot's commit.");
        if (ContentClearance.Withhold(value.Content) is { } withheld)
            return (null, $"The document is withheld ({withheld}): it is not sent anywhere.");

        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO import.blobs (workspace_id, blob_sha, content, size, withheld_reason, created_at)
            VALUES ({scan.WorkspaceId}, {file.BlobSha}, {value.Content}, {value.Content.LongLength}, {null}, {now})
            ON CONFLICT (workspace_id, blob_sha) DO NOTHING
            """, ct);
        return (value.Content, null);
    }

    private static Task<ScanEntity?> LatestScanAsync(AppDbContext db, Guid projectId, Guid sourceId, CancellationToken ct) =>
        db.Scans.AsNoTracking()
            .Where(s => s.ProjectId == projectId && s.SourceId == sourceId && s.Status == ScanStatus.Completed && s.CommitSha != null)
            .OrderByDescending(s => s.CompletedAt).ThenByDescending(s => s.Id)
            .FirstOrDefaultAsync(ct);
}
