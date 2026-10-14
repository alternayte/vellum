using System.Text.RegularExpressions;
using Vellum.Modules.Import.Extraction;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Import.Sources;

namespace Vellum.Cli;

/// <summary>
/// One directory, scanned as the server scans a commit: the same scope rules, the same content
/// clearance, the same extractors and the same manifest. What leaves the machine is the push
/// this builds: paths, sizes, blob SHAs, facts and citations. File content stays here.
/// <para>
/// The walk follows no symbolic link and does not enter a directory that the path filter
/// refuses (dependencies, build output, <c>.git</c>, secret stores). It reads only the files
/// that the scope selects, and each at most to the file limit.
/// </para>
/// </summary>
internal static partial class LocalScan
{
    [GeneratedRegex("^(?:[0-9a-f]{40}|[0-9a-f]{64})$")]
    private static partial Regex Sha();

    public static AgentPushRequest Run(
        string root, string name, ScanLimits limits, IReadOnlyList<string> includePaths, IReadOnlyList<string> excludePaths)
    {
        var listing = new List<ListingEntry>();
        List(new DirectoryInfo(root), "", listing);
        var selection = ScanScope.Select(listing, limits, includePaths, excludePaths);

        var read = new List<(string Path, byte[] Content)>();
        var skipped = new List<AgentPushSkip>();
        var partial = selection.Partial;
        foreach (var file in selection.Files)
        {
            if (file.Reason is not null)
            {
                // A limit left the file out. The server knows every other reason from the path.
                if (file.Reason.StartsWith("limit:", StringComparison.Ordinal))
                    skipped.Add(new AgentPushSkip(file.Entry.Path, file.Reason));
                continue;
            }

            var (content, failure) = ReadBounded(Path.Combine(root, file.Entry.Path), limits.MaxFileBytes);
            if (content is null)
            {
                // The file grew past the limit, or could not be read, after it was listed.
                skipped.Add(new AgentPushSkip(file.Entry.Path, failure!));
                partial = true;
                continue;
            }
            if (ContentClearance.Withhold(content) is { } withheld)
            {
                skipped.Add(new AgentPushSkip(file.Entry.Path, withheld));
                continue;
            }
            read.Add((file.Entry.Path, content));
        }

        var extraction = ScanExtraction.Run(name, "", read, limits);
        var files = new List<AgentPushFile>();
        foreach (var (path, content) in read)
        {
            if (extraction.FileGaps.TryGetValue(path, out var reason))
                skipped.Add(new AgentPushSkip(path, reason));
            else
                files.Add(new AgentPushFile(path, content.LongLength, GitBlob.Sha(content)));
        }

        var facts = extraction.Result.Facts.Select(f => new AgentPushFact(
            f.Kind.ToString(), f.Key, f.Name, f.Attributes,
            [.. f.Evidence.Select(e => new AgentPushCitation(e.Path, e.StartLine, e.EndLine, e.Rule, e.RuleVersion))])).ToList();

        return new AgentPushRequest(
            name, GitHead.CommitSha(root),
            [.. EvidenceExtractor.Rules.Select(r => new AgentPushRule(r.Id, r.Version))],
            new AgentPushScope(limits.MaxFiles, limits.MaxFileBytes, limits.MaxTotalBytes, limits.ExcludeGlobs, includePaths, excludePaths),
            files, skipped, facts, !partial && extraction.Result.IsComplete, extraction.ExtractionGap, listing.Count);
    }

    /// <summary>Every regular file under <paramref name="directory"/>, as a path relative to the root with '/' separators.</summary>
    private static void List(DirectoryInfo directory, string prefix, List<ListingEntry> listing)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }).OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            // A link can point out of the directory. It is never followed.
            if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;
            var path = prefix + entry.Name;
            if (entry is DirectoryInfo child)
            {
                // A directory that the path filter refuses for every file in it is not entered.
                if (PathFilter.SkipReason(path + "/x", out var notePath) is "excluded-directory" or "secret-file" && notePath == path + "/")
                    continue;
                List(child, path + "/", listing);
            }
            else if (entry is FileInfo file)
            {
                listing.Add(new ListingEntry(path, "", file.Length));
            }
        }
    }

    /// <summary>The bytes of the file, or why not: it holds more than <paramref name="maxBytes"/>, or it cannot be read.</summary>
    private static (byte[]? Content, string? Failure) ReadBounded(string path, long maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int count;
            while ((count = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                buffer.Write(chunk, 0, count);
                if (buffer.Length > maxBytes)
                    return (null, "limit:file-bytes");
            }
            return (buffer.ToArray(), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, "read-failure");
        }
    }

    /// <summary>
    /// The commit that <c>.git/HEAD</c> names, read as text: in <paramref name="directory"/> or
    /// the nearest directory above it that has <c>.git</c>. Null when there is none or it cannot
    /// be read. No git program runs.
    /// </summary>
    internal static class GitHead
    {
        private const int MaxText = 64 * 1024;

        public static string? CommitSha(string directory)
        {
            try
            {
                for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current is not null; current = current.Parent)
                {
                    var dotGit = Path.Combine(current.FullName, ".git");
                    if (Directory.Exists(dotGit))
                        return Resolve(dotGit);
                    // A worktree or a submodule: .git is a file that names the git directory.
                    if (File.Exists(dotGit))
                    {
                        var text = Text(dotGit);
                        return text is not null && text.StartsWith("gitdir: ", StringComparison.Ordinal)
                            ? Resolve(Path.GetFullPath(text["gitdir: ".Length..].Trim(), current.FullName))
                            : null;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
            }
            return null;
        }

        private static string? Resolve(string gitDir)
        {
            var head = Text(Path.Combine(gitDir, "HEAD"))?.Trim();
            if (head is null)
                return null;
            if (Sha().IsMatch(head))
                return head;
            if (!head.StartsWith("ref: refs/", StringComparison.Ordinal))
                return null;
            var name = head["ref: ".Length..];
            if (name.Contains("..", StringComparison.Ordinal) || name.Contains('\\') || name.Any(char.IsControl))
                return null;

            // The refs of a worktree are in the common directory of the repository.
            var common = Text(Path.Combine(gitDir, "commondir"))?.Trim() is { Length: > 0 } relative
                ? Path.GetFullPath(relative, gitDir)
                : gitDir;
            foreach (var place in new[] { gitDir, common })
            {
                if (Text(Path.Combine(place, name))?.Trim() is { } loose && Sha().IsMatch(loose))
                    return loose;
            }
            foreach (var line in (Text(Path.Combine(common, "packed-refs")) ?? "").Split('\n'))
            {
                var parts = line.Trim().Split(' ');
                if (parts.Length == 2 && parts[1] == name && Sha().IsMatch(parts[0]))
                    return parts[0];
            }
            return null;
        }

        private static string? Text(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxText)
                return null;
            return File.ReadAllText(path);
        }
    }
}
