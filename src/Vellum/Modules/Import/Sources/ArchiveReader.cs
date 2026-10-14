using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Vellum.Modules.GitHub;

namespace Vellum.Modules.Import.Sources;

/// <summary>Why an uploaded archive is refused as a whole. The message names no entry: entry names are untrusted text.</summary>
public sealed class ArchiveRefused(string code, string message) : Exception(message)
{
    public string Code { get; } = code;

    public static ArchiveRefused Format() => new("archive_format",
        "The upload is not a .zip or a .tar.gz archive. Vellum reads these two formats.");

    public static ArchiveRefused Unreadable() => new("archive_unreadable",
        "The archive is damaged, encrypted or not what its format says. Nothing of it was stored.");

    public static ArchiveRefused AbsolutePath() => new("archive_absolute_path",
        "An entry of the archive has an absolute path (or a backslash or a drive letter). Vellum refuses the whole archive. Pack the files with paths relative to the archive.");

    public static ArchiveRefused Traversal() => new("archive_path_traversal",
        "An entry of the archive has a '..' part (or an empty or '.' part) in its path, which would place it outside the archive. Vellum refuses the whole archive.");

    public static ArchiveRefused Link() => new("archive_link_entry",
        "The archive holds a symbolic link, a hard link, a device or another entry that is not a plain file or a folder. Vellum refuses the whole archive. Pack the files the links point at instead.");

    public static ArchiveRefused Nested() => new("archive_nested",
        "The archive holds another archive. Vellum does not open archives inside archives and refuses the whole upload. Unpack the inner archive first.");

    public static ArchiveRefused Duplicate() => new("archive_duplicate_path",
        "Two entries of the archive have the same path, so the archive does not say which content is meant. Vellum refuses the whole archive.");

    public static ArchiveRefused TooLarge(long limit) => new("archive_too_large",
        $"The files of the archive expand to more than {limit} bytes, the limit import.maxArchiveBytes of this workspace. Vellum refuses the whole archive. Upload a smaller archive or raise the limit.");

    public static ArchiveRefused UploadTooLarge(long limit) => new("archive_too_large",
        $"The upload is larger than {limit} bytes, which no archive within import.maxArchiveBytes needs. Vellum refuses it.");

    public static ArchiveRefused Ratio() => new("archive_compression_ratio",
        $"An entry of the archive expands more than {ArchiveReader.MaxCompressionRatio} times its compressed size. Such an archive is how a decompression bomb looks, so Vellum refuses it.");

    public static ArchiveRefused TooManyEntries() => new("archive_too_many_entries",
        $"The archive holds more than {ArchiveReader.MaxEntries} entries. Vellum refuses it.");
}

/// <summary>One file of an archive: its path after the top folder is taken off, its size, its git blob SHA.</summary>
internal sealed record ArchiveFile(string Path, long Size, string BlobSha);

/// <summary>
/// Reads an uploaded <c>.zip</c> or <c>.tar.gz</c> that the caller spooled to a temporary file.
/// Nothing is extracted to disk and nothing in it runs. The first pass reads every entry and
/// refuses the whole archive on an absolute path, a <c>..</c> part, a link or device entry, a
/// nested archive, two entries with one path, an expanded total over the workspace limit or an
/// entry that expands more than <see cref="MaxCompressionRatio"/> times. It computes the git blob
/// SHA of every file and keeps no content. The second pass reads only the files the scan rules
/// selected, so memory holds at most the bytes a scan may read.
/// </summary>
internal static class ArchiveReader
{
    /// <summary>The most an entry may expand, relative to its compressed size, once it is over <see cref="RatioFloorBytes"/>.</summary>
    public const int MaxCompressionRatio = 100;
    /// <summary>Below this many expanded bytes the ratio is not checked: a small file of one repeated character is no bomb.</summary>
    public const long RatioFloorBytes = 64 * 1024;
    public const int MaxEntries = 200_000;

    private static readonly string[] ArchiveExtensions =
    [
        ".zip", ".jar", ".war", ".ear", ".aar", ".apk", ".nupkg", ".whl", ".egg", ".tar", ".tgz", ".gz", ".bz2", ".tbz", ".tbz2",
        ".xz", ".txz", ".zst", ".7z", ".rar", ".cab", ".lz", ".lzma", ".z",
    ];

    public enum Format { Zip, TarGz }

    /// <summary>The format by the first bytes: <c>PK</c> for zip, <c>1f 8b</c> for gzip. Null for anything else.</summary>
    public static Format? Detect(ReadOnlySpan<byte> head) =>
        head.Length >= 4 && head[0] == 'P' && head[1] == 'K' && ((head[2] == 3 && head[3] == 4) || (head[2] == 5 && head[3] == 6)) ? Format.Zip
        : head.Length >= 2 && head[0] == 0x1f && head[1] == 0x8b ? Format.TarGz
        : null;

