using System.Globalization;
using System.IO.Compression;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vellum.Modules.Drafts;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Modules.Modelling;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Promises;
using Vellum.Modules.Workspaces;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Build;

/// <param name="RevisionId">A named model revision. Without it the export pins the branch at its current version and reports that version.</param>
/// <param name="BranchId">A draft or variant branch. Default: the main model.</param>
/// <param name="Format"><c>bundle</c> (a zip of every file) or <c>sdd</c> (<c>SDD.md</c> alone).</param>
/// <param name="Pinned">
/// The <c>pinned</c> state of a manifest. With it the export is made again from exactly the
/// versions it names, and <paramref name="RevisionId"/> and <paramref name="BranchId"/> must be absent.
/// </param>
public sealed record ExportRequest(Guid? RevisionId = null, Guid? BranchId = null, string Format = "bundle", PinnedState? Pinned = null);

public sealed record BriefRequest(
    IReadOnlyList<Guid>? PromiseIds = null, IReadOnlyList<Guid>? ScenarioIds = null, IReadOnlyList<Guid>? ElementIds = null,
    Guid? RevisionId = null, Guid? BranchId = null);

/// <summary>A manifest to check against what its pinned state generates.</summary>
public sealed record VerifyRequest(BuildManifest Manifest);

/// <param name="Status">
/// <c>match</c>: the file is what the pinned state generates. <c>differs</c>: both have the
/// file and the bytes differ. <c>missing</c>: the pinned state generates a file that was not
/// given. <c>unknown-file</c>: a file was given that the pinned state does not generate.
/// </param>
public sealed record VerifiedFile(string Path, string Status, string? ExpectedSha256, string? ActualSha256);

/// <summary>An input that is not at its pinned version any more.</summary>
/// <param name="Kind"><c>model</c>, <c>project-name</c>, <c>scenario</c>, <c>schema</c>, <c>promise</c>, <c>decision</c>, <c>run</c> or <c>source</c>.</param>
/// <param name="Pinned">What the manifest pins, or null for an input that did not exist then.</param>
/// <param name="Current">What it is now, or null for an input that no longer exists.</param>
public sealed record MovedInput(string Kind, string Id, string? Pinned, string? Current);

/// <summary>
/// The answer to two separate questions about a bundle or a manifest.
/// <para>
/// Is it what its pinned versions generate? <paramref name="MatchesPinned"/> is true only when
/// every file and the manifest are byte for byte what Vellum generates from exactly the
/// versions the manifest pins. A false answer means the bundle or the manifest was changed
/// after the export: nothing that happened in the project later can cause it.
/// </para>
/// <para>
/// Are those versions still the newest? <paramref name="Current"/> is false when an input
/// moved after the export; <paramref name="Moved"/> lists each. That is drift, not tampering.
/// </para>
/// </summary>
/// <param name="Identical">True only when <paramref name="MatchesPinned"/> is true and, for a zip, the archive is not <c>differs</c>.</param>
/// <param name="Manifest"><c>match</c> or <c>differs</c>: the supplied manifest against the regenerated one, byte for byte.</param>
/// <param name="ManifestDifferences">The top-level fields of the manifest that differ.</param>
/// <param name="Archive">For a zip: <c>match</c> when its bytes are the bytes Vellum writes for these files, else <c>differs</c>. Null for a manifest alone.</param>
public sealed record VerifyResponse(
    PinnedState Pinned, bool Identical, bool MatchesPinned,
    string Manifest, IReadOnlyList<string> ManifestDifferences, string? Archive,
    bool Current, IReadOnlyList<MovedInput> Moved,
    string ExpectedBundleHash, string ActualBundleHash,
    IReadOnlyList<VerifiedFile> Files, SelfCheck SelfCheck);

