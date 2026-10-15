using System.Text;
using System.Text.RegularExpressions;
using Vellum.Modules.GitHub;
using Vellum.Modules.Import.Scans;

namespace Vellum.Modules.Import.Sources;

/// <summary>
/// The fetch stage of a <c>directory</c> Source: the folder under the sources root, read again
/// on every scan. There is no watcher; a rescan is the way to see a change.
/// <para>
/// The folder is resolved to its real path on every scan and must stay under the real root.
/// Inside it, a symbolic link is never followed: it is listed the way git stores it, as a file
/// whose content is the text of its target, so nothing outside the folder is read and the
/// manifest is the one a git host gives for the same tree. A <c>.git</c> folder is not listed.
/// When the folder has one, its <c>HEAD</c> (and the ref file or <c>packed-refs</c> line it names)
/// is read as text, and the commit SHA is kept as the reported commit of the scan. No git command runs.
/// </para>
/// </summary>
internal static partial class DirectoryFetch
{
    /// <summary>The most entries one folder may hold, files and folders together. More is refused before anything is read.</summary>
    public const int MaxEntries = 200_000;
    private const int MaxDepth = 64;
    private const int MaxHeadBytes = 4096;
    private const long MaxPackedRefsBytes = 8 * 1024 * 1024;

    public static async Task RunAsync(FetchContext context, CancellationToken ct)
    {
        var (scan, source) = (context.Scan, context.Source);
        var roots = context.Services.GetRequiredService<SourcesRoot>();
        if (roots.Unavailable() is { } unavailable)
            throw ScanStop.Failed(new ScanError(unavailable.Type, unavailable.Title));
        var folder = roots.Resolve(source.Locator, out var error)
            ?? throw ScanStop.Failed(new ScanError("directory_unreadable", error + " Fix the folder, then start a new scan."));

        if (!scan.TreeListed)
        {
            scan.ReportedCommitSha = ReadHead(folder);
            var entries = List(folder, out var unreadable);
            if (unreadable.Count > 0)
            {
                // A folder that cannot be read is out of what this scan looked at: its files
                // are neither listed nor absent, so no fact under it is proposed for removal or
                // marked stale. The scan is partial and says which folders it could not read.
                scan.ExcludePaths = [.. scan.ExcludePaths.Concat(unreadable).Distinct(StringComparer.Ordinal)];
                scan.Partial = true;
                scan.Notes = ScanNotes.Write(ScanNotes.Read(scan.Notes).Concat(unreadable.Select(path => ScanNotes.Of("folder-unreadable", path))));
            }
            await context.ListAsync(entries, ct);
        }

        foreach (var file in await context.PendingAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            var content = Read(roots.RealRoot()!, folder, file.Path, context.Limits.MaxFileBytes);
            if (content is null)
                context.Skip(file, "file-missing", partial: true);
            else if (content.Length > context.Limits.MaxFileBytes)
                context.Skip(file, "limit:file-bytes", partial: true);
            else
                await context.AcceptAsync(file, content, ct);
            await context.SaveAsync(ct);
        }

        await context.PinContentRevisionAsync(ct);
    }

