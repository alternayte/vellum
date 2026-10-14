using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Settings;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Import.Sources;

/// <summary>
/// <c>POST /api/projects/{projectId}/sources/{sourceId}/archive</c>: one <c>.zip</c> or
/// <c>.tar.gz</c> as the raw request body. Each upload is a new scan of the Source, so a new
/// Snapshot.
/// <para>
/// The body is streamed to a temporary file in the data folder, never held in memory as a
/// whole; the file is deleted when the request ends, so the archive is not kept. No entry is
/// extracted to disk. The archive is read twice from that file: once to check every entry and
/// to list the files with their blob SHAs, and once to read the files the scan rules select.
/// A refusal stores nothing. Only the cleared blobs of the selected files are stored, with the
/// scan and its file rows, in one transaction.
/// </para>
/// </summary>
public static class ArchiveUpload
{
    /// <summary>What an upload may hold beyond the expanded limit: the headers and the central directory of many small entries.</summary>
    public const long UploadOverhead = 64L * 1024 * 1024;

    /// <summary>The body limit of the route: the largest limit a workspace can set, and the overhead.</summary>
    public const long MaxBodyBytes = ImportSettings.MaxArchiveBytesLimit + UploadOverhead;

    public static async Task<IResult> Handle(
        Guid projectId, Guid sourceId, ClaimsPrincipal user, HttpContext http,
        WorkspaceAuthorizationService auth, SettingsResolver settings, AppDbContext db, ScanSignal signal,
        Bootstrap bootstrap, TimeProvider time, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Editor, ct);
        var source = await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId && s.ProjectId == projectId, ct);
        if (source is null)
            return NotFound("Source");
        if (source.Kind != SourceKind.Archive)
        {
            return Results.BadRequest(new ErrorResponse("source_kind_mismatch",
                $"This Source is of kind '{EnumText.Of(source.Kind)}'. Upload an archive to a Source of kind 'archive'."));
        }

        var workspace = await settings.GetWorkspaceAsync(source.WorkspaceId, ct);
        var import = workspace.Effective.Import;
        var cap = import.MaxArchiveBytes + UploadOverhead;
        if (http.Request.ContentLength > cap)
            return Refuse(ArchiveRefused.UploadTooLarge(cap), StatusCodes.Status413PayloadTooLarge);
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
            bodySize.MaxRequestBodySize = cap;

        var folder = Path.Combine(bootstrap.DataDirectory, "uploads");
        Directory.CreateDirectory(folder);
        await using var spool = new FileStream(Path.Combine(folder, $"{Guid.NewGuid():N}.upload"), new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
            Options = FileOptions.DeleteOnClose | FileOptions.Asynchronous,
        });

        try
        {
            await SpoolAsync(http.Request.Body, spool, cap, ct);
        }
        catch (ArchiveRefused refused)
        {
            return Refuse(refused, StatusCodes.Status413PayloadTooLarge);
        }

        var head = new byte[4];
        spool.Position = 0;
        var headLength = await spool.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        if (ArchiveReader.Detect(head.AsSpan(0, headLength)) is not { } format)
            return Refuse(ArchiveRefused.Format(), StatusCodes.Status400BadRequest);

        var limits = new ScanLimits(import.MaxFiles, import.MaxFileBytes, import.MaxTotalBytes, [.. import.ExcludeGlobs]);
        List<ArchiveFile> files;
        Dictionary<string, byte[]> content;
        try
        {
            (files, content) = await Task.Run(() =>
            {
                var listed = ArchiveReader.List(spool, format, import.MaxArchiveBytes, out var strip);
                var selection = ScanScope.Select([.. listed.Select(ArchiveFiles.Entry)], limits, [], []);
                var wanted = selection.Files.Where(f => f.Reason is null).Select(f => f.Entry.Path).ToHashSet(StringComparer.Ordinal);
                return (listed, ArchiveReader.Read(spool, format, import.MaxArchiveBytes, wanted, strip));
            }, ct);
        }
        catch (ArchiveRefused refused)
        {
            return Refuse(refused, StatusCodes.Status400BadRequest);
        }

        return await ScanRequests.QueueAsync(projectId, source, new ScanRequests.ScanOptions(null, null, null, null),
            user, workspace, db, signal, time.GetUtcNow(), ct,
            async (scan, token) =>
            {
                var context = new FetchContext(http.RequestServices, db, scan, source, ScanLimits.FromJson(scan.Limits), time);
                await context.ListAsync([.. files.Select(ArchiveFiles.Entry)], token);
                foreach (var file in await context.PendingAsync(token))
                {
                    if (content.TryGetValue(file.Path, out var bytes))
                        await context.AcceptAsync(file, bytes, token);
                    else
                        context.Skip(file, "blob-missing", partial: true);
                }
                await context.SaveAsync(token);
            });
    }

    private static async Task SpoolAsync(Stream body, FileStream spool, long cap, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await body.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > cap)
                throw ArchiveRefused.UploadTooLarge(cap);
            await spool.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        await spool.FlushAsync(ct);
    }

    private static IResult Refuse(ArchiveRefused refused, int status) =>
        Results.Json(new ErrorResponse(refused.Code, refused.Message), statusCode: status);
}