/// <summary>
/// Build mode (SDD §3): the standalone SDD and the agent handoff bundle of a pinned state,
/// the check that a bundle is what its pinned state generates, and the change-scoped build
/// brief. Every endpoint reads and none writes, so a Viewer may call them all.
/// </summary>
public static partial class BuildModule
{
    public static IServiceCollection AddBuildModule(this IServiceCollection services)
    {
        services.TryAddSingleton(BuildLimits.Default);
        return services;
    }

    public static WebApplication MapBuildEndpoints(this WebApplication app)
    {
        var build = app.MapGroup("/api/projects/{projectId}/build").RequireAuthorization().WithTags("Build");

        // An export request names a revision or holds the pinned state of a manifest: a document, not a bundle.
        build.MapPost("/export", Export).AllowAgent(AgentScope.Export)
            .Produces<string>(contentType: "text/markdown")
            .Produces(StatusCodes.Status200OK, contentType: "application/zip")
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ErrorResponse>(StatusCodes.Status413PayloadTooLarge)
            .WithBodyLimit(BuildLimits.Default.MaxManifestBytes);
        build.MapGet("/manifest", Manifest).AllowAgent(AgentScope.Export).Produces<BuildManifest>();
        build.MapPost("/brief", MakeBrief).AllowAgent(AgentScope.Export).Produces<BriefResult>()
            .WithBodyLimit(RequestBodyLimit.Default);

        // A bundle is uploaded whole, so this endpoint takes a body as large as a bundle may be.
        // A manifest alone is held to the manifest limit by the handler.
        build.MapPost("/verify", Verify).AllowAgent(AgentScope.Export)
            .Accepts<VerifyRequest>("application/json", "application/zip")
            .Produces<VerifyResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ErrorResponse>(StatusCodes.Status413PayloadTooLarge)
            .WithBodyLimit(BuildLimits.Default.MaxBundleBytes + RequestBodyLimit.Document);

        return app;
    }

    private static async Task<IResult> Export(
        Guid projectId, ExportRequest request, HttpContext http,
        WorkspaceAuthorizationService auth, BuildLimits limits, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, UserId(http.User), WorkspaceRole.Viewer, ct);
        if (request.Format is not ("bundle" or "sdd"))
            return Invalid("format", "The format is bundle or sdd");

        Bundle? bundle;
        IResult? refusal;
        if (request.Pinned is { } pin)
        {
            if (request.RevisionId is not null || request.BranchId is not null)
                return Invalid("pinned", "Give a pinned state, or a revision or a branch, not both");
            if (CheckPinned(pin, projectId, limits, "pinned") is { } invalid)
                return invalid;
            (bundle, refusal) = await BuildPinnedAsync(http.RequestServices, projectId, pin, limits, "pinned", ct);
        }
        else
            (bundle, refusal) = await BuildAsync(http.RequestServices, projectId, request.RevisionId, request.BranchId, limits, ct);
        if (bundle is null)
            return refusal!;

