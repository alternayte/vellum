using System.IO.Compression;
using System.Security.Claims;
using System.Text.Json;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vellum.Modules.Drafts;
using Vellum.Modules.Modelling;
using Vellum.Modules.Promises;
using Vellum.Modules.Workspaces;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Build;

/// <param name="RevisionId">A named model revision. Without it the export pins the branch at its current version and reports that version.</param>
/// <param name="BranchId">A draft or variant branch. Default: the main model.</param>
/// <param name="Format"><c>bundle</c> (a zip of every file) or <c>sdd</c> (<c>SDD.md</c> alone).</param>
public sealed record ExportRequest(Guid? RevisionId = null, Guid? BranchId = null, string Format = "bundle");

public sealed record BriefRequest(
    IReadOnlyList<Guid>? PromiseIds = null, IReadOnlyList<Guid>? ScenarioIds = null, IReadOnlyList<Guid>? ElementIds = null,
    Guid? RevisionId = null, Guid? BranchId = null);

/// <summary>A manifest to check against what its pinned state generates now.</summary>
public sealed record VerifyRequest(BuildManifest Manifest);

/// <param name="Status">
/// <c>match</c>: the file is what the pinned state generates. <c>differs</c>: both have the
/// file and the bytes differ. <c>missing</c>: the pinned state generates a file that was not
/// given. <c>unknown-file</c>: a file was given that the pinned state does not generate.
/// </param>
public sealed record VerifiedFile(string Path, string Status, string? ExpectedSha256, string? ActualSha256);

/// <param name="Identical">True when every file has the status <c>match</c>.</param>
public sealed record VerifyResponse(
    PinnedState Pinned, bool Identical, string ExpectedBundleHash, string ActualBundleHash,
    IReadOnlyList<VerifiedFile> Files, SelfCheck SelfCheck);

/// <summary>
/// Build mode (SDD §3): the standalone SDD and the agent handoff bundle of a pinned model
/// state, the check that a bundle is what its pinned state generates, and the change-scoped
/// build brief. Every endpoint reads and none writes, so a Viewer may call them all.
/// </summary>
public static class BuildModule
{
    public static IServiceCollection AddBuildModule(this IServiceCollection services)
    {
        services.TryAddSingleton(BuildLimits.Default);
        return services;
    }

