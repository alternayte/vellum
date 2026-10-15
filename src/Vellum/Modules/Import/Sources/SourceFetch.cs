using Microsoft.EntityFrameworkCore;
using Vellum.Modules.GitHub;
using Vellum.Modules.Import.Scans;
using Vellum.Shared;

namespace Vellum.Modules.Import.Sources;

/// <summary>
/// The fetch stage of a scan: the one stage that differs per kind of Source. It pins the
/// revision, lists every file with its git blob SHA in <c>scan_files</c>, and stores the
/// content of each file in scope in <c>blobs</c> after <see cref="FetchContext.AcceptAsync"/>
/// checked and cleared it. Extraction, proposals, staleness and the Snapshot that follow
/// read only those rows, so they run the same for every kind.
/// <para>
/// A kind that this build has no fetcher for fails the scan with <c>source_kind_unavailable</c>.
/// No Source of such a kind can be created, so only a row written by a later build meets it.
/// </para>
/// </summary>
internal static class SourceFetch
{
    public static Task RunAsync(FetchContext context, CancellationToken ct) => context.Source.Kind switch
    {
        SourceKind.Github => GitHubFetch.RunAsync(context, ct),
        // An upload pinned the Snapshot and listed its one file when it was made (ModelFileUpload).
        SourceKind.ModelFile when context.Scan is { TreeListed: true, CommitSha: not null } => Task.CompletedTask,
        SourceKind.ModelFile => throw ScanStop.Failed(new ScanError("upload_missing",
            "This scan of an uploaded file has no file. Upload the file again.")),
        _ => throw ScanStop.Failed(new ScanError("source_kind_unavailable",
            $"This instance cannot read a Source of kind '{EnumText.Of(context.Source.Kind)}'.")),
    };
}