        // The headers name the pinned state, so a caller can ask for the same export again.
        http.Response.Headers["X-Vellum-Stream-Version"] = bundle.Manifest.Pinned.StreamVersion.ToString(CultureInfo.InvariantCulture);
        http.Response.Headers["X-Vellum-Bundle-Hash"] = bundle.Manifest.BundleHash;
        http.Response.Headers["X-Vellum-Build-Ready"] = bundle.Manifest.BuildReady ? "true" : "false";
        if (request.Format == "sdd")
        {
            return bundle.Files.TryGetValue(SddDocument.Path, out var sdd)
                ? Results.Bytes(sdd, "text/markdown; charset=utf-8", "SDD.md")
                : Results.Conflict(new ErrorResponse("file_not_generated", "SDD.md was not generated. See the blockers of the manifest."));
        }
        return Results.Bytes(bundle.Zip(), "application/zip", $"vellum-bundle-{bundle.Manifest.BundleHash[..12]}.zip");
    }

    private static async Task<IResult> Manifest(
        Guid projectId, Guid? revisionId, Guid? branchId, ClaimsPrincipal user, HttpContext http,
        WorkspaceAuthorizationService auth, BuildLimits limits, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, UserId(user), WorkspaceRole.Viewer, ct);
        var (bundle, refusal) = await BuildAsync(http.RequestServices, projectId, revisionId, branchId, limits, ct);
        return bundle is null ? refusal! : Results.Ok(bundle.Manifest);
    }

    private static async Task<IResult> MakeBrief(
        Guid projectId, BriefRequest request, HttpContext http,
        WorkspaceAuthorizationService auth, BuildLimits limits, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, UserId(http.User), WorkspaceRole.Viewer, ct);
        var selection = new BriefSelection(
            [.. (request.PromiseIds ?? []).Distinct()], [.. (request.ScenarioIds ?? []).Distinct()], [.. (request.ElementIds ?? []).Distinct()]);
        if (selection.PromiseIds.Count + selection.ScenarioIds.Count + selection.ElementIds.Count == 0)
            return Invalid("promiseIds", "Select at least one promise, scenario or element");
        if (selection.PromiseIds.Count + selection.ScenarioIds.Count + selection.ElementIds.Count > BuildLimits.MaxBriefSelection)
            return Invalid("promiseIds", $"Select at most {BuildLimits.MaxBriefSelection} things");

        var (pinned, refusal) = await PinnedModel.ResolveAsync(http.RequestServices, projectId, request.RevisionId, request.BranchId, ct);
        if (pinned is null)
            return refusal!;
        BuildInput input;
        try
        {
            input = await BuildInput.LoadAsync(http.RequestServices, projectId, pinned, limits, ct);
        }
        catch (BundleTooLargeException tooLarge)
        {
            return TooLarge(tooLarge);
        }

        if (selection.PromiseIds.FirstOrDefault(id => input.Promises.All(p => p.Id != id)) is var promise && promise != Guid.Empty)
            return Invalid("promiseIds", $"No promise with ID {promise} exists in the project");
        if (selection.ScenarioIds.FirstOrDefault(id => input.Scenarios.All(s => s.Id.Value != id)) is var scenario && scenario != Guid.Empty)
            return Invalid("scenarioIds", $"No scenario with ID {scenario} exists in the project");
        if (selection.ElementIds.FirstOrDefault(id => !input.Model.Elements.ContainsKey(id)) is var element && element != Guid.Empty)
            return Invalid("elementIds", $"No element with ID {element} exists in the pinned model");

        try
        {
            return Results.Ok(Brief.Build(input, selection, limits));
        }
        catch (BundleTooLargeException tooLarge)
        {
            return TooLarge(tooLarge);
        }
    }

    /// <summary>What a caller gave to verify: the manifest as it was written, and the SHA-256 of each file.</summary>
    /// <param name="ManifestBytes">The bytes of <c>manifest.json</c> of a zip. For a manifest alone, its JSON in canonical form.</param>
    /// <param name="Zip">The whole archive, for a zip.</param>
    private sealed record Supplied(BuildManifest Manifest, byte[] ManifestBytes, Dictionary<string, string> Files, byte[]? Zip);

    /// <summary>
    /// Generates the bundle of the manifest's pinned state again, from exactly the versions
    /// it pins, and compares it with what was given: each file, and the manifest byte for
    /// byte. The body is a manifest as JSON (<c>{ "manifest": … }</c>) or a bundle zip; for a
    /// zip the bytes of each entry are hashed, so an edited file shows even when the manifest
    /// in the zip was edited to agree with it, and an edited manifest shows by itself. A zip
    /// that is not one a bundle can be (an entry name twice, an entry the manifest does not
    /// list, a name that is not in its plain form, more entries than a bundle holds) is
    /// refused as <c>invalid_bundle</c>: it is never reported as identical.
    /// </summary>
    private static async Task<IResult> Verify(
        Guid projectId, HttpContext http, WorkspaceAuthorizationService auth, BuildLimits limits, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, UserId(http.User), WorkspaceRole.Viewer, ct);

        var isZip = http.Request.ContentType?.StartsWith("application/zip", StringComparison.OrdinalIgnoreCase) == true;
        using var body = new MemoryStream();
        await http.Request.Body.CopyToAsync(body, ct);
        if (!isZip && body.Length > limits.MaxManifestBytes)
        {
            return Results.Json(
                new ErrorResponse("payload_too_large", $"A manifest holds at most {limits.MaxManifestBytes} bytes."),
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        Supplied supplied;
        var anomalies = new List<FieldError>();
        try
        {
            if ((isZip ? ReadZip(body, limits, anomalies) : ReadManifest(body, limits, anomalies)) is not { } read)
            {
                return Results.BadRequest(new ErrorResponse("invalid_bundle",
                    anomalies.Count > 0 ? "The body is not a bundle that Vellum writes" : "The body holds no manifest",
                    Errors: anomalies.Count > 0 ? anomalies : null));
            }
            supplied = read;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            return Results.BadRequest(new ErrorResponse("invalid_bundle", "The body is not a manifest or a bundle zip", ex.Message));
        }
        if (anomalies.Count > 0)
            return Results.BadRequest(new ErrorResponse("invalid_bundle", "The body is not a bundle that Vellum writes", Errors: anomalies));

        if (supplied.Manifest.Pinned is not { } pin)
            return Results.BadRequest(new ErrorResponse("invalid_bundle", "The manifest holds no pinned state"));
        if (CheckPinned(pin, projectId, limits, "manifest.pinned") is { } invalid)
            return invalid;

        var services = http.RequestServices;
        var (bundle, refusal) = await BuildPinnedAsync(services, projectId, pin, limits, "manifest.pinned", ct);
        if (bundle is null)
            return refusal!;

        var expected = bundle.Manifest.Files.ToDictionary(f => f.Path, f => f.Sha256, StringComparer.Ordinal);
        var files = expected.Keys.Union(supplied.Files.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(path =>
            {
                var want = expected.GetValueOrDefault(path);
                var have = supplied.Files.GetValueOrDefault(path);
                var status = want is null ? "unknown-file" : have is null ? "missing" : want == have ? "match" : "differs";
                return new VerifiedFile(path, status, want, have);
            })
            .ToList();

        var manifestMatches = supplied.ManifestBytes.AsSpan().SequenceEqual(bundle.ManifestBytes);
        var differences = manifestMatches ? [] : ManifestDifferences(supplied.ManifestBytes, bundle.ManifestBytes);
        var matchesPinned = manifestMatches && files.All(f => f.Status == "match");
        var archive = supplied.Zip is null ? null : supplied.Zip.AsSpan().SequenceEqual(bundle.Zip()) ? "match" : "differs";

        IReadOnlyList<MovedInput> moved;
        try
        {
            moved = await MovedAsync(services, projectId, pin, limits, ct);
        }
        catch (BundleTooLargeException tooLarge)
        {
            return TooLarge(tooLarge);
        }

        return Results.Ok(new VerifyResponse(
            bundle.Manifest.Pinned, matchesPinned && archive != "differs", matchesPinned,
            manifestMatches ? "match" : "differs", differences, archive,
            moved.Count == 0, moved,
            bundle.Manifest.BundleHash, Bundle.BundleHash(supplied.Files.Select(f => (f.Key, f.Value))),
            files, bundle.Manifest.SelfCheck));
    }

    /// <summary>The top-level fields whose values differ between two manifests, by name. When only the bytes differ, one entry says so.</summary>
    private static List<string> ManifestDifferences(byte[] supplied, byte[] regenerated)
    {
        using var given = JsonDocument.Parse(supplied);
        using var wanted = JsonDocument.Parse(regenerated);
        if (given.RootElement.ValueKind != JsonValueKind.Object)
            return ["(the manifest is not a JSON object)"];

        static Dictionary<string, string> Fields(JsonElement root) =>
            root.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => Convert.ToHexString(BuildText.CanonicalJson(g.Last().Value)), StringComparer.Ordinal);
        var (have, want) = (Fields(given.RootElement), Fields(wanted.RootElement));
        var differing = have.Keys.Union(want.Keys, StringComparer.Ordinal)
            .Where(name => !have.TryGetValue(name, out var a) || !want.TryGetValue(name, out var b) || a != b)
            .Order(StringComparer.Ordinal)
            .ToList();
        return differing.Count > 0 ? differing : ["(the values are equal; the bytes are not those Vellum writes)"];
    }

    [GeneratedRegex(@"\A[A-Za-z0-9._-]+(?:/[A-Za-z0-9._-]+)*\z")]
    private static partial Regex PlainPath();

    /// <summary>
    /// True for a path a bundle can hold: parts of letters, digits, <c>.</c>, <c>_</c> and
    /// <c>-</c>, joined by one <c>/</c>, with no part that is <c>.</c> or <c>..</c>. Every
    /// other spelling of a path (a backslash, a leading or doubled slash, a directory entry,
    /// another Unicode form) can name the same file twice to one reader and not to another.
    /// </summary>
    internal static bool IsPlainPath(string? path) =>
        path is { Length: > 0 and <= 512 } && PlainPath().IsMatch(path) && path.Split('/').All(part => part is not ("." or ".."));

    /// <summary>A manifest alone. Its files are the hashes it lists; nothing shows whether the files themselves were changed.</summary>
    private static Supplied? ReadManifest(MemoryStream body, BuildLimits limits, List<FieldError> anomalies)
    {
        using var document = JsonDocument.Parse(body.GetBuffer().AsMemory(0, (int)body.Length));
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("manifest", out var element) || element.ValueKind != JsonValueKind.Object)
            return null;
        if (element.Deserialize<BuildManifest>(BuildText.Json) is not { } manifest)
            return null;

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (manifest.Files is { Count: var count } listed)
        {
            if (count > limits.MaxFiles)
                anomalies.Add(new FieldError("manifest.files", $"The manifest lists {count} files; a bundle holds at most {limits.MaxFiles}."));
            foreach (var file in listed.Take(limits.MaxFiles))
            {
                if (file?.Path is null || file.Sha256 is null || !IsPlainPath(file.Path))
                    anomalies.Add(new FieldError(file?.Path ?? "manifest.files", "The manifest lists a file with no hash, or with a path that is not in its plain form."));
                else if (!files.TryAdd(file.Path, file.Sha256))
                    anomalies.Add(new FieldError(file.Path, "The manifest lists this file more than once."));
            }
        }
        return new Supplied(manifest, BuildText.CanonicalJson(element), files, null);
    }

    /// <summary>
    /// The manifest of a bundle zip and the SHA-256 of each other entry, read inside the
    /// bundle bounds. Everything that makes the zip another thing than a bundle Vellum writes
    /// goes to <paramref name="anomalies"/>.
    /// </summary>
    private static Supplied? ReadZip(MemoryStream body, BuildLimits limits, List<FieldError> anomalies)
    {
        body.Position = 0;
        using var zip = new ZipArchive(body, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count > limits.MaxFiles + 1)
        {
            anomalies.Add(new FieldError("", $"The zip holds {zip.Entries.Count} entries; a bundle holds at most {limits.MaxFiles + 1}."));
            return null;
        }

        BuildManifest? manifest = null;
        byte[]? manifestBytes = null;
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (!IsPlainPath(name))
            {
                anomalies.Add(new FieldError(name, "The entry name is not in its plain form: parts of letters, digits, '.', '_' and '-' joined by '/'."));
                continue;
            }
            if (!seen.Add(name))
            {
                anomalies.Add(new FieldError(name, "The zip holds this entry more than once. Readers disagree about which one is the file."));
                continue;
            }

            // The declared length is not trusted: the read stops one byte past the bound.
            using var stream = entry.Open();
            using var content = new MemoryStream();
            var buffer = new byte[81920];
            for (int read; (read = stream.Read(buffer, 0, buffer.Length)) > 0;)
            {
                total += read;
                if (content.Length + read > limits.MaxFileBytes || total > limits.MaxBundleBytes)
                    throw new InvalidDataException("The zip is larger than a bundle may be.");
                content.Write(buffer, 0, read);
            }

            if (name == Bundle.ManifestPath)
            {
                manifestBytes = content.ToArray();
                manifest = JsonSerializer.Deserialize<BuildManifest>(manifestBytes, BuildText.Json);
            }
            else
                hashes[name] = BuildText.Sha256(content.GetBuffer().AsSpan(0, (int)content.Length));
        }
        if (manifest is null || manifestBytes is null)
        {
            anomalies.Add(new FieldError(Bundle.ManifestPath, "The zip holds no manifest.json."));
            return null;
        }

        // The manifest of the zip and its entries must name the same files.
        var listed = (manifest.Files ?? []).Where(f => f?.Path is not null).Select(f => f.Path).ToList();
        foreach (var path in listed.GroupBy(p => p, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key))
            anomalies.Add(new FieldError(path, "The manifest lists this file more than once."));
        var named = listed.ToHashSet(StringComparer.Ordinal);
        foreach (var path in hashes.Keys.Where(path => !named.Contains(path)).Order(StringComparer.Ordinal))
            anomalies.Add(new FieldError(path, "The zip holds this entry and its manifest does not list it."));
        foreach (var path in named.Where(path => !hashes.ContainsKey(path)).Order(StringComparer.Ordinal))
            anomalies.Add(new FieldError(path, "The manifest lists this file and the zip does not hold it."));

        return new Supplied(manifest, manifestBytes, hashes, body.ToArray());
    }

    /// <summary>Null when a pinned state names this project and is one a manifest of this format holds, inside the bounds of a bundle.</summary>
    private static IResult? CheckPinned(PinnedState pin, Guid projectId, BuildLimits limits, string field)
    {
        if (pin.ProjectId != projectId)
            return Invalid($"{field}.projectId", "The pinned state is of another project");
        if (pin.ProjectName is null || pin.Scenarios is null || pin.Schemas is null || pin.Promises is null || pin.Runs is null || pin.Sources is null)
        {
            return Results.BadRequest(new ErrorResponse("invalid_bundle",
                $"The pinned state does not name the version of every input. Only a manifest of the format {Bundle.FormatWithoutReview} or {Bundle.Format} can be generated again."));
        }
        // A state of the format before the review names no decision stream. That is not an error.
        var decisions = pin.Decisions ?? [];
        if (pin.Scenarios.Count + pin.Schemas.Count + pin.Promises.Count + decisions.Count > limits.MaxFiles
            || pin.Runs.Count > limits.MaxPinnedRuns || pin.Sources.Count > limits.MaxFiles)
            return Invalid(field, "The pinned state names more inputs than a bundle holds");
        if (pin.Scenarios.Concat(pin.Schemas).Concat(pin.Promises).Concat(decisions).Any(p => p is null || p.Version < 1)
            || pin.Runs.Any(r => r is null) || pin.Sources.Any(s => s?.Repository is null))
            return Invalid(field, "Every pinned input has an ID and a version of at least 1");
        foreach (var (name, pins) in new[] { ("scenarios", pin.Scenarios), ("schemas", pin.Schemas), ("promises", pin.Promises), ("decisions", decisions) })
        {
            if (pins.GroupBy(p => p.Id).Any(g => g.Count() > 1))
                return Invalid($"{field}.{name}", "An input is pinned once");
        }
        return null;
    }

    /// <summary>The model of a pinned state: its named revision, or its branch stream folded to the pinned version.</summary>
    private static async Task<(PinnedModel? Model, IResult? Refusal)> ResolvePinnedAsync(
        IServiceProvider services, Guid projectId, PinnedState pin, string field, CancellationToken ct)
    {
        if (pin.RevisionId is { } revisionId)
        {
            var (revision, refusal) = await PinnedModel.ResolveAsync(services, projectId, revisionId, null, ct);
            if (revision is null)
                return (null, refusal);
            return revision.Pin.StreamId == pin.Branch && revision.Pin.Version == pin.StreamVersion
                ? (revision, null)
                : (null, Invalid($"{field}.streamVersion", "The named revision is of another branch or version than the pinned state says"));
        }

        if (!await IsBranchAsync(services, projectId, pin.Branch, ct))
            return (null, Invalid($"{field}.branch", "The project has no such model branch"));
        var state = await PinnedModel.AtVersionAsync(services.GetRequiredService<IEventStore>(), pin.Branch, pin.StreamVersion, ct);
        return state is null
            ? (null, Invalid($"{field}.streamVersion", "The branch has no such version"))
            : (new PinnedModel(new ModelPin(pin.Branch, pin.StreamVersion, null, null), state), null);
    }

    private static async Task<bool> IsBranchAsync(IServiceProvider services, Guid projectId, Guid streamId, CancellationToken ct) =>
        await services.GetRequiredService<WorkspacesDbContext>().Projects.AsNoTracking().AnyAsync(p => p.Id == projectId && p.StreamId == streamId, ct)
        || await services.GetRequiredService<DraftsDbContext>().Drafts.AsNoTracking().AnyAsync(d => d.ProjectId == projectId && d.StreamId == streamId, ct)
        || await services.GetRequiredService<ModellingDbContext>().Variants.AsNoTracking().AnyAsync(v => v.ProjectId == projectId && v.StreamId == streamId, ct);

    /// <summary>The bundle of a pinned state, made from exactly the versions it names.</summary>
    private static async Task<(Bundle? Bundle, IResult? Refusal)> BuildPinnedAsync(
        IServiceProvider services, Guid projectId, PinnedState pin, BuildLimits limits, string field, CancellationToken ct)
    {
        var (pinned, refusal) = await ResolvePinnedAsync(services, projectId, pin, field, ct);
        if (pinned is null)
            return (null, refusal);
        try
        {
            return (Bundle.Build(await BuildInput.LoadAsync(services, projectId, pinned, limits, ct, pin), limits), null);
        }
        catch (BundleTooLargeException tooLarge)
        {
            return (null, TooLarge(tooLarge));
        }
        catch (PinnedInputUnavailableException gone)
        {
            return (null, Results.Conflict(new ErrorResponse("pinned_input_unavailable",
                "A pinned input can no longer be read at its version, so the bundle cannot be generated again",
                Errors: gone.Inputs.Select(input => new FieldError(field, input)).ToList())));
        }
    }

    /// <summary>
    /// Every input that is not at its pinned version now: drift after the export. The model of
    /// a named revision does not move; a branch does. A run that was recorded later is drift too.
    /// </summary>
    private static async Task<IReadOnlyList<MovedInput>> MovedAsync(
        IServiceProvider services, Guid projectId, PinnedState pin, BuildLimits limits, CancellationToken ct)
    {
        var moved = new List<MovedInput>();
        static string V(long version) => version.ToString(CultureInfo.InvariantCulture);

        var (model, version) = await services.GetRequiredService<IEventStore>().Load<ModelState>(StreamId.From(pin.Branch), ct);
        if (pin.RevisionId is null && version != pin.StreamVersion)
            moved.Add(new MovedInput("model", pin.Branch.ToString(), V(pin.StreamVersion), V(version)));

        // The other inputs do not depend on the model, so any model serves to read their current versions.
        var now = (await BuildInput.LoadAsync(services, projectId, new PinnedModel(new ModelPin(pin.Branch, version, null, null), model), limits, ct)).Pinned;
        if (now.ProjectName != pin.ProjectName)
            moved.Add(new MovedInput("project-name", projectId.ToString(), pin.ProjectName, now.ProjectName));

        void Streams(string kind, IReadOnlyList<PinnedStream> pinned, IReadOnlyList<PinnedStream> current)
        {
            var then = pinned.ToDictionary(p => p.Id, p => p.Version);
            var today = current.ToDictionary(p => p.Id, p => p.Version);
            foreach (var id in then.Keys.Union(today.Keys).Order())
            {
                var (was, had) = (then.TryGetValue(id, out var a) ? a : (long?)null, today.TryGetValue(id, out var b) ? b : (long?)null);
                if (was != had)
                    moved.Add(new MovedInput(kind, id.ToString(), was is { } w ? V(w) : null, had is { } h ? V(h) : null));
            }
        }
        Streams("scenario", pin.Scenarios, now.Scenarios);
        Streams("schema", pin.Schemas, now.Schemas);
        Streams("promise", pin.Promises, now.Promises);
        // A state of the format before the review pins no decision, so a decision cannot have moved for it.
        if (pin.Decisions is not null)
            Streams("decision", pin.Decisions, now.Decisions ?? []);

        static string RunKey(PinnedRun run) => string.Create(CultureInfo.InvariantCulture, $"{run.PromiseId}:{run.PromiseRevision}:{run.ObligationId}");
        var runsThen = pin.Runs.GroupBy(RunKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().RunId, StringComparer.Ordinal);
        var runsNow = now.Runs.ToDictionary(RunKey, r => r.RunId, StringComparer.Ordinal);
        foreach (var key in runsThen.Keys.Union(runsNow.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var (was, had) = (runsThen.TryGetValue(key, out var a) ? a : (Guid?)null, runsNow.TryGetValue(key, out var b) ? b : (Guid?)null);
            if (was != had)
                moved.Add(new MovedInput("run", key, was?.ToString(), had?.ToString()));
        }

        static string Source(PinnedSource s) => $"tracked {s.TrackedRef ?? "-"}@{s.TrackedCommit ?? "-"}, newest scan at {s.LatestCommit ?? "-"}";
        var sourcesThen = pin.Sources.GroupBy(s => s.Repository, StringComparer.Ordinal).ToDictionary(g => g.Key, g => Source(g.Last()), StringComparer.Ordinal);
        var sourcesNow = now.Sources.ToDictionary(s => s.Repository, Source, StringComparer.Ordinal);
        foreach (var key in sourcesThen.Keys.Union(sourcesNow.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var (was, had) = (sourcesThen.GetValueOrDefault(key), sourcesNow.GetValueOrDefault(key));
            if (was != had)
                moved.Add(new MovedInput("source", key, was, had));
        }
        return moved;
    }

    private static async Task<(Bundle? Bundle, IResult? Refusal)> BuildAsync(
        IServiceProvider services, Guid projectId, Guid? revisionId, Guid? branchId, BuildLimits limits, CancellationToken ct)
    {
        var (pinned, refusal) = await PinnedModel.ResolveAsync(services, projectId, revisionId, branchId, ct);
        if (pinned is null)
            return (null, refusal);
        try
        {
            return (Bundle.Build(await BuildInput.LoadAsync(services, projectId, pinned, limits, ct), limits), null);
        }
        catch (BundleTooLargeException tooLarge)
        {
            return (null, TooLarge(tooLarge));
        }
    }

    private static string UserId(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private static IResult TooLarge(BundleTooLargeException tooLarge) => Results.Json(
        new ErrorResponse("bundle_too_large", tooLarge.Message), statusCode: StatusCodes.Status413PayloadTooLarge);

    private static IResult Invalid(string field, string message) =>
        Results.BadRequest(new ErrorResponse("validation_error", "Validation failed", Errors: [new FieldError(field, message)]));
}