    /// <summary>
    /// Every file of the archive, in path order, with the top folder taken off when all files
    /// share one. <paramref name="strip"/> is that folder, or null.
    /// </summary>
    public static List<ArchiveFile> List(FileStream archive, Format format, long maxExpanded, out string? strip)
    {
        var files = new List<ArchiveFile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long expanded = 0;
        Walk(archive, format, maxExpanded, (path, declared, read) =>
        {
            if (!seen.Add(path))
                throw ArchiveRefused.Duplicate();
            if (expanded + declared > maxExpanded)
                throw ArchiveRefused.TooLarge(maxExpanded);
            using var sha = new BlobHasher(declared);
            var size = read(sha.Append, maxExpanded - expanded);
            // The hash starts with the declared length; an entry whose bytes differ from it lies.
            if (size != declared)
                throw ArchiveRefused.Unreadable();
            expanded += size;
            files.Add(new ArchiveFile(path, size, sha.Finish()));
        });

        var top = TopFolder(files.Select(f => f.Path));
        strip = top;
        return [.. files
            .Select(f => top is null ? f : f with { Path = f.Path[(top.Length + 1)..] })
            .OrderBy(f => f.Path, StringComparer.Ordinal)];
    }

    /// <summary>The bytes of the files in <paramref name="wanted"/>, keyed by their path after the top folder is taken off.</summary>
    public static Dictionary<string, byte[]> Read(FileStream archive, Format format, long maxExpanded, IReadOnlySet<string> wanted, string? strip)
    {
        var content = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        Walk(archive, format, maxExpanded, (path, _, read) =>
        {
            var key = strip is null ? path : path[(strip.Length + 1)..];
            if (!wanted.Contains(key))
            {
                read(static (_, _) => { }, maxExpanded);
                return;
            }
            var buffer = new MemoryStream();
            read((chunk, count) => buffer.Write(chunk, 0, count), maxExpanded);
            content[key] = buffer.ToArray();
        });
        return content;
    }

