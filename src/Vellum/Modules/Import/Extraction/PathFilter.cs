namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// Decides from the path alone whether a file is read. Content is never looked at here, so a
/// secret file is refused before anything can read it.
/// </summary>
internal static class PathFilter
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "vendor", "bin", "obj", "dist", ".git",
    };

    private static readonly HashSet<string> SecretDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ssh", ".aws", ".gnupg", ".azure", ".kube",
    };

    private static readonly string[] SecretSuffixes =
    [
        ".pem", ".key", ".pfx", ".p12", ".jks", ".keystore", ".kdbx", ".tfvars", ".tfvars.json", ".tfstate",
    ];

    private static readonly string[] SecretPrefixes =
    [
        ".env", "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", "secrets.",
    ];

    private static readonly HashSet<string> SecretNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".npmrc", ".pypirc", ".netrc", ".htpasswd", ".dockercfg", "kubeconfig",
        "credentials", "credentials.json", "credentials.yaml", "credentials.yml", "credentials.xml",
    };

    private static readonly HashSet<string> LockNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json", "packages.lock.json", "pnpm-lock.yaml", "npm-shrinkwrap.json", "go.sum", "bun.lockb",
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tif", ".tiff", ".psd", ".svgz",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".zip", ".gz", ".tgz", ".tar", ".bz2", ".xz", ".7z", ".rar", ".zst", ".jar", ".war", ".nupkg",
        ".dll", ".exe", ".so", ".dylib", ".a", ".o", ".lib", ".pdb", ".class", ".wasm", ".bin", ".dat",
        ".woff", ".woff2", ".ttf", ".otf", ".eot",
        ".mp3", ".mp4", ".mov", ".avi", ".wav", ".ogg", ".webm",
        ".db", ".sqlite", ".sqlite3", ".mdf", ".snk",
    };

    private static readonly HashSet<string> RelevantExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".go", ".json", ".yaml", ".yml",
        // A Structurizr DSL workspace.
        ".dsl",
    };

    /// <summary>
    /// The extensions of prose: text that no extractor reads, except an ADR (<see cref="IsRelevantPath"/>).
    /// A scan does not fetch it; the prose import reads one document when a person asks for it.
    /// </summary>
    public static bool IsProse(string name) =>
        Extension(name).ToLowerInvariant() is ".md" or ".markdown" or ".txt";

    /// <summary>
    /// <see cref="IsRelevant"/> for a whole path: a Markdown file is read only where ADRs stand
    /// (<see cref="AdrExtractor.Directory"/>), so other prose takes no part of the bounds.
    /// </summary>
    public static bool IsRelevantPath(string path)
    {
        var slash = path.LastIndexOf('/');
        var name = path[(slash + 1)..];
        return IsRelevant(name)
            || (slash > 0 && Extension(name).Equals(".md", StringComparison.OrdinalIgnoreCase) && AdrExtractor.Directory(path[..slash]) is not null);
    }

    /// <summary>The reason the path is not read, or null when it may be read.</summary>
    /// <param name="notePath">The path the note is about: the excluded directory for a directory exclusion.</param>
    public static string? SkipReason(string path, out string notePath)
    {
        notePath = path;
        if (path.Length is 0 or > 1024 || path[0] == '/' || path.Contains('\\') || path.Any(char.IsControl))
        {
            notePath = new string(path.Where(c => !char.IsControl(c)).Take(200).ToArray());
            return "invalid-path";
        }

        var segments = path.Split('/');
        if (segments.Any(s => s is "" or "." or ".."))
            return "invalid-path";

        var consumed = 0;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            consumed += segments[i].Length + 1;
            if (ExcludedDirectories.Contains(segments[i]))
            {
                notePath = path[..consumed];
                return "excluded-directory";
            }
            if (SecretDirectories.Contains(segments[i]))
            {
                notePath = path[..consumed];
                return "secret-file";
            }
        }

        var name = segments[^1];
        if (IsSecretName(name))
            return "secret-file";
        if (LockNames.Contains(name) || name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            return "lock-file";
        if (name.Contains(".min.", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
            return "minified";
        if (BinaryExtensions.Contains(Extension(name)))
            return "binary-extension";
        return null;
    }

    /// <summary>True when an extractor reads files with this name. Other files are ignored and not counted.</summary>
    public static bool IsRelevant(string name) =>
        RelevantExtensions.Contains(Extension(name)) || name == "go.mod" || IsDockerfile(name);

    public static bool IsDockerfile(string name) =>
        name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".Dockerfile", StringComparison.OrdinalIgnoreCase);

    public static string Extension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? "" : name[dot..];
    }

    private static bool IsSecretName(string name)
    {
        if (SecretNames.Contains(name))
            return true;
        foreach (var prefix in SecretPrefixes)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        foreach (var suffix in SecretSuffixes)
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        // A Kubernetes Secret manifest is usually named for what it holds.
        var extension = Extension(name);
        return (extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            && name.Contains("secret", StringComparison.OrdinalIgnoreCase);
    }
}
