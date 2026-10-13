using System.Text;
using Vellum.Modules.Import.Extraction;
using Vellum.Modules.Promises;

namespace Vellum.Modules.Build;

/// <summary>The bounds of one export. A larger export is refused with 413; nothing is cut to fit.</summary>
/// <param name="MaxManifestBytes">The most bytes a manifest, or a request that holds one or its pinned state, may have. An export whose manifest would be larger is refused.</param>
/// <param name="MaxPinnedRuns">The most runs a pinned state names.</param>
public sealed record BuildLimits(
    long MaxBundleBytes = 32 * 1024 * 1024, long MaxFileBytes = 16 * 1024 * 1024, int MaxFiles = 5_000,
    long MaxManifestBytes = 16 * 1024 * 1024, int MaxPinnedRuns = 100_000)
{
    public static readonly BuildLimits Default = new();

    /// <summary>The most promises, scenarios and elements one brief request selects.</summary>
    public const int MaxBriefSelection = 200;
}

public sealed class BundleTooLargeException(string message) : Exception(message);

public sealed record GeneratorInfo(string Name, string Version);

public sealed record BundleFile(string Path, string Sha256, long Bytes);

/// <summary>What was generated, what the extractor read back from it, and the names that differ.</summary>
public sealed record SelfCheckCount(int Expected, int Parsed, IReadOnlyList<string> Missing, IReadOnlyList<string> Unexpected)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Agrees => Missing.Count == 0 && Unexpected.Count == 0;
}

/// <summary>
/// The generated OpenAPI and AsyncAPI documents, parsed back by the import extractor and
/// compared with the model by count and name. It shows that the documents say what the model
/// says. It shows nothing about an implementation.
/// </summary>
public sealed record SelfCheck(
    bool Passed,
    SelfCheckCount OpenApiOperations,
    SelfCheckCount AsyncApiChannels,
    SelfCheckCount AsyncApiMessages,
    SelfCheckCount AsyncApiSubscriptions,
    SelfCheckCount AsyncApiPublications,
    IReadOnlyList<string> Notes);

/// <summary>
/// <c>manifest.json</c>. It holds no time stamp: the same pinned state gives the same manifest
/// and so the same zip. <paramref name="BundleHash"/> is the SHA-256 of the lines
/// <c>&lt;path&gt; &lt;sha256&gt;\n</c> of <paramref name="Files"/> in path order.
/// </summary>
public sealed record BuildManifest(
    string Format,
    GeneratorInfo Generator,
    PinnedState Pinned,
    IReadOnlyList<BundleFile> Files,
    string BundleHash,
    bool BuildReady,
    IReadOnlyList<Blocker> Blockers,
    SelfCheck SelfCheck);

/// <summary>
/// The agent handoff bundle of one pinned state: <c>SDD.md</c>, the machine-readable files and
/// the manifest. Building it calls no AI and reads no clock.
/// </summary>
public sealed class Bundle
{
    /// <summary>Format 2 pins the version of every input, so a bundle of it can be generated again. A manifest of format 1 cannot.</summary>
    public const string Format = "vellum-bundle/2";
    public const string GeneratorVersion = "2";
    public const string ManifestPath = "manifest.json";

    /// <summary>The code of the blocker for a contract file that holds a number YAML cannot hold exactly.</summary>
    public const string YamlNumberBlocker = "yaml-unrepresentable-number";

    private Bundle(SortedDictionary<string, byte[]> files, BuildManifest manifest)
    {
        Files = files;
        Manifest = manifest;
        ManifestBytes = BuildText.CanonicalJson(manifest);
    }

    /// <summary>Every file but the manifest, by path in ordinal order.</summary>
    public IReadOnlyDictionary<string, byte[]> Files { get; }
    public BuildManifest Manifest { get; }
    public byte[] ManifestBytes { get; }

    public static GeneratorInfo Generator { get; } = new("vellum-build", GeneratorVersion);

