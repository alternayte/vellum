using System.Buffers.Binary;
using System.Text;

namespace Vellum.Modules.Build;

/// <summary>
/// A zip writer whose output depends only on the names and the bytes of the entries, on every
/// operating system and runtime version. Every field of every header is written here:
/// <list type="bullet">
/// <item>entries are stored without compression, in ordinal order of their names;</item>
/// <item>"version made by" is 2.0 on Unix (<c>0x0314</c>), "version needed" is 2.0;</item>
/// <item>the time is 1980-01-01 00:00:00, the first time the format can hold;</item>
/// <item>the external attributes are those of a regular file with mode 0644, the internal attributes are 0;</item>
/// <item>there is no extra field, no comment and no data descriptor; the sizes and the CRC-32 are in the local header;</item>
/// <item>the flag for a UTF-8 name is set only for a name that is not ASCII.</item>
/// </list>
/// Zip64 is not written: an archive that needs it is refused.
/// </summary>
internal static class ZipWriter
{
    public const ushort VersionMadeBy = 0x0314;
    public const ushort VersionNeeded = 20;
    public const uint ExternalAttributes = 0x81A4_0000;

    private const uint LocalHeaderSignature = 0x04034b50;
    private const uint CentralHeaderSignature = 0x02014b50;
    private const uint EndSignature = 0x06054b50;
    private const ushort Utf8Flag = 0x0800;
    private const ushort Stored = 0;
    private const ushort DosTime = 0;
    private const ushort DosDate = (0 << 9) | (1 << 5) | 1;

    private static readonly uint[] CrcTable = MakeCrcTable();

    /// <param name="entries">The files by name. A name uses <c>/</c> between its parts.</param>
    /// <exception cref="BundleTooLargeException">The archive would need zip64: more than 65535 entries, or 4 GiB or more.</exception>
    public static byte[] Write(IEnumerable<KeyValuePair<string, byte[]>> entries)
    {
        var ordered = entries.OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
        if (ordered.Count > ushort.MaxValue)
            throw new BundleTooLargeException($"A zip without zip64 holds at most {ushort.MaxValue} files.");

        using var archive = new MemoryStream();
        using var central = new MemoryStream();
        Span<byte> local = stackalloc byte[30];
        Span<byte> directory = stackalloc byte[46];
        foreach (var (path, content) in ordered)
        {
            var name = Encoding.UTF8.GetBytes(path);
            if (name.Length > ushort.MaxValue || archive.Length + central.Length + content.Length + name.Length * 2L + 98 >= uint.MaxValue)
                throw new BundleTooLargeException("The bundle is too large for a zip without zip64.");

            var flags = name.Length == path.Length ? (ushort)0 : Utf8Flag;
            var crc = Crc32(content);
            var offset = (uint)archive.Length;

            BinaryPrimitives.WriteUInt32LittleEndian(local, LocalHeaderSignature);
            BinaryPrimitives.WriteUInt16LittleEndian(local[4..], VersionNeeded);
            BinaryPrimitives.WriteUInt16LittleEndian(local[6..], flags);
            BinaryPrimitives.WriteUInt16LittleEndian(local[8..], Stored);
            BinaryPrimitives.WriteUInt16LittleEndian(local[10..], DosTime);
            BinaryPrimitives.WriteUInt16LittleEndian(local[12..], DosDate);
            BinaryPrimitives.WriteUInt32LittleEndian(local[14..], crc);
            BinaryPrimitives.WriteUInt32LittleEndian(local[18..], (uint)content.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(local[22..], (uint)content.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(local[26..], (ushort)name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(local[28..], 0);
            archive.Write(local);
            archive.Write(name);
            archive.Write(content);

            BinaryPrimitives.WriteUInt32LittleEndian(directory, CentralHeaderSignature);
            BinaryPrimitives.WriteUInt16LittleEndian(directory[4..], VersionMadeBy);
            BinaryPrimitives.WriteUInt16LittleEndian(directory[6..], VersionNeeded);
            BinaryPrimitives.WriteUInt16LittleEndian(directory[8..], flags);
            BinaryPrimitives.WriteUInt16LittleEndian(directory[10..], Stored);
            BinaryPrimitives.WriteUInt16LittleEndian(directory[12..], DosTime);
            BinaryPrimitives.WriteUInt16LittleEndian(directory[14..], DosDate);
            BinaryPrimitives.WriteUInt32LittleEndian(directory[16..], crc);
            BinaryPrimitives.WriteUInt32LittleEndian(directory[20..], (uint)content.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(directory[24..], (uint)content.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(directory[28..], (ushort)name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(directory[30..], 0);   // extra field length
            BinaryPrimitives.WriteUInt16LittleEndian(directory[32..], 0);   // comment length
            BinaryPrimitives.WriteUInt16LittleEndian(directory[34..], 0);   // disk number
            BinaryPrimitives.WriteUInt16LittleEndian(directory[36..], 0);   // internal attributes
            BinaryPrimitives.WriteUInt32LittleEndian(directory[38..], ExternalAttributes);
            BinaryPrimitives.WriteUInt32LittleEndian(directory[42..], offset);
            central.Write(directory);
            central.Write(name);
        }

        var directoryOffset = (uint)archive.Length;
        central.WriteTo(archive);

        Span<byte> end = stackalloc byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(end, EndSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(end[4..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(end[6..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(end[8..], (ushort)ordered.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(end[10..], (ushort)ordered.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(end[12..], (uint)central.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(end[16..], directoryOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(end[20..], 0);
        archive.Write(end);
        return archive.ToArray();
    }

    /// <summary>The CRC-32 of the zip format (polynomial 0xEDB88320, reflected).</summary>
    internal static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFF_FFFFu;
        foreach (var b in bytes)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB8_8320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
