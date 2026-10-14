using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Vellum.Modules.Build;
using Vellum.Modules.Modelling;
using Vellum.Modules.Promises;

namespace Vellum.Tests.Modules.Build;

/// <summary>
/// The zip of a bundle is the same bytes on every operating system. The golden file was
/// written once and is committed; the writer must give exactly its bytes, whatever runs it.
/// </summary>
public class ZipWriterTests
{
    private const string GoldenSha256 = "38fbb9d42efcd4917f138b78d4da597a822b8a7688027790ec3f64ce5b477fe2";

    private static readonly KeyValuePair<string, byte[]>[] GoldenFiles =
    [
        new("manifest.json", Encoding.UTF8.GetBytes("{\n  \"format\": \"vellum-bundle/1\"\n}\n")),
        new("SDD.md", Encoding.UTF8.GetBytes("# Orders: Software Design Document\n")),
        new("openapi/00000000-0000-0000-0000-000000000003.yaml", Encoding.UTF8.GetBytes("openapi: \"3.1.0\"\n")),
        new("scenarios/empty.json", []),
        // A name that is not ASCII, so the UTF-8 flag is in the golden bytes.
        new("notes/caf" + (char)0xE9 + ".txt", [0, 1, 2, 255]),
    ];

    private static string GoldenPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Build", "golden.zip");

    [Fact]
    public void The_writer_gives_the_bytes_of_the_committed_golden_zip()
    {
        var zip = ZipWriter.Write(GoldenFiles);

        Assert.Equal(GoldenSha256, Convert.ToHexStringLower(SHA256.HashData(zip)));
        Assert.Equal(GoldenSha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(GoldenPath))));

        // The order of the input does not show.
        Assert.Equal(zip, ZipWriter.Write(GoldenFiles.Reverse()));
    }

    [Fact]
    public void The_zip_reads_back_with_the_same_names_and_bytes_in_ordinal_order()
    {
        using var archive = new ZipArchive(new MemoryStream(ZipWriter.Write(GoldenFiles)), ZipArchiveMode.Read);

        Assert.Equal(GoldenFiles.Select(f => f.Key).Order(StringComparer.Ordinal), archive.Entries.Select(e => e.FullName));
        foreach (var (name, bytes) in GoldenFiles)
        {
            var entry = archive.GetEntry(name)!;
            using var content = new MemoryStream();
            using (var stream = entry.Open())
                stream.CopyTo(content);
            Assert.Equal(bytes, content.ToArray());
            Assert.Equal(new DateTime(1980, 1, 1), entry.LastWriteTime.DateTime);
        }
    }

    [Fact]
    public void Every_header_field_is_fixed_and_no_entry_has_an_extra_field_or_a_data_descriptor()
    {
        AssertFixedHeaders(ZipWriter.Write(GoldenFiles), GoldenFiles.Length);
    }

    /// <summary>The zip of a real bundle goes through the same writer.</summary>
    [Fact]
    public void The_zip_of_a_bundle_has_the_fixed_header_fields()
    {
        var id = new Guid("00000000-0000-0000-0000-000000000001");
        object[] events = [new ModelEvent.ElementAdded(id, ElementKind.App, "Orders", null, null, null, ElementStatus.Current, null, [])];
        var bundle = Bundle.Build(
            new BuildInput(id, "Orders", new ModelPin(id, 1, null, null), events.Aggregate(ModelState.Initial, ModelState.Evolve), [], [], []),
            BuildLimits.Default);

        AssertFixedHeaders(bundle.Zip(), bundle.Files.Count + 1);
    }

    [Fact]
    public void Crc32_is_the_crc_of_the_zip_format()
    {
        Assert.Equal(0xCBF43926u, ZipWriter.Crc32("123456789"u8));
        Assert.Equal(0u, ZipWriter.Crc32([]));
    }

    /// <summary>Reads the raw records of the archive: the local header of each entry, the central directory and its end.</summary>
    private static void AssertFixedHeaders(byte[] zip, int entries)
    {
        static ushort U16(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]);
        static uint U32(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);

        var end = zip.Length - 22;
        Assert.Equal(0x06054b50u, U32(zip, end));
        Assert.Equal(entries, U16(zip, end + 8));
        Assert.Equal(entries, U16(zip, end + 10));
        Assert.Equal(0, U16(zip, end + 20));   // no archive comment
        var directory = (int)U32(zip, end + 16);
        Assert.Equal(end, directory + (int)U32(zip, end + 12));

        var at = directory;
        var nextLocal = 0;
        for (var i = 0; i < entries; i++)
        {
            Assert.Equal(0x02014b50u, U32(zip, at));
            Assert.Equal(0x0314, U16(zip, at + 4));          // version made by: 2.0, Unix
            Assert.Equal(20, U16(zip, at + 6));              // version needed
            var flags = U16(zip, at + 8);
            Assert.Equal(0, flags & ~0x0800);                // no data descriptor, no encryption; the UTF-8 name flag only
            Assert.Equal(0, U16(zip, at + 10));              // stored
            Assert.Equal(0, U16(zip, at + 12));              // time 00:00:00
            Assert.Equal(0x0021, U16(zip, at + 14));         // date 1980-01-01
            var size = U32(zip, at + 20);
            Assert.Equal(size, U32(zip, at + 24));
            var nameLength = U16(zip, at + 28);
            Assert.Equal(0, U16(zip, at + 30));              // no extra field
            Assert.Equal(0, U16(zip, at + 32));              // no comment
            Assert.Equal(0, U16(zip, at + 36));              // internal attributes
            Assert.Equal(0x81A40000u, U32(zip, at + 38));    // external attributes: regular file, 0644
            var local = (int)U32(zip, at + 42);
            Assert.Equal(nextLocal, local);

            Assert.Equal(0x04034b50u, U32(zip, local));
            Assert.Equal(20, U16(zip, local + 4));
            Assert.Equal(flags, U16(zip, local + 6));
            Assert.Equal(0, U16(zip, local + 8));
            Assert.Equal(0, U16(zip, local + 10));
            Assert.Equal(0x0021, U16(zip, local + 12));
            Assert.Equal(U32(zip, at + 16), U32(zip, local + 14));   // the CRC-32 is in the local header
            Assert.Equal(size, U32(zip, local + 18));
            Assert.Equal(size, U32(zip, local + 22));
            Assert.Equal(nameLength, U16(zip, local + 26));
            Assert.Equal(0, U16(zip, local + 28));
            Assert.True(zip.AsSpan(local + 30, nameLength).SequenceEqual(zip.AsSpan(at + 46, nameLength)));

            nextLocal = local + 30 + nameLength + (int)size;
            at += 46 + nameLength;
        }
        Assert.Equal(directory, nextLocal);
        Assert.Equal(end, at);
    }
}