    public static Bundle Build(BuildInput input, BuildLimits limits)
    {
        var view = new BuildView(input);
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        void Add(string path, byte[] bytes)
        {
            total += bytes.Length;
            if (bytes.Length > limits.MaxFileBytes || total > limits.MaxBundleBytes || files.Count >= limits.MaxFiles)
            {
                throw new BundleTooLargeException(
                    $"The export is larger than a bundle may be ({limits.MaxBundleBytes} bytes, {limits.MaxFileBytes} bytes per file, {limits.MaxFiles} files). Export a build brief for a part of the model.");
            }
            files[path] = bytes;
        }

        // A contract file that YAML cannot hold exactly is not written: a file with another
        // number in it would be a wrong contract. The blocker names the number.
        var notWritten = new SortedDictionary<string, YamlNumberException>(StringComparer.Ordinal);
        void AddYaml(string path, Func<byte[]> write)
        {
            try
            {
                Add(path, write());
            }
            catch (YamlNumberException unrepresentable)
            {
                notWritten[path] = unrepresentable;
            }
        }

        var routes = ContractDocuments.Routes(view);
        foreach (var (owner, routed) in routes.OrderBy(r => r.Key))
            AddYaml(ContractDocuments.OpenApiPath(owner), () => ContractDocuments.OpenApi(view, owner, routed));
        AddYaml(ContractDocuments.AsyncApiPath, () => ContractDocuments.AsyncApi(view));

        var selfCheck = Check(view, routes, files);
        var blockers = Blockers.Of(view, routes).ToList();
        foreach (var (path, unrepresentable) in notWritten)
        {
            blockers.Add(new Blocker(YamlNumberBlocker, path,
                $"The file {path} is not written: the number {unrepresentable.Value} at {unrepresentable.Path} is not finite, is an integer outside 64 bits, or has more digits than a YAML reader keeps. Change the stored schema."));
        }
        if (!selfCheck.Passed)
        {
            blockers.Add(new Blocker("self-check-failed", ContractDocuments.AsyncApiPath,
                "The generated OpenAPI or AsyncAPI documents do not read back as the model. See selfCheck in the manifest."));
        }

        Add("architecture.json", ContractDocuments.Architecture(view, routes));
        foreach (var scenario in view.Scenarios)
            Add(PromiseDocuments.ScenarioPath(scenario), PromiseDocuments.Scenario(scenario, view.Playbacks[scenario.Id.Value]));
        foreach (var promise in view.Promises)
            Add(PromiseDocuments.Path(promise), PromiseDocuments.Promise(promise));
        Add(SddDocument.Path, SddDocument.Write(view, routes, Slices.Of(view), blockers, notWritten.Keys.ToHashSet(StringComparer.Ordinal)));

        var listed = files.Select(f => new BundleFile(f.Key, BuildText.Sha256(f.Value), f.Value.Length)).ToList();
        var manifest = new BuildManifest(
            Format, Generator,
            input.Pinned,
            listed, BundleHash(listed.Select(f => (f.Path, f.Sha256))), blockers.Count == 0, blockers, selfCheck);
        var bundle = new Bundle(files, manifest);
        // The manifest is sent back to verify and to export again, so it has the bound those requests have.
        if (bundle.ManifestBytes.Length > limits.MaxManifestBytes || manifest.Pinned.Runs.Count > limits.MaxPinnedRuns)
        {
            throw new BundleTooLargeException(
                $"The manifest of the export is larger than a manifest may be ({limits.MaxManifestBytes} bytes, {limits.MaxPinnedRuns} pinned runs). Export a build brief for a part of the model.");
        }
        return bundle;
    }

