using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Vellum.Cli;

/// <summary>
/// What a path is, without opening it: a regular file or not, and which file it is. .NET says
/// neither (a FIFO, a device and a regular file all look the same to <see cref="FileInfo"/>),
/// so on Linux and macOS this asks the runtime's own <c>lstat</c> and <c>fstat</c>
/// (<c>SystemNative_LStat</c>, <c>SystemNative_FStat</c> in <c>libSystem.Native</c>, which every
/// .NET runtime and every single-file build carries).
/// <para>
/// The identity is the whole status record the runtime writes: type, device, inode, size and
/// times. A file that is opened and then has the same record as the path had before is the file
/// that was checked. One that does not (another file was moved in, a link was put in its place,
/// or it changed) is refused.
/// </para>
/// </summary>
internal static unsafe partial class FileIdentity
{
    /// <summary>Larger than the runtime's status record; the rest stays zero and compares equal.</summary>
    private const int RecordBytes = 512;
    /// <summary>The file type bits of <c>Mode</c>, as the runtime writes them, and the type of a regular file.</summary>
    private const int TypeMask = 0xF000;
    private const int RegularType = 0x8000;
    /// <summary><c>Mode</c> follows the 4-byte <c>Flags</c> at the start of the record.</summary>
    private const int ModeOffset = 4;

    internal sealed record Status(bool IsRegular, byte[] Record);

    [LibraryImport("libSystem.Native", EntryPoint = "SystemNative_LStat", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LStat(string path, byte* output);

    [LibraryImport("libSystem.Native", EntryPoint = "SystemNative_FStat")]
    private static partial int FStat(SafeFileHandle file, byte* output);

    /// <summary>The status of the path itself (a link is not followed), or null when it does not exist.</summary>
    public static Status? Of(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new FileInfo(path);
            return info.Exists ? new Status(!info.Attributes.HasFlag(FileAttributes.ReparsePoint) && !info.Attributes.HasFlag(FileAttributes.Device), []) : null;
        }
        var record = new byte[RecordBytes];
        fixed (byte* output = record)
        {
            if (LStat(path, output) != 0)
                return null;
        }
        return new Status((BitConverter.ToInt32(record, ModeOffset) & TypeMask) == RegularType, record);
    }

    /// <summary>True when the open file is the one <paramref name="before"/> describes. On Windows, where no FIFO is read by mistake, the walk's link check stands alone.</summary>
    public static bool Same(Status before, SafeFileHandle file)
    {
        if (OperatingSystem.IsWindows())
            return true;
        var record = new byte[RecordBytes];
        fixed (byte* output = record)
        {
            if (FStat(file, output) != 0)
                return false;
        }
        return record.AsSpan().SequenceEqual(before.Record);
    }
}
