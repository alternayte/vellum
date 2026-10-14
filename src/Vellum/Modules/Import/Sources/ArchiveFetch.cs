using Vellum.Modules.Import.Scans;

namespace Vellum.Modules.Import.Sources;

/// <summary>
/// The fetch stage of an <c>archive</c> Source. The upload request did the reading: it listed
/// every entry, kept the cleared blobs of the files in scope and wrote the file rows with the
/// scan (<see cref="ArchiveUpload"/>). The archive itself is not kept, so nothing can be fetched
/// again: a file whose stored blob is gone ends the scan, and a new upload is the way on.
/// </summary>
internal static class ArchiveFetch
{
    public static readonly ScanError ContentGone = new("archive_content_gone",
        "The stored files of this upload are gone, and Vellum does not keep the archive. Upload the archive again.");

    public static async Task RunAsync(FetchContext context, CancellationToken ct)
    {
        if (!context.Scan.TreeListed)
            throw ScanStop.Failed(ContentGone);
        await context.RequeueLostAsync(ct);
        foreach (var file in await context.PendingAsync(ct))
        {
            if (!await context.UseStoredAsync(file, ct))
                throw ScanStop.Failed(ContentGone);
            await context.SaveAsync(ct);
        }
        await context.PinContentRevisionAsync(ct);
    }
}
