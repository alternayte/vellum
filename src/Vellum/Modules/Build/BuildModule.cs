using Microsoft.Extensions.DependencyInjection.Extensions;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Shared;

namespace Vellum.Modules.Build;

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
        // The answer has the headers X-Vellum-Build-Ready, of the exported state, and X-Vellum-Current: false
        // when that state is a past one.
        build.MapPost("/export", Export).AllowAgent(AgentScope.Export)
            .Produces<string>(contentType: "text/markdown")
            .Produces(StatusCodes.Status200OK, contentType: "application/zip")
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ErrorResponse>(StatusCodes.Status413PayloadTooLarge)
            .WithBodyLimit(BuildLimits.Default.MaxManifestBytes);
        // The manifest holds no content: whether the state is build ready, the blockers, the
        // pinned versions and the file hashes. A token that only reads may have it.
        build.MapGet("/manifest", Manifest).AllowAgent(AgentScope.Read, AgentScope.Export).Produces<BuildManifest>();
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
}
