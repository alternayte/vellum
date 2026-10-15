using System.Runtime.InteropServices;
using Vellum.Modules.Settings;
using Vellum.Shared;

namespace Vellum.Modules.Import.Sources;

/// <summary>
/// The one folder a <c>directory</c> Source may read, on a local instance only: <c>/sources</c>
/// in Compose (a read-only bind mount of <c>VELLUM_SOURCES</c>), or <c>Vellum__SourcesRoot</c>
/// for a server run on the host. A Source keeps a path relative to it.
/// <para>
/// Every path is resolved to its real path, with each symbolic link on the way followed, and
/// must stay under the real path of the root. A path or a link (to a file or a folder) that
/// leaves the root is refused. Nothing here writes, and nothing runs.
/// </para>
/// </summary>
public sealed partial class SourcesRoot
{
    public const string ConfigurationKey = "Vellum:SourcesRoot";
    public const int MaxLocator = 400;

    private readonly string? _configured;

    public SourcesRoot(IConfiguration configuration, Bootstrap bootstrap)
    {
        Profile = bootstrap.Profile;
        _configured = configuration[ConfigurationKey] is { Length: > 0 } value ? value.Trim() : null;
    }

    public OperatingProfile Profile { get; }

    /// <summary>
    /// Why this instance cannot read a directory, as a stable code and a sentence: <c>source_kind_not_in_profile</c>
    /// on a hosted instance, <c>sources_root_not_configured</c> without a root, <c>sources_root_unreadable</c>
    /// when the root is not a folder. Null when it can.
    /// </summary>
    public ErrorResponse? Unavailable()
    {
        if (Profile == OperatingProfile.Hosted)
        {
            return new ErrorResponse("source_kind_not_in_profile",
                "A hosted instance reads no folder of its own machine. Upload an archive, or connect a git host.");
        }
        if (_configured is null)
        {
            return new ErrorResponse("sources_root_not_configured",
                "This instance has no sources root. Set Vellum__SourcesRoot to a folder, or mount one at /sources with VELLUM_SOURCES in Compose, and restart.");
        }
        return RealRoot() is null
            ? new ErrorResponse("sources_root_unreadable", "The sources root of this instance is not a folder the server can read. Check Vellum__SourcesRoot or the /sources mount.")
            : null;
    }

    /// <summary>The real path of the root, without a trailing separator. Null when it is not a folder.</summary>
    public string? RealRoot()
    {
        if (Profile == OperatingProfile.Hosted || _configured is null)
            return null;
        var real = RealPath.Of(Path.GetFullPath(_configured));
        return real is not null && Directory.Exists(real) ? Path.TrimEndingDirectorySeparator(real) : null;
    }

    /// <summary>
    /// Reads the folder a person typed as the locator a Source keeps: a relative path with
    /// <c>/</c> between its parts, no empty part, no <c>.</c> and no <c>..</c>, no backslash and
    /// no control character. An absolute path is refused.
    /// </summary>
    public static bool TryReadLocator(string? value, out string locator, out string error)
    {
        locator = "";
        error = "Give a folder under the sources root as a relative path, such as acme/orders, with no '..' part.";
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxLocator || value.Any(char.IsControl))
            return false;
        var text = value.Trim();
        if (text.StartsWith('/') || text.Contains('\\') || text.Contains(':'))
        {
            error = "Give a path relative to the sources root, not an absolute path.";
            return false;
        }
        text = text.TrimEnd('/');
        var parts = text.Split('/');
        if (parts.Any(part => part is "" or "." or ".."))
            return false;
        locator = text;
        return true;
    }

    /// <summary>
    /// The real path of the folder <paramref name="locator"/> names, when it is a folder under
    /// the real root. Null with the reason when it is not: the root is missing, the path leaves
    /// the root (through <c>..</c> or a link), or it is not a folder.
    /// </summary>
    public string? Resolve(string locator, out string error)
    {
        error = "";
        if (Unavailable() is { } unavailable)
        {
            error = unavailable.Title;
            return null;
        }
        if (!TryReadLocator(locator, out var checkedLocator, out error))
            return null;
        var root = RealRoot()!;
        var real = RealPath.Of(Path.Combine(root, checkedLocator.Replace('/', Path.DirectorySeparatorChar)));
        if (real is null)
        {
            error = "No folder has this path under the sources root.";
            return null;
        }
        if (!IsUnder(root, real))
        {
            error = "The path leaves the sources root through a link. Vellum reads nothing outside the root.";
            return null;
        }
        if (!Directory.Exists(real))
        {
            error = "The path names a file, not a folder.";
            return null;
        }
        return real;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or lies under it. Both are real paths.</summary>
    public static bool IsUnder(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var trimmed = Path.TrimEndingDirectorySeparator(root);
        return string.Equals(path, trimmed, comparison)
            || (path.StartsWith(trimmed, comparison) && path.Length > trimmed.Length && path[trimmed.Length] == Path.DirectorySeparatorChar);
    }
}

/// <summary>The real path of a file system entry: every symbolic link on the way followed, <c>.</c> and <c>..</c> removed.</summary>
internal static partial class RealPath
{
    /// <summary>Null when the entry does not exist or cannot be resolved.</summary>
    public static string? Of(string path)
    {
        if (OperatingSystem.IsWindows())
            return Walk(path);
        var resolved = realpath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero)
            return null;
        try
        {
            return Marshal.PtrToStringUTF8(resolved);
        }
        finally
        {
            free(resolved);
        }
    }

    /// <summary>
    /// Windows has no <c>realpath</c>. The walk follows each link part by part and refuses a
    /// chain longer than 40 links, as the POSIX call does.
    /// </summary>
    private static string? Walk(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        var parts = new Queue<string>(full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries));
        var links = 0;
        while (parts.Count > 0)
        {
            var next = Path.Combine(current, parts.Dequeue());
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (!info.Exists)
                return null;
            if (info.LinkTarget is { } target)
            {
                if (++links > 40)
                    return null;
                var targetFull = Path.GetFullPath(target, current);
                var targetRoot = Path.GetPathRoot(targetFull)!;
                var rest = parts.ToList();
                parts = new Queue<string>(targetFull[targetRoot.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Concat(rest));
                current = targetRoot;
                continue;
            }
            current = next;
        }
        return current;
    }

    /// <summary>
    /// Opens a file for reading only when its last part is no link at the moment of the open
    /// (<c>O_NOFOLLOW</c>) and the handle is a plain file: a pipe, a socket or a character
    /// device cannot seek. <c>O_NONBLOCK</c> keeps the open of a pipe from waiting for a writer.
    /// Null when the open is refused or the handle is no plain file. Windows opens as before;
    /// the caller has checked for a reparse point.
    /// </summary>
    public static FileStream? OpenNoFollow(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete, Options = FileOptions.SequentialScan,
            });
        }
        var flags = OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD()
            ? 0x0100 | 0x0004
            : (RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm ? 0x8000 : 0x20000) | 0x0800;
        var fd = open(path, flags | ReadOnly);
        if (fd < 0)
            return null;
        var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(fd, ownsHandle: true);
        try
        {
            var stream = new FileStream(handle, FileAccess.Read, bufferSize: 0);
            if (stream.CanSeek)
                return stream;
            stream.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            handle.Dispose();
            return null;
        }
    }

    private const int ReadOnly = 0;

#pragma warning disable SYSLIB1054 // The libc calls are plain; a source-generated stub adds nothing here.
    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolved);

    [DllImport("libc")]
    private static extern void free(IntPtr pointer);
#pragma warning restore SYSLIB1054
}
