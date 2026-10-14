using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Ai.Design;
using Vellum.Modules.Ai.Providers;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using Vellum.Shared.Outbound;

namespace Vellum.Modules.Ai;

public static class AiModule
{
    public static IServiceCollection AddAiModule(this IServiceCollection services)
    {
        // The connection string is read when the context is first used, so configuration that
        // a test host overrides applies.
        services.AddOutboundClient<IAnthropicApi>();
        services.AddOutboundClient<IOpenAiApi>();
        services.AddOutboundClient<IGeminiApi>();
        services.AddOutboundClient<IOpenAiCompatibleApi>();
        services.AddTransient<IAiProviderAdapter, AnthropicAdapter>();
        services.AddTransient<IAiProviderAdapter, OpenAiAdapter>();
        services.AddTransient<IAiProviderAdapter, GeminiAdapter>();
        services.AddTransient<IAiProviderAdapter, OpenAiCompatibleAdapter>();

        services.AddScoped<AiUsageLedger>();
        services.AddScoped<AiClient>();
        services.AddAiDesign();
        return services;
    }

    public static WebApplication MapAiEndpoints(this WebApplication app)
    {
        var workspace = app.MapGroup("/api/workspaces/{workspaceId}/ai").WithTags("AI").RequireAuthorization();
        workspace.MapGet("/usage", GetAiUsage.Handle)
            .Produces<AiUsageResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);
        workspace.MapPost("/providers/{providerId}/test", TestAiProvider.Handle)
            .Produces<AiConnectionTest>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        var prices = app.MapGroup("/api/instance/ai/prices").WithTags("AI").RequireAuthorization();
        prices.MapGet("/", ModelPrices.List).Produces<List<ModelPriceDto>>();
        prices.MapPost("/", ModelPrices.Set)
            .Produces<ModelPriceDto>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        app.MapAiDesignEndpoints();
        return app;
    }
}

/// <summary>
/// One small real call to the provider. A failure on the provider's side is a 200 with
/// <c>ok: false</c>; the error says what to change.
/// </summary>
public static class TestAiProvider
{
    public static async Task<IResult> Handle(
        Guid workspaceId,
        Guid providerId,
        ClaimsPrincipal user,
        WorkspaceAuthorizationService auth,
        AiClient ai,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        return await ai.TestProviderAsync(workspaceId, providerId, userId, ct) is { } test
            ? Results.Ok(test)
            : Results.NotFound(new ErrorResponse("not_found", "The workspace has no AI provider with that ID."));
    }
}
