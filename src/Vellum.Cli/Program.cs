using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Vellum.Modules.Import.Extraction;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Import.Sources;

namespace Vellum.Cli;

/// <summary>The exit codes of <c>vellum</c>. The reference page of the docs lists the same.</summary>
internal static class ExitCodes
{
    public const int Ok = 0;
    /// <summary>A directory could not be scanned, or another failure on this machine.</summary>
    public const int Failed = 1;
    public const int Usage = 2;
    /// <summary>The server refused the token: 401 or 403.</summary>
    public const int Auth = 3;
    /// <summary>The server refused the push: other rules, a limit, a bad request, a push in progress.</summary>
    public const int Refused = 4;
    /// <summary>The server failed or could not be reached.</summary>
    public const int Server = 5;
}

public static class Program
{
    public const string TokenVariable = "VELLUM_TOKEN";
    public const string UrlVariable = "VELLUM_URL";
    public const string ProjectVariable = "VELLUM_PROJECT";

    public static string Version { get; } =
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunAsync(args, Console.Out, Console.Error, Environment.GetEnvironmentVariable, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A failure the CLI does not know ends here with exit code 1, not with a stack trace.
            await Console.Error.WriteLineAsync($"vellum failed: {ex.GetType().Name}: {ex.Message}");
            return ExitCodes.Failed;
        }
    }

    private const string Usage = """
        Usage: vellum scan <dir>... [options]

        Scans each directory on this machine with the extractors of Vellum and pushes the
        manifest (path, size, git blob SHA), the facts and their citations to a project.
        No file content leaves the machine. Each directory is one agent-push Source.

        Options:
          --url <url>              The Vellum app, such as https://vellum.example.com ($VELLUM_URL).
          --project <id>           The project ID ($VELLUM_PROJECT).
          --name <name>            The Source name, for one directory. Default: the folder name.
          --name <dir>=<name>      The Source name of one of several directories.
          --include <path>         Scan only under this path. Repeat for more.
          --exclude <path>         Leave out this path. Repeat for more.
          --exclude-glob <glob>    Leave out the paths that match. Repeat for more.
          --max-files <n>          Read at most n files (at most the workspace's import.maxFiles).
          --max-file-bytes <n>     Read no file larger than n bytes.
          --max-total-bytes <n>    Read at most n bytes in all.
          --dry-run                Scan and report; push nothing.
          --payload <file>         Write the push bodies to this file as JSON, to see what is sent.
          --timeout <seconds>      How long one request may take. Default: 300.
          --allow-http             Send the token over http to a host that is not this machine.

        The agent token is read from $VELLUM_TOKEN only. It needs the push scope.
        Exit codes: 0 pushed, 1 failed here, 2 usage, 3 token refused, 4 push refused,
        5 server failed or unreachable.

        vellum --version         Prints the version.
        """;

    internal sealed record Options(
        List<string> Directories, Dictionary<string, string> Names, Uri? Url, Guid? Project,
        List<string> Include, List<string> Exclude, List<string> ExcludeGlobs,
        int? MaxFiles, long? MaxFileBytes, long? MaxTotalBytes, bool DryRun, string? Payload, TimeSpan Timeout, bool AllowHttp);

    /// <param name="environment">Reads an environment variable.</param>
    /// <param name="primaryHandler">The handler that sends the HTTP requests. Null for the network.</param>
    internal static async Task<int> RunAsync(
        string[] args, TextWriter output, TextWriter error, Func<string, string?> environment,
        Func<HttpMessageHandler>? primaryHandler, CancellationToken ct)
    {
        if (args is [] or ["help" or "--help" or "-h"])
        {
            await output.WriteLineAsync(Usage);
            return args.Length == 0 ? ExitCodes.Usage : ExitCodes.Ok;
        }
        if (args is ["--version" or "version"])
        {
            await output.WriteLineAsync($"vellum {Version}");
            return ExitCodes.Ok;
        }
        if (args[0] != "scan")
        {
            await error.WriteLineAsync($"Unknown command '{args[0]}'. Run: vellum scan <dir>... (vellum --help lists the options).");
            return ExitCodes.Usage;
        }

        if (Parse(args[1..], environment, out var problem) is not { } options)
        {
            await error.WriteLineAsync(problem);
            return ExitCodes.Usage;
        }

        var token = environment(TokenVariable);
        if (!options.DryRun)
        {
            var missing = new[]
            {
                options.Url is null ? $"--url (or ${UrlVariable})" : null,
                options.Project is null ? $"--project (or ${ProjectVariable})" : null,
                string.IsNullOrWhiteSpace(token) ? $"${TokenVariable}" : null,
            }.OfType<string>().ToList();
            if (missing.Count > 0)
            {
                await error.WriteLineAsync($"A push needs {string.Join(", ", missing)}. Add --dry-run to scan without pushing.");
                return ExitCodes.Usage;
            }
        }

        using var client = options.Url is not null && options.Project is not null && !string.IsNullOrWhiteSpace(token)
            ? new PushClient(options.Url, token.Trim(), options.Timeout, primaryHandler)
            : null;

        try
        {
            var limits = await LimitsAsync(options, client, output, ct);
            var payloads = new List<AgentPushRequest>();
            var exit = ExitCodes.Ok;
            foreach (var directory in options.Directories)
            {
                var name = options.Names[directory];
                AgentPushRequest request;
                try
                {
                    request = LocalScan.Run(directory, name, limits, options.Include, options.Exclude, warning => error.WriteLine($"{name}: {warning}"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await error.WriteLineAsync($"{name}: the directory could not be read: {ex.Message}");
                    exit = exit == ExitCodes.Ok ? ExitCodes.Failed : exit;
                    continue;
                }
                payloads.Add(request);
                var summary = string.Create(CultureInfo.InvariantCulture,
                    $"{name}: {request.Files.Count} files, {request.Facts.Count} facts, {request.Skipped.Count} skipped{(request.IsComplete ? "" : ", partial")}");

                if (options.DryRun || client is null)
                {
                    await output.WriteLineAsync($"{summary}. Not pushed (dry run). Snapshot {SnapshotManifest.Id(request.Files.Select(f => new ManifestEntry(f.Path, f.Size, f.BlobSha)))}");
                    continue;
                }
                try
                {
                    var pushed = await client.PushAsync(options.Project!.Value, request, ct);
                    await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                        $"{summary}. Pushed to Source '{pushed.SourceName}'{(pushed.SourceCreated ? " (new)" : "")}: Snapshot {pushed.SnapshotId}, {pushed.Proposals} proposals wait for review at {new Uri(options.Url!, $"/projects/{options.Project}")}"));
                }
                catch (PushRefused refused)
                {
                    await error.WriteLineAsync($"{name}: {refused.Message}");
                    exit = exit == ExitCodes.Ok ? refused.ExitCode : exit;
                }
            }

            if (options.Payload is not null)
            {
                await File.WriteAllTextAsync(options.Payload, JsonSerializer.Serialize(payloads, new JsonSerializerOptions(PushClient.Json) { WriteIndented = true }), ct);
                await output.WriteLineAsync($"Wrote the push bodies to {options.Payload}.");
            }
            return exit;
        }
        catch (PushRefused refused)
        {
            await error.WriteLineAsync(refused.Message);
            return refused.ExitCode;
        }
    }

    /// <summary>
    /// The scope limits: the workspace's import settings when the server can be asked, made
    /// smaller by the flags. The server's rules must be the rules of this CLI, or no file is read.
    /// </summary>
    private static async Task<ScanLimits> LimitsAsync(Options options, PushClient? client, TextWriter output, CancellationToken ct)
    {
        var defaults = ExtractionLimits.Default;
        int maxFiles = defaults.MaxFiles;
        long maxFileBytes = defaults.MaxFileBytes, maxTotalBytes = defaults.MaxTotalBytes;
        var globs = new List<string>();
        if (client is not null)
        {
            var settings = await client.SettingsAsync(options.Project!.Value, ct);
            var mine = EvidenceExtractor.Rules.Select(r => (r.Id, r.Version)).ToHashSet();
            var theirs = settings.Rules.Select(r => (r.Id, r.Version)).ToHashSet();
            if (!mine.SetEquals(theirs))
            {
                throw new PushRefused(ExitCodes.Refused, string.Create(CultureInfo.InvariantCulture,
                    $"This vellum {Version} extracts with other rules than the server ({theirs.Except(mine).Count()} of the server's rule versions are not here, {mine.Except(theirs).Count()} of these are not on the server). Use the vellum CLI of the server's version. Nothing was read."));
            }
            (maxFiles, maxFileBytes, maxTotalBytes) = (settings.MaxFiles, settings.MaxFileBytes, settings.MaxTotalBytes);
            globs.AddRange(settings.ExcludeGlobs);
        }
        else
        {
            await output.WriteLineAsync("Dry run without a server: the default import limits and no workspace exclude globs apply.");
        }
        globs.AddRange(options.ExcludeGlobs.Where(g => !globs.Contains(g, StringComparer.Ordinal)));
        return new ScanLimits(
            Math.Min(maxFiles, options.MaxFiles ?? int.MaxValue),
            Math.Min(maxFileBytes, options.MaxFileBytes ?? long.MaxValue),
            Math.Min(maxTotalBytes, options.MaxTotalBytes ?? long.MaxValue),
            globs);
    }

    internal static Options? Parse(string[] args, Func<string, string?> environment, out string problem)
    {
        problem = "";
        var directories = new List<string>();
        var names = new List<string>();
        var include = new List<string>();
        var exclude = new List<string>();
        var globs = new List<string>();
        string? url = environment(UrlVariable), project = environment(ProjectVariable), payload = null;
        int? maxFiles = null;
        long? maxFileBytes = null, maxTotalBytes = null;
        var timeout = TimeSpan.FromSeconds(300);
        bool dryRun = false, allowHttp = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                directories.Add(arg);
                continue;
            }
            if (arg is "--dry-run")
            {
                dryRun = true;
                continue;
            }
            if (arg is "--allow-http")
            {
                allowHttp = true;
                continue;
            }
            if (i + 1 >= args.Length)
            {
                problem = $"{arg} needs a value.";
                return null;
            }
            var value = args[++i];
            switch (arg)
            {
                case "--url": url = value; break;
                case "--project": project = value; break;
                case "--name": names.Add(value); break;
                case "--include": include.Add(value.Trim('/')); break;
                case "--exclude": exclude.Add(value.Trim('/')); break;
                case "--exclude-glob": globs.Add(value); break;
                case "--payload": payload = value; break;
                case "--max-files" when int.TryParse(value, CultureInfo.InvariantCulture, out var n) && n > 0: maxFiles = n; break;
                case "--max-file-bytes" when long.TryParse(value, CultureInfo.InvariantCulture, out var n) && n > 0: maxFileBytes = n; break;
                case "--max-total-bytes" when long.TryParse(value, CultureInfo.InvariantCulture, out var n) && n > 0: maxTotalBytes = n; break;
                case "--timeout" when int.TryParse(value, CultureInfo.InvariantCulture, out var n) && n is > 0 and <= 3600: timeout = TimeSpan.FromSeconds(n); break;
                case "--max-files" or "--max-file-bytes" or "--max-total-bytes" or "--timeout":
                    problem = $"{arg} takes a whole number above 0.";
                    return null;
                default:
                    problem = $"Unknown option '{arg}'. vellum --help lists the options.";
                    return null;
            }
        }

        if (directories.Count == 0)
        {
            problem = "Name at least one directory: vellum scan <dir>...";
            return null;
        }
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                problem = $"'{directory}' is not a directory.";
                return null;
            }
        }

        // The Source names: --name <dir>=<name>, or --name <name> for the only directory, else the folder name.
        var byDirectory = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var split = name.IndexOf('=', StringComparison.Ordinal);
            var target = split > 0 ? directories.FirstOrDefault(d => Trim(d) == Trim(name[..split])) : null;
            if (target is not null)
                byDirectory[target] = name[(split + 1)..];
            else if (directories.Count == 1)
                byDirectory[directories[0]] = name;
            else
            {
                problem = $"--name '{name}' names no directory of this run. With several directories, write --name <dir>=<name>.";
                return null;
            }
        }
        foreach (var directory in directories)
        {
            if (!byDirectory.ContainsKey(directory))
                byDirectory[directory] = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)));
            if (TextUtil.Slug(byDirectory[directory]).Length == 0)
            {
                problem = $"The Source name of '{directory}' holds no letter or digit. Give one with --name.";
                return null;
            }
        }
        if (directories.Select(d => TextUtil.Slug(byDirectory[d])).Distinct().Count() != directories.Count)
        {
            problem = "Two directories have the same Source name. Give each its own with --name <dir>=<name>.";
            return null;
        }

        Uri? baseUrl = null;
        if (!string.IsNullOrWhiteSpace(url))
        {
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out baseUrl) || baseUrl.Scheme is not ("https" or "http") || baseUrl.UserInfo.Length > 0)
            {
                problem = "--url is the http or https address of the Vellum app.";
                return null;
            }
            if (baseUrl.Scheme == "http" && !baseUrl.IsLoopback && !allowHttp)
            {
                problem = "--url uses http to another machine, which would send the token unencrypted. Use https, or add --allow-http.";
                return null;
            }
        }
        Guid? projectId = null;
        if (!string.IsNullOrWhiteSpace(project))
        {
            if (!Guid.TryParse(project.Trim(), out var parsed))
            {
                problem = "--project is the ID of the project, as its address in the app shows it.";
                return null;
            }
            projectId = parsed;
        }

        return new Options(directories, byDirectory, baseUrl, projectId, include, exclude, globs,
            maxFiles, maxFileBytes, maxTotalBytes, dryRun, payload, timeout, allowHttp);
    }

    private static string Trim(string directory) => directory.TrimEnd('/', Path.DirectorySeparatorChar);
}
