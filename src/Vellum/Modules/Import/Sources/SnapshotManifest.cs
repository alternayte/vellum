using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Vellum.Modules.Import.Sources;

/// <summary>One file of a Snapshot: its path, its size in bytes and its git blob SHA.</summary>
public sealed record ManifestEntry(string Path, long Size, string BlobSha);

/// <summary>
/// The identity of a Snapshot. A Snapshot is the manifest of the files a scan read: path,
/// size and git blob SHA. The blob SHA is computed for content of any kind
/// (<see cref="GitBlob.Sha"/>), so one tree read from a git host, an archive or a directory
/// gives the same manifest and the same ID.
/// <para>
/// The ID is the SHA-256, in lower-case hex, of the canonical manifest: the entries ordered by
/// the UTF-8 bytes of their path, each written as <c>path NUL size NUL sha LF</c>, where size
/// is a decimal number and sha is 40 lower-case hex characters. No git path holds NUL, and
/// the SHA has a fixed length, so no two manifests give the same bytes.
/// </para>
/// </summary>
public static class SnapshotManifest
{
    public static string Id(IEnumerable<ManifestEntry> entries) =>
        Convert.ToHexStringLower(SHA256.HashData(Canonical(entries)));

    public static byte[] Canonical(IEnumerable<ManifestEntry> entries)
    {
        var ordered = entries
            .Select(e => (Bytes: Encoding.UTF8.GetBytes(e.Path), Entry: e))
            .OrderBy(e => e.Bytes, ByteOrder.Instance)
            .ToList();
        using var buffer = new MemoryStream();
        foreach (var (path, entry) in ordered)
        {
            if (Array.IndexOf(path, (byte)0) >= 0)
                throw new ArgumentException("A manifest path holds no NUL character.", nameof(entries));
            if (entry.BlobSha.Length != 40 || !entry.BlobSha.All(char.IsAsciiHexDigit))
                throw new ArgumentException("A manifest blob SHA is 40 hexadecimal characters.", nameof(entries));
            buffer.Write(path);
            buffer.WriteByte(0);
            buffer.Write(Encoding.ASCII.GetBytes(entry.Size.ToString(CultureInfo.InvariantCulture)));
            buffer.WriteByte(0);
            buffer.Write(Encoding.ASCII.GetBytes(entry.BlobSha.ToLowerInvariant()));
            buffer.WriteByte((byte)'\n');
        }
        return buffer.ToArray();
    }

    private sealed class ByteOrder : IComparer<byte[]>
    {
        public static readonly ByteOrder Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}

/// <summary>The git blob SHA of any content: SHA-1 over <c>blob &lt;length&gt;\0</c> and the bytes, as <c>git hash-object</c> computes it.</summary>
public static class GitBlob
{
    public static string Sha(ReadOnlySpan<byte> content)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha.AppendData(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"blob {content.Length}\0")));
        sha.AppendData(content);
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}
