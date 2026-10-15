using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Vellum.Modules.Import.Sources;

/// <summary>
/// The checks that run on the raw structure of an archive before a library parses it, so no
/// library allocates or reads what a header claims before Vellum has bounded it.
/// </summary>
internal static class ArchiveChecks
{
    /// <summary>The largest central directory of a zip that Vellum reads. 200 000 entries with average names fit.</summary>
    public const long MaxCentralDirectoryBytes = 48L * 1024 * 1024;
    /// <summary>The largest metadata entry of a tar (a PAX header, a GNU long name or link).</summary>
    public const int MaxTarMetadataBytes = 64 * 1024;
    private const int Block = 512;

    /// <summary>
    /// Reads the end of central directory record (and its zip64 form) and walks the central
    /// directory headers before <see cref="ZipArchive"/> loads them. Refuses a directory that
    /// names more than <see cref="ArchiveReader.MaxEntries"/> entries, that is larger than
    /// <see cref="MaxCentralDirectoryBytes"/>, or that lies outside the file.
    /// </summary>
    public static void Zip(FileStream archive)
    {
        var length = archive.Length;
        var tail = new byte[(int)Math.Min(length, 65_535 + 22 + 20)];
        archive.Position = length - tail.Length;
        archive.ReadExactly(tail);
        var eocd = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
        {
            if (tail[i] == 0x50 && tail[i + 1] == 0x4b && tail[i + 2] == 0x05 && tail[i + 3] == 0x06)
            {
                eocd = i;
                break;
            }
        }
        if (eocd < 0)
            throw ArchiveRefused.Unreadable();

        ulong entries = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 10));
        ulong size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 12));
        ulong offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16));
        if (entries == 0xFFFF || size == 0xFFFFFFFF || offset == 0xFFFFFFFF)
        {
            // The zip64 locator sits right before the record and points at the zip64 record.
            var locator = eocd - 20;
            if (locator < 0 || BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(locator)) != 0x07064b50)
                throw ArchiveRefused.Unreadable();
            var at = BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(locator + 8));
            if (at > (ulong)length - 56)
                throw ArchiveRefused.Unreadable();
            var record = new byte[56];
            archive.Position = (long)at;
            archive.ReadExactly(record);
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != 0x06064b50)
                throw ArchiveRefused.Unreadable();
            entries = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(32));
            size = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(40));
            offset = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(48));
        }
        if (entries > ArchiveReader.MaxEntries)
            throw ArchiveRefused.TooManyEntries();
        if (size > MaxCentralDirectoryBytes)
            throw ArchiveRefused.TooManyEntries();
        if (offset > (ulong)length || size > (ulong)length - offset)
            throw ArchiveRefused.Unreadable();

        // Count the headers themselves: the record's number can understate them.
        var directory = new byte[(int)size];
        archive.Position = (long)offset;
        archive.ReadExactly(directory);
        var count = 0;
        for (var i = 0; i + 46 <= directory.Length;)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(i)) != 0x02014b50)
                break;
            if (++count > ArchiveReader.MaxEntries)
                throw ArchiveRefused.TooManyEntries();
            i += 46 + BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(i + 28))
                 + BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(i + 30))
                 + BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(i + 32));
        }
    }

    /// <summary>
    /// Walks the tar headers of a <c>.tar.gz</c> block by block before <c>TarReader</c> reads it.
    /// Refuses a metadata entry over <see cref="MaxTarMetadataBytes"/> (a reader would allocate its
    /// declared size), file data over <paramref name="maxExpanded"/>, more than
    /// <see cref="ArchiveReader.MaxEntries"/> entries, and an entry that expands more than
    /// <see cref="ArchiveReader.MaxCompressionRatio"/> times the compressed bytes it took. The
    /// gzip reader gets the file in blocks of 512 bytes, so the compressed bytes of one entry are
    /// known to within one block.
    /// </summary>
    public static void TarGz(FileStream archive, long maxExpanded)
    {
        archive.Position = 0;
        var compressed = new BlockStream(archive);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: true);
        var header = new byte[Block];
        var buffer = new byte[64 * 1024];
        long data = 0;
        var entries = 0;
        long? paxSize = null;
        while (true)
        {
            var start = compressed.Count;
            var got = gzip.ReadAtLeast(header, Block, throwOnEndOfStream: false);
            if (got == 0 || header.AsSpan(0, got).IndexOfAnyExcept((byte)0) < 0)
                return;
            if (got < Block)
                throw ArchiveRefused.Unreadable();
            if (++entries > ArchiveReader.MaxEntries)
                throw ArchiveRefused.TooManyEntries();

            var type = (char)header[156];
            var own = Size(header.AsSpan(124, 12));
            var metadata = type is 'x' or 'g' or 'L' or 'K' or 'X';
            var size = metadata ? own : paxSize ?? own;
            if (!metadata)
                paxSize = null;
            if (metadata && size > MaxTarMetadataBytes)
                throw ArchiveRefused.Unreadable();
            if (!metadata)
            {
                data += size;
                if (data > maxExpanded)
                    throw ArchiveRefused.TooLarge(maxExpanded);
            }

            var padded = (size + Block - 1) / Block * Block;
            long expanded = Block;
            if (type == 'x')
            {
                var record = new byte[padded];
                gzip.ReadExactly(record);
                expanded += padded;
                paxSize = PaxSize(record.AsSpan(0, (int)size)) ?? paxSize;
            }
            else
            {
                for (var left = padded; left > 0;)
                {
                    var read = gzip.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (read == 0)
                        throw ArchiveRefused.Unreadable();
                    left -= read;
                    expanded += read;
                    if (Bomb(expanded, compressed.Count - start))
                        throw ArchiveRefused.Ratio();
                }
            }
            if (Bomb(expanded, compressed.Count - start))
                throw ArchiveRefused.Ratio();
        }
    }

    private static bool Bomb(long expanded, long compressed) =>
        expanded > ArchiveReader.RatioFloorBytes && expanded > (compressed + Block) * ArchiveReader.MaxCompressionRatio;

    /// <summary>A tar size field: octal text, or base-256 when the first byte has its high bit set.</summary>
    private static long Size(ReadOnlySpan<byte> field)
    {
        if ((field[0] & 0x80) != 0)
        {
            if ((field[0] & 0x40) != 0)
                throw ArchiveRefused.Unreadable();
            long value = field[0] & 0x3F;
            foreach (var b in field[1..])
            {
                if (value > long.MaxValue >> 8)
                    throw ArchiveRefused.Unreadable();
                value = (value << 8) | b;
            }
            return value;
        }
        long octal = 0;
        foreach (var b in field)
        {
            if (b is (byte)' ' or 0)
            {
                if (octal == 0)
                    continue;
                break;
            }
            if (b is < (byte)'0' or > (byte)'7' || octal > long.MaxValue >> 3)
                throw ArchiveRefused.Unreadable();
            octal = (octal << 3) | (long)(b - '0');
        }
        return octal;
    }

    /// <summary>The <c>size</c> record of a PAX header, which replaces the size field of the next entry.</summary>
    private static long? PaxSize(ReadOnlySpan<byte> records)
    {
        var text = Encoding.UTF8.GetString(records);
        foreach (var line in text.Split('\n'))
        {
            var space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space < 0)
                continue;
            var pair = line[(space + 1)..];
            if (pair.StartsWith("size=", StringComparison.Ordinal))
            {
                return long.TryParse(pair[5..], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : throw ArchiveRefused.Unreadable();
            }
        }
        return null;
    }

    /// <summary>Hands the file to the gzip reader in blocks of 512 bytes and counts them.</summary>
    private sealed class BlockStream(Stream inner) : Stream
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
            var read = inner.Read(buffer, offset, Math.Min(count, Block));
            Count += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