    /// <summary>The folder every file path starts with, when there is one and no file sits beside it. Null otherwise.</summary>
    public static string? TopFolder(IEnumerable<string> paths)
    {
        string? top = null;
        foreach (var path in paths)
        {
            var slash = path.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0)
                return null;
            var first = path[..slash];
            if (top is null)
                top = first;
            else if (top != first)
                return null;
        }
        return top;
    }

    /// <summary>Reads the entry's bytes into the sink and returns how many there were. At most <c>limit</c> bytes are read; one more refuses the archive.</summary>
    private delegate long EntryReader(Action<byte[], int> sink, long limit);

    private static void Walk(FileStream archive, Format format, long maxExpanded, Action<string, long, EntryReader> onFile)
    {
        archive.Position = 0;
        try
        {
            if (format == Format.Zip)
                WalkZip(archive, maxExpanded, onFile);
            else
                WalkTarGz(archive, maxExpanded, onFile);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or FormatException or NotSupportedException
                                       or ArgumentException or IOException)
        {
            throw ArchiveRefused.Unreadable();
        }
    }

    private static void WalkZip(FileStream archive, long maxExpanded, Action<string, long, EntryReader> onFile)
    {
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: Encoding.UTF8);
        if (zip.Entries.Count > MaxEntries)
            throw ArchiveRefused.TooManyEntries();
        foreach (var entry in zip.Entries)
        {
            // The upper 16 bits of the external attributes hold the Unix mode when the archive
            // was made on Unix: only a plain file (0x8000), a folder (0x4000) or none (0) passes.
            var type = (entry.ExternalAttributes >> 16) & 0xF000;
            if (type is not (0 or 0x8000 or 0x4000))
                throw ArchiveRefused.Link();
            if (entry.IsEncrypted)
                throw ArchiveRefused.Unreadable();
            if (Normalize(entry.FullName) is not { } path)
                continue;
            if (type == 0x4000)
                continue;
            if (entry.Length > maxExpanded)
                throw ArchiveRefused.TooLarge(maxExpanded);
            var compressed = Math.Max(1, entry.CompressedLength);
            onFile(path, entry.Length, (sink, limit) =>
            {
                using var stream = entry.Open();
                return Copy(stream, sink, limit, maxExpanded, read => read > RatioFloorBytes && read > compressed * MaxCompressionRatio);
            });
        }
    }

    private static void WalkTarGz(FileStream archive, long maxExpanded, Action<string, long, EntryReader> onFile)
    {
        var compressed = new CountingStream(archive);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: true);
        var expanded = new CountingStream(gzip);
        using var tar = new TarReader(expanded, leaveOpen: true);
        var entries = 0;
        bool Bomb(long _) => expanded.Count > RatioFloorBytes && expanded.Count > Math.Max(1, compressed.Count) * MaxCompressionRatio;
        while (tar.GetNextEntry(copyData: false) is { } entry)
        {
            if (++entries > MaxEntries)
                throw ArchiveRefused.TooManyEntries();
            if (Bomb(0))
                throw ArchiveRefused.Ratio();
            switch (entry.EntryType)
            {
                case TarEntryType.GlobalExtendedAttributes:
                case TarEntryType.ExtendedAttributes:
                    continue;
                case TarEntryType.Directory:
                    Normalize(entry.Name.TrimEnd('/') + "/");
                    continue;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    break;
                default:
                    // A symbolic link, a hard link, a device, a pipe, a sparse or multi-volume part.
                    throw ArchiveRefused.Link();
            }
            if (Normalize(entry.Name) is not { } path)
                continue;
            if (entry.Length > maxExpanded)
                throw ArchiveRefused.TooLarge(maxExpanded);
            var data = entry.DataStream;
            onFile(path, entry.Length, (sink, limit) => data is null ? 0 : Copy(data, sink, limit, maxExpanded, Bomb));
        }
    }

    /// <summary>
    /// Copies an entry to the sink in chunks. The bytes are counted as they come, not as the
    /// headers declare them: an entry that expands past the workspace limit, or past the ratio,
    /// refuses the archive. The first bytes are checked for the signature of another archive.
    /// </summary>
    private static long Copy(Stream source, Action<byte[], int> sink, long limit, long maxExpanded, Func<long, bool> bomb)
    {
        var buffer = new byte[81920];
        long total = 0;
        var head = new byte[262];
        var headLength = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (headLength < head.Length)
            {
                var take = Math.Min(read, head.Length - headLength);
                Array.Copy(buffer, 0, head, headLength, take);
                headLength += take;
            }
            total += read;
            if (total > limit || total > maxExpanded)
                throw ArchiveRefused.TooLarge(maxExpanded);
            if (bomb(total))
                throw ArchiveRefused.Ratio();
            sink(buffer, read);
        }
        if (IsArchive(head.AsSpan(0, headLength)))
            throw ArchiveRefused.Nested();
        return total;
    }

    /// <summary>
    /// The path of an entry with <c>/</c> between its parts, or null for a folder entry. A path
    /// that is absolute, holds a backslash, a drive letter or a control character, or has an
    /// empty, <c>.</c> or <c>..</c> part (after a leading <c>./</c>) refuses the archive. A name
    /// that says it is an archive refuses it too.
    /// </summary>
    internal static string? Normalize(string name)
    {
        if (name.Length == 0 || name.Any(char.IsControl))
            throw ArchiveRefused.Traversal();
        if (name[0] == '/' || name.Contains('\\') || (name.Length >= 2 && name[1] == ':'))
            throw ArchiveRefused.AbsolutePath();
        var path = name;
        while (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        var folder = path.EndsWith('/');
        if (folder)
            path = path.TrimEnd('/');
        if (path.Length == 0)
            return null;
        if (path.Split('/').Any(part => part is "" or "." or ".."))
            throw ArchiveRefused.Traversal();
        if (folder)
            return null;
        var dot = path.LastIndexOf('.');
        if (dot > path.LastIndexOf('/') && ArchiveExtensions.Contains(path[dot..].ToLowerInvariant()))
            throw ArchiveRefused.Nested();
        return path;
    }

    /// <summary>The signatures of zip, gzip, bzip2, xz, 7z, rar and zstd, and the <c>ustar</c> mark of a tar.</summary>
    internal static bool IsArchive(ReadOnlySpan<byte> head) =>
        Detect(head) is not null
        || head.StartsWith("BZh"u8)
        || head.StartsWith((ReadOnlySpan<byte>)[0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00])
        || head.StartsWith((ReadOnlySpan<byte>)[0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C])
        || head.StartsWith("Rar!"u8)
        || head.StartsWith((ReadOnlySpan<byte>)[0x28, 0xB5, 0x2F, 0xFD])
        || (head.Length >= 262 && head[257..262].SequenceEqual("ustar"u8));

    /// <summary>
    /// The git blob SHA of content that arrives in chunks: SHA-1 over <c>blob &lt;size&gt;\0</c>
    /// and the bytes, as <c>git hash-object</c> computes it. The header needs the size first, so
    /// it takes the size the entry declares; the caller refuses an entry whose bytes differ.
    /// </summary>
    private sealed class BlobHasher : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

        public BlobHasher(long declared) =>
            _hash.AppendData(Encoding.ASCII.GetBytes(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"blob {declared}\0")));

        public void Append(byte[] chunk, int count) => _hash.AppendData(chunk, 0, count);

        public string Finish() => Convert.ToHexStringLower(_hash.GetHashAndReset());

        public void Dispose() => _hash.Dispose();
    }

    /// <summary>A stream that counts the bytes read through it.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long Count { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Count += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>The tree entry form of an archive file, for the scan scope rules.</summary>
internal static class ArchiveFiles
{
    public static GitHubTreeEntry Entry(ArchiveFile file) => new(file.Path, "blob", file.BlobSha, file.Size, "100644");
}
