using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Ai.Design;

/// <summary>
/// AI-assisted design: flow proposals, architecture proposals and explanations. Every
/// endpoint works without an AI provider: the three that call one answer with
/// <see cref="AiUnavailable"/>, and the reads and decisions do not call one.
/// </summary>
public static class DesignModule
{
    public static IServiceCollection AddAiDesign(this IServiceCollection services)
    {
        // The connection string is read when the context is first used, so configuration that
        // a test host overrides applies.
        services.AddDbContext<AiDesignDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection"))
                   .UseSnakeCaseNamingConvention());
        return services;
    }

    public static WebApplication MapAiDesignEndpoints(this WebApplication app)
    {
        var ai = app.MapGroup("/api/projects/{projectId}/ai").WithTags("AI design").RequireAuthorization();

        var flows = ai.MapGroup("/flow-proposals");
        flows.MapPost("/", FlowProposals.Create)
            .Produces<FlowProposalDto>(StatusCodes.Status201Created)
            .Produces<AiUnavailable>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);
        flows.MapGet("/", FlowProposals.List).Produces<Page<ProposalSummary>>();
        flows.MapGet("/{proposalId}", FlowProposals.Get)
            .Produces<FlowProposalDto>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);
        flows.MapPost("/{proposalId}/accept", FlowProposals.Accept)
            .Produces<AcceptFlowProposalResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);
        flows.MapPost("/{proposalId}/reject", FlowProposals.Reject)
            .Produces<FlowProposalDto>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);
        flows.MapPost("/{proposalId}/missing/{alias}/create", FlowProposals.CreateMissing)
            .Produces<FlowProposalDto>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        var architecture = ai.MapGroup("/architecture-proposals");
        architecture.MapPost("/", ArchitectureProposals.Create)
            .Produces<ArchitectureProposalDto>(StatusCodes.Status201Created)
            .Produces<AiUnavailable>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);
        architecture.MapGet("/", ArchitectureProposals.List).Produces<Page<ProposalSummary>>();
        architecture.MapGet("/{proposalId}", ArchitectureProposals.Get)
            .Produces<ArchitectureProposalDto>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);
        architecture.MapPost("/{proposalId}/decide", ArchitectureProposals.Decide)
            .Produces<ArchitectureProposalDto>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ProposalItemsRefused>(StatusCodes.Status409Conflict);

        ai.MapPost("/explain", Explain.Handle)
            .Produces<AiUnavailable>()
            .Produces<ExplainDelta>(StatusCodes.Status200OK, "text/event-stream")
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        return app;
    }
}