    public static WebApplication MapBuildEndpoints(this WebApplication app)
    {
        var build = app.MapGroup("/api/projects/{projectId}/build").RequireAuthorization().WithTags("Build");

        build.MapPost("/export", Export)
            .Produces<string>(contentType: "text/markdown")
            .Produces(StatusCodes.Status200OK, contentType: "application/zip")
            .Produces<ErrorResponse>(StatusCodes.Status413PayloadTooLarge);
        build.MapGet("/manifest", Manifest).Produces<BuildManifest>();
        build.MapPost("/brief", MakeBrief).Produces<BriefResult>();

        // A bundle is uploaded whole, so this endpoint takes a body as large as a bundle may be.
        build.MapPost("/verify", Verify)
            .Accepts<VerifyRequest>("application/json", "application/zip")
            .Produces<VerifyResponse>()
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

        var (bundle, refusal) = await BuildAsync(http.RequestServices, projectId, request.RevisionId, request.BranchId, limits, ct);
        if (bundle is null)
            return refusal!;

        // The headers name the pinned state, so a caller can ask for the same export again.
        http.Response.Headers["X-Vellum-Stream-Version"] = bundle.Manifest.Pinned.StreamVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        http.Response.Headers["X-Vellum-Bundle-Hash"] = bundle.Manifest.BundleHash;
        http.Response.Headers["X-Vellum-Build-Ready"] = bundle.Manifest.BuildReady ? "true" : "false";
        return request.Format == "sdd"
            ? Results.Bytes(bundle.Files[SddDocument.Path], "text/markdown; charset=utf-8", "SDD.md")
            : Results.Bytes(bundle.Zip(), "application/zip", $"vellum-bundle-{bundle.Manifest.BundleHash[..12]}.zip");
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
        if (selection.PromiseIds.Count + selection.ScenarioIds.Count + selection.ElementIds.Count > 200)
            return Invalid("promiseIds", "Select at most 200 things");

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

    /// <summary>
    /// Generates the bundle of the manifest's pinned state again and compares file by file.
    /// The body is a manifest as JSON (<c>{ "manifest": … }</c>) or a bundle zip; for a zip
    /// the bytes of each entry are hashed, so an edited file shows even when the manifest in
    /// the zip was edited to agree with it.
    /// </summary>
    private static async Task<IResult> Verify(
        Guid projectId, HttpContext http, WorkspaceAuthorizationService auth, BuildLimits limits, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, UserId(http.User), WorkspaceRole.Viewer, ct);

        using var body = new MemoryStream();
        await http.Request.Body.CopyToAsync(body, ct);
        BuildManifest? manifest;
        Dictionary<string, string> given;
        try
        {
            if (http.Request.ContentType?.StartsWith("application/zip", StringComparison.OrdinalIgnoreCase) == true)
                (manifest, given) = ReadZip(body, limits);
            else
            {
                manifest = JsonSerializer.Deserialize<VerifyRequest>(body.GetBuffer().AsSpan(0, (int)body.Length), BuildText.Json)?.Manifest;
                given = (manifest?.Files ?? []).Where(f => f?.Path is not null && f.Sha256 is not null)
                    .GroupBy(f => f.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Sha256, StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            return Results.BadRequest(new ErrorResponse("invalid_bundle", "The body is not a manifest or a bundle zip", ex.Message));
        }
        if (manifest?.Pinned is not { } pin)
            return Results.BadRequest(new ErrorResponse("invalid_bundle", "The body holds no manifest with a pinned state"));
        if (pin.ProjectId != projectId)
            return Invalid("manifest.pinned.projectId", "The manifest is of another project");

        var services = http.RequestServices;
        PinnedModel? pinned;
        if (pin.RevisionId is { } revisionId)
        {
            (pinned, var refusal) = await PinnedModel.ResolveAsync(services, projectId, revisionId, null, ct);
            if (pinned is null)
                return refusal!;
        }
        else
        {
            if (!await IsBranchAsync(services, projectId, pin.Branch, ct))
                return Invalid("manifest.pinned.branch", "The project has no such model branch");
            var state = await PinnedModel.AtVersionAsync(services.GetRequiredService<IEventStore>(), pin.Branch, pin.StreamVersion, ct);
            if (state is null)
                return Invalid("manifest.pinned.streamVersion", "The branch has no such version");
            pinned = new PinnedModel(new ModelPin(pin.Branch, pin.StreamVersion, null, null), state);
        }

        Bundle bundle;
        try
        {
            bundle = Bundle.Build(await BuildInput.LoadAsync(services, projectId, pinned, limits, ct), limits);
        }
        catch (BundleTooLargeException tooLarge)
        {
            return TooLarge(tooLarge);
        }

        var expected = bundle.Manifest.Files.ToDictionary(f => f.Path, f => f.Sha256, StringComparer.Ordinal);
        var files = expected.Keys.Union(given.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(path =>
            {
                var want = expected.GetValueOrDefault(path);
                var have = given.GetValueOrDefault(path);
                var status = want is null ? "unknown-file" : have is null ? "missing" : want == have ? "match" : "differs";
                return new VerifiedFile(path, status, want, have);
            })
            .ToList();

        return Results.Ok(new VerifyResponse(
            bundle.Manifest.Pinned, files.All(f => f.Status == "match"),
            bundle.Manifest.BundleHash, Bundle.BundleHash(given.Select(f => (f.Key, f.Value))),
            files, bundle.Manifest.SelfCheck));
    }

    /// <summary>The manifest of a bundle zip and the SHA-256 of each other entry, read inside the bundle bounds.</summary>
    private static (BuildManifest? Manifest, Dictionary<string, string> Hashes) ReadZip(MemoryStream body, BuildLimits limits)
    {
        body.Position = 0;
        using var zip = new ZipArchive(body, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count > limits.MaxFiles + 1)
            throw new InvalidDataException("The zip holds more files than a bundle may.");

        BuildManifest? manifest = null;
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
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

            if (entry.FullName == Bundle.ManifestPath)
                manifest = JsonSerializer.Deserialize<BuildManifest>(content.GetBuffer().AsSpan(0, (int)content.Length), BuildText.Json);
            else
                hashes[entry.FullName] = BuildText.Sha256(content.GetBuffer().AsSpan(0, (int)content.Length));
        }
        return (manifest, hashes);
    }

    private static async Task<bool> IsBranchAsync(IServiceProvider services, Guid projectId, Guid streamId, CancellationToken ct) =>
        await services.GetRequiredService<WorkspacesDbContext>().Projects.AsNoTracking().AnyAsync(p => p.Id == projectId && p.StreamId == streamId, ct)
        || await services.GetRequiredService<DraftsDbContext>().Drafts.AsNoTracking().AnyAsync(d => d.ProjectId == projectId && d.StreamId == streamId, ct)
        || await services.GetRequiredService<ModellingDbContext>().Variants.AsNoTracking().AnyAsync(v => v.ProjectId == projectId && v.StreamId == streamId, ct);

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