/// <summary>What a fetcher works with, and the steps every fetcher shares: the checkpoint, the file rows and the content check.</summary>
internal sealed class FetchContext(
    IServiceProvider services, AppDbContext db, ScanEntity scan, SourceEntity source, ScanLimits limits, TimeProvider time)
{
    private static readonly TimeSpan DefaultRateLimitWait = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRateLimitWait = TimeSpan.FromMinutes(65);

    public IServiceProvider Services => services;
    public AppDbContext Db => db;
    public ScanEntity Scan => scan;
    public SourceEntity Source => source;
    public ScanLimits Limits => limits;

    /// <summary>
    /// The checkpoint. The scan row is written with every save, and its fencing token is a
    /// concurrency token: a run that lost the scan fails here, and its file rows with it.
    /// </summary>
    public async Task SaveAsync(CancellationToken ct)
    {
        scan.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Writes one row per listed file. Which of them the scan reads is decided from the list
    /// alone, by the scope rules, before anything is fetched. Every kind lists its files in the
    /// shape of a git tree entry: type <c>blob</c>, a mode, the git blob SHA and the size.
    /// </summary>
    public async Task ListAsync(IReadOnlyList<GitHubTreeEntry> entries, CancellationToken ct)
    {
        var selection = ScanScope.Select(entries, limits, scan.IncludePaths, scan.ExcludePaths);
        var rows = selection.Files.Select(file => new ScanFileEntity
        {
            ScanId = scan.Id, Path = file.Entry.Path, PathHash = KeyHash.Of(file.Entry.Path),
            BlobSha = file.Entry.Sha, Size = file.Entry.Size ?? 0,
            State = file.Reason is null ? ScanFileState.Pending : ScanFileState.Skipped, Reason = file.Reason,
        }).ToList();
        db.ScanFiles.AddRange(rows);
        scan.TreeListed = true;
        scan.FilesListed = selection.Listed;
        scan.FilesSkipped = selection.Skipped;
        scan.Partial |= selection.Partial;
        scan.Notes = ScanNotes.Write(ScanNotes.Read(scan.Notes).Concat(selection.Notes));
        await SaveAsync(ct);
        foreach (var row in rows)
            db.Entry(row).State = EntityState.Detached;
    }

    /// <summary>
    /// A file counts as read only while its content is stored. One whose blob is gone goes
    /// back to the list of files to fetch.
    /// </summary>
    public async Task RequeueLostAsync(CancellationToken ct)
    {
        var workspaceId = scan.WorkspaceId;
        var lost = await db.ScanFiles
            .Where(f => f.ScanId == scan.Id && f.State == ScanFileState.Read
                && !db.Blobs.Any(b => b.WorkspaceId == workspaceId && b.BlobSha == f.BlobSha && b.Content != null))
            .ToListAsync(ct);
        if (lost.Count == 0)
            return;
        foreach (var file in lost)
        {
            file.State = ScanFileState.Pending;
            scan.FilesRead--;
            scan.BytesRead -= file.Size;
        }
        await SaveAsync(ct);
    }

    /// <summary>The files still to fetch, in path order.</summary>
    public async Task<List<ScanFileEntity>> PendingAsync(CancellationToken ct) =>
        (await db.ScanFiles.Where(f => f.ScanId == scan.Id && f.State == ScanFileState.Pending).ToListAsync(ct))
            .OrderBy(f => f.Path, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Settles a file whose blob is stored already, without asking the Source. False when no
    /// blob is stored and the fetcher has to fetch it.
    /// </summary>
    public async Task<bool> UseStoredAsync(ScanFileEntity file, CancellationToken ct)
    {
        var stored = await db.Blobs.AsNoTracking()
            .Where(b => b.WorkspaceId == scan.WorkspaceId && b.BlobSha == file.BlobSha)
            .Select(b => new { b.WithheldReason, HasContent = b.Content != null })
            .FirstOrDefaultAsync(ct);
        if (stored is null)
            return false;
        if (stored.HasContent)
            MarkRead(file, file.Size);
        else
            Skip(file, stored.WithheldReason ?? "secret-file", partial: false);
        return true;
    }

    /// <summary>
    /// The bytes must be the blob the listing named, and they must be cleared before they are
    /// stored anywhere. Content that fails the clearance is never written: only the reason is.
    /// </summary>
    public async Task AcceptAsync(ScanFileEntity file, byte[] content, CancellationToken ct)
    {
        if (!ContentClearance.IsBlob(content, file.BlobSha))
            Skip(file, CoverageGap.ShaMismatch, partial: true);
        else if (ContentClearance.Withhold(content) is { } withheld)
        {
            await StoreBlobAsync(file.BlobSha, null, content.LongLength, withheld, ct);
            Skip(file, withheld, partial: false);
        }
        else
        {
            await StoreBlobAsync(file.BlobSha, content, content.LongLength, null, ct);
            MarkRead(file, content.LongLength);
        }
    }

    public void MarkRead(ScanFileEntity file, long bytes)
    {
        file.State = ScanFileState.Read;
        scan.FilesRead++;
        scan.BytesRead += bytes;
    }

    public void Skip(ScanFileEntity file, string reason, bool partial)
    {
        file.State = ScanFileState.Skipped;
        file.Reason = reason;
        scan.FilesSkipped++;
        scan.Partial |= partial;
        scan.Notes = ScanNotes.Write(ScanNotes.Read(scan.Notes).Append(ScanNotes.Of(reason, file.Path)));
    }

    /// <summary>When a scan that a host's rate limit stopped starts again: the host's reset time, bounded.</summary>
    public DateTimeOffset RetryAt(DateTimeOffset? resetAt) => RetryAt(time.GetUtcNow(), resetAt);

    public static DateTimeOffset RetryAt(DateTimeOffset now, DateTimeOffset? resetAt)
    {
        var at = resetAt is null || resetAt == DateTimeOffset.MinValue ? now + DefaultRateLimitWait : resetAt.Value;
        // The host's clock and this one differ, and a reset time can be far away or in the past.
        return at < now + TimeSpan.FromSeconds(1) ? now + TimeSpan.FromSeconds(1)
            : at > now + MaxRateLimitWait ? now + MaxRateLimitWait
            : at;
    }

    private async Task StoreBlobAsync(string blobSha, byte[]? content, long size, string? withheld, CancellationToken ct)
    {
        // Two scans of one workspace can fetch the same blob at once. The content is the
        // same, and a row that says "withheld" is never given content.
        var now = time.GetUtcNow();
        var workspaceId = scan.WorkspaceId;
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO import.blobs (workspace_id, blob_sha, content, size, withheld_reason, created_at)
            VALUES ({workspaceId}, {blobSha}, {content}, {size}, {withheld}, {now})
            ON CONFLICT (workspace_id, blob_sha) DO NOTHING
            """, ct);
    }
}
