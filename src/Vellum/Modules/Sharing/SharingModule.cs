using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Shared;

namespace Vellum.Modules.Sharing;

/// <summary>
/// Share links and embeds: read-only pages of a project or a view for people without an
/// account. Members manage links under <c>/api/projects/{projectId}/share-links</c>; a visitor
/// reads one under <c>/api/share/{token}</c>, a route of its own that answers with the share's
/// own types.
/// </summary>
public static class SharingModule
{
    public static IServiceCollection AddSharingModule(this IServiceCollection services)
    {
        services.AddScoped<SharePageReader>();
        services.AddScoped<ShareLinkResolver>();
        services.AddSingleton<ShareGrants>();
        services.AddSingleton<IPasswordHasher<ShareLinkEntity>, PasswordHasher<ShareLinkEntity>>();
        services.AddScoped<IProjectRetention, SharingRetention>();
        return services;
    }

    public static WebApplication MapSharingEndpoints(this WebApplication app)
    {
        var links = app.MapGroup("/api/projects/{projectId}/share-links").WithTags("Sharing").RequireAuthorization();
        links.MapGet("/", ShareLinks.List).Produces<List<ShareLinkDto>>();
        links.MapPost("/", ShareLinks.Create).RequireHuman()
            .Produces<ShareLinkCreatedDto>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);
        links.MapPatch("/{linkId:guid}", ShareLinks.Change).RequireHuman()
            .Produces<ShareLinkDto>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);
        links.MapPost("/{linkId:guid}/revoke", ShareLinks.Revoke).RequireHuman()
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        // No credential: the token is the credential. The limit for callers without one applies.
        var share = app.MapGroup("/api/share").WithTags("Shared pages").AllowAnonymous();
        share.MapGet("/{token}", PublicShare.Read)
            .Produces<SharedPage>()
            .Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);
        share.MapPost("/{token}/unlock", PublicShare.Unlock)
            .RequireRateLimiting(RequestLimits.CredentialPolicy)
            .WithBodyLimit(4096)
            .Produces<ShareGrantDto>()
            .Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests);

        return app;
    }
}

/// <summary>What the share tables keep of a deleted project: nothing. A snapshot goes with its link (ON DELETE CASCADE).</summary>
public sealed class SharingRetention(AppDbContext db) : IProjectRetention
{
    public Task DeleteProjectAsync(DeletedProject project, CancellationToken ct) =>
        db.Set<ShareLinkEntity>().Where(l => l.ProjectId == project.Id).ExecuteDeleteAsync(ct);
}