    /// <summary>
    /// Every file of the folder as a git tree entry: path, mode, no SHA (the read computes it)
    /// and the size the file system gives. Folders named <c>.git</c> are left out, and so is
    /// everything that is neither a file, a folder nor a link (a pipe, a socket, a device).
    /// </summary>
    /// <param name="unreadable">The folders, relative to <paramref name="folder"/>, that could not be listed.</param>
    internal static List<GitHubTreeEntry> List(string folder, out List<string> unreadable)
    {
        var entries = new List<GitHubTreeEntry>();
        var failed = new List<string>();
        var seen = 0;
        void Walk(DirectoryInfo directory, string prefix, int depth)
        {
            if (depth > MaxDepth)
                throw ScanStop.Failed(new ScanError("directory_too_deep", $"The folder nests deeper than {MaxDepth} levels. Vellum does not read it. Choose a smaller folder."));
            List<FileSystemInfo> children;
            try
            {
                children = [.. directory.EnumerateFileSystemInfos("*", new EnumerationOptions
                {
                    AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false,
                })];
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                if (depth == 0)
                    throw ScanStop.Failed(new ScanError("directory_unreadable", "The server cannot list the folder. Check its permissions, then start a new scan."));
                failed.Add(prefix.TrimEnd('/'));
                return;
            }
            foreach (var info in children)
            {
                if (++seen > MaxEntries)
                    throw ScanStop.Failed(new ScanError("directory_too_large", $"The folder holds more than {MaxEntries} entries. Vellum does not read it. Choose a smaller folder."));
                var path = prefix + info.Name;
                if (info.LinkTarget is { } target)
                {
                    entries.Add(new GitHubTreeEntry(path, "blob", "", Encoding.UTF8.GetByteCount(target), "120000"));
                    continue;
                }
                if (info is DirectoryInfo child)
                {
                    if (info.Name != ".git")
                        Walk(child, path + "/", depth + 1);
                    continue;
                }
                if (info is FileInfo file && IsRegular(file))
                {
                    var executable = !OperatingSystem.IsWindows()
                        && (file.UnixFileMode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
                    entries.Add(new GitHubTreeEntry(path, "blob", "", file.Length, executable ? "100755" : "100644"));
                }
            }
        }
        Walk(new DirectoryInfo(folder), "", 0);
        unreadable = failed;
        return entries;
    }

    /// <summary>
    /// The bytes of one listed file, at most <paramref name="maxBytes"/> + 1 of them, or null
    /// when it is gone. A link gives the text of its target. A file whose folder now leaves the
    /// root, or that became a link to a folder or something other than a file, is gone.
    /// </summary>
    internal static byte[]? Read(string root, string folder, string relative, long maxBytes)
    {
        var path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
        var parent = RealPath.Of(Path.GetDirectoryName(path)!);
        if (parent is null || !SourcesRoot.IsUnder(root, parent) || !SourcesRoot.IsUnder(folder, parent))
            return null;
        var info = new FileInfo(Path.Combine(parent, Path.GetFileName(path)));
        if (info.LinkTarget is { } target)
            return Encoding.UTF8.GetBytes(target);
        if (!info.Exists || !IsRegular(info))
            return null;
        // A pipe, a socket or a device reports no length; opening a pipe would wait for a writer.
        // An entry of length 0 is read as empty without being opened.
        if (info.Length == 0)
            return [];
        try
        {
            using var stream = RealPath.OpenNoFollow(info.FullName);
            if (stream is null)
                return null;
            var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > maxBytes)
                    break;
            }
            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Not a folder, not a link and not what Windows calls a device. On Unix .NET does not tell a
    /// pipe or a socket from a file; those report length 0, and <see cref="Read"/> opens no entry of
    /// length 0.
    /// </summary>
    private static bool IsRegular(FileInfo file) =>
        (file.Attributes & (FileAttributes.Device | FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0;

    /// <summary>
    /// The commit SHA that <c>.git/HEAD</c> names, read as text: a detached SHA, or the ref it
    /// points at, from its loose ref file or from <c>packed-refs</c>. Null when there is no
    /// <c>.git</c> folder, when any part is a link, or when the text is not what git writes.
    /// </summary>
    internal static string? ReadHead(string folder)
    {
        var git = new DirectoryInfo(Path.Combine(folder, ".git"));
        if (!git.Exists || git.LinkTarget is not null)
            return null;
        var head = ReadSmall(Path.Combine(git.FullName, "HEAD"), MaxHeadBytes)?.Trim();
        if (head is null)
            return null;
        if (Sha().IsMatch(head))
            return head.ToLowerInvariant();
        if (!head.StartsWith("ref: ", StringComparison.Ordinal))
            return null;
        var name = head[5..].Trim();
        if (!RefName().IsMatch(name) || name.Split('/').Any(part => part is "." or ".." || part.StartsWith('.') || part.EndsWith(".lock", StringComparison.Ordinal)))
            return null;

        var loose = ReadSmall(Path.Combine(git.FullName, name.Replace('/', Path.DirectorySeparatorChar)), MaxHeadBytes)?.Trim();
        if (loose is not null)
            return Sha().IsMatch(loose) ? loose.ToLowerInvariant() : null;

        var packed = ReadSmall(Path.Combine(git.FullName, "packed-refs"), MaxPackedRefsBytes);
        if (packed is null)
            return null;
        foreach (var line in packed.Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split(' ', 2);
            if (parts.Length == 2 && parts[1] == name && Sha().IsMatch(parts[0]))
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    private static string? ReadSmall(string path, long maxBytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || !IsRegular(info) || info.Length == 0 || info.Length > maxBytes)
            return null;
        try
        {
            return File.ReadAllText(info.FullName, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\z")]
    private static partial Regex Sha();

    [GeneratedRegex(@"\Arefs/[A-Za-z0-9._/+-]{1,240}\z")]
    private static partial Regex RefName();
}