    /// <summary>SHA-256 over the sorted list of path and hash.</summary>
    public static string BundleHash(IEnumerable<(string Path, string Sha256)> files) => BuildText.Sha256(Encoding.UTF8.GetBytes(
        string.Concat(files.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => $"{f.Path} {f.Sha256}\n"))));

    /// <summary>
    /// A zip that is the same bytes for the same files on every operating system: see
    /// <see cref="ZipWriter"/>, which writes every header field itself.
    /// </summary>
    public byte[] Zip() => ZipWriter.Write(Files.Append(new(ManifestPath, ManifestBytes)));

    private static SelfCheck Check(
        BuildView view, IReadOnlyDictionary<Guid, IReadOnlyList<Route>> routes, IReadOnlyDictionary<string, byte[]> files)
    {
        var notes = new List<string>();
        ExtractionResult Read(string path)
        {
            // A file that was not written cannot read back. The self-check then does not pass,
            // and the blocker of the file says why it is missing.
            if (!files.ContainsKey(path))
            {
                notes.Add($"{path}: the file was not written, so it was not read back.");
                return new ExtractionResult("vellum-export", "pinned", [], [], true);
            }

            // One document per call: the extractor never merges two generated files.
            var result = EvidenceExtractor.Extract(new ExtractionInput("vellum-export", "pinned", [new SourceFile(path, Encoding.UTF8.GetString(files[path]))]));
            if (!result.IsComplete)
                notes.Add($"{path}: the extractor did not read the whole document.");
            notes.AddRange(result.Notes.Select(n => $"{path}:{n.Line}: {n.Kind} {n.Reason}"));
            return result;
        }

        var expectedOperations = new List<string>();
        var parsedOperations = new List<string>();
        foreach (var (owner, routed) in routes.OrderBy(r => r.Key))
        {
            var path = ContractDocuments.OpenApiPath(owner);
            expectedOperations.AddRange(routed.Select(r => $"{path}: {r.Method} {r.Path}"));
            parsedOperations.AddRange(Read(path).Facts.Where(f => f.Kind == FactKind.Operation).Select(f => $"{path}: {f.Name}"));
        }

        var asyncApi = Read(ContractDocuments.AsyncApiPath);
        var publications = view.Publications()
            .Where(p => view.Model.Channels.ContainsKey(p.Channel) && view.Model.Messages.ContainsKey(p.Message))
            .Select(p => ContractDocuments.SendKey(p.Publisher, p.Channel));
        var subscriptions = view.Model.Subscriptions.Values
            .Where(s => view.Model.Channels.ContainsKey(s.ChannelId)).Select(s => ContractDocuments.ReceiveKey(s.Id.Value));

        var check = new SelfCheck(
            false,
            Compare(expectedOperations, parsedOperations),
            Compare(view.Channels.Select(c => c.Name),
                asyncApi.Facts.Where(f => f.Kind == FactKind.Channel).Select(f => f.Attributes.GetValueOrDefault("address") ?? f.Name)),
            Compare(view.Messages.Select(m => m.Name), asyncApi.Facts.Where(f => f.Kind == FactKind.Message).Select(f => f.Name)),
            Compare(subscriptions, asyncApi.Facts.Where(f => f.Kind == FactKind.Subscription).Select(f => f.Name)),
            Compare(publications, asyncApi.Facts.Where(f => f.Kind == FactKind.Publication).Select(f => f.Name)),
            [.. notes.Order(StringComparer.Ordinal)]);
        return check with
        {
            Passed = notes.Count == 0 && check.OpenApiOperations.Agrees && check.AsyncApiChannels.Agrees && check.AsyncApiMessages.Agrees
                     && check.AsyncApiSubscriptions.Agrees && check.AsyncApiPublications.Agrees,
        };
    }

    /// <summary>Compares two sets of names. Two model items with one name are one name on both sides.</summary>
    private static SelfCheckCount Compare(IEnumerable<string> expected, IEnumerable<string> parsed)
    {
        var want = expected.ToHashSet(StringComparer.Ordinal);
        var have = parsed.ToHashSet(StringComparer.Ordinal);
        return new SelfCheckCount(want.Count, have.Count,
            [.. want.Except(have).Order(StringComparer.Ordinal)], [.. have.Except(want).Order(StringComparer.Ordinal)]);
    }
}
