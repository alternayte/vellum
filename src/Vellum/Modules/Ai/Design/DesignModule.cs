using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Shared;

namespace Vellum.Modules.Ai.Design;

/// <summary>
/// Marks an endpoint that asks a provider to generate: a flow proposal, an architecture
/// proposal or an explanation. Each such request can spend money, and the cheapest role can
/// ask for an explanation, so these endpoints share two token buckets: one for each user and
/// one for each project. A request takes one token from both. A request over either limit
/// gets 429 with <c>Retry-After</c> before the endpoint runs.
/// </summary>
public sealed record AiGenerationLimit
{
    /// <summary>The most generating requests one user can make in a burst, and the number that comes back each minute.</summary>
    public const int PerUser = 30;

    /// <summary>The most generating requests all users of one project can make in a burst, and the number that comes back each minute.</summary>
    public const int PerProject = 90;

    public static readonly TimeSpan Period = TimeSpan.FromMinutes(1);
}

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
        services.AddScoped<PrivateSources>();
        services.AddSingleton<ExplainStreams>();
        services.AddSingleton<ProposalSubmitRate>();

        // The limiter runs for every request and limits only an endpoint that carries
        // AiGenerationLimit. It runs after authorization, so the user is known.
        services.Configure<RateLimiterOptions>(options => options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<HttpContext, string>(http => Bucket(
                http, "user:" + http.User.FindFirstValue(ClaimTypes.NameIdentifier), AiGenerationLimit.PerUser)),
            PartitionedRateLimiter.Create<HttpContext, string>(http => Bucket(
                http, "project:" + http.GetRouteValue("projectId"), AiGenerationLimit.PerProject))));
        return services;
    }

    private static RateLimitPartition<string> Bucket(HttpContext http, string key, int tokens) =>
        http.GetEndpoint()?.Metadata.GetMetadata<AiGenerationLimit>() is null
            ? RateLimitPartition.GetNoLimiter("")
            : RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = tokens,
                TokensPerPeriod = tokens,
                ReplenishmentPeriod = AiGenerationLimit.Period,
                QueueLimit = 0,
                AutoReplenishment = true,
            });

    public static WebApplication MapAiDesignEndpoints(this WebApplication app)
    {
        var ai = app.MapGroup("/api/projects/{projectId}/ai").WithTags("AI design").RequireAuthorization();
        var generates = new AiGenerationLimit();

        var flows = ai.MapGroup("/flow-proposals");
        flows.MapPost("/", FlowProposals.Create).RequireHuman()
            .WithMetadata(generates)
            .Produces<FlowProposalDto>(StatusCodes.Status201Created)
            .Produces<AiUnavailable>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests);
        // A caller that wrote the flow itself submits it: an agent token with the propose scope,
        // or a person. It is stored as a draft that only a person accepts. No provider is called.
        flows.MapPost("/submit", FlowProposals.Submit).AllowAgent(AgentScope.Propose)
            .WithBodyLimit(RequestBodyLimit.Document)
            .AddEndpointFilter(CallerRate.Filter<ProposalSubmitRate>)
            .Produces<FlowProposalDto>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests);
        flows.MapGet("/", FlowProposals.List).Produces<Page<ProposalSummary>>();
        flows.MapGet("/{proposalId}", FlowProposals.Get)
            .Produces<FlowProposalDto>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);
        flows.MapDelete("/{proposalId}", FlowProposals.Delete)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);
        flows.MapPost("/{proposalId}/accept", FlowProposals.Accept).RequireHuman()
            .Produces<AcceptFlowProposalResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ProposalStale>(StatusCodes.Status409Conflict);
        flows.MapPost("/{proposalId}/reject", FlowProposals.Reject)
            .Produces<FlowProposalDto>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);
        flows.MapPost("/{proposalId}/missing/{alias}/create", FlowProposals.CreateMissing).RequireHuman()
            .Produces<FlowProposalDto>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        var architecture = ai.MapGroup("/architecture-proposals");
        architecture.MapPost("/", ArchitectureProposals.Create).RequireHuman()
            .WithMetadata(generates)
            .Produces<ArchitectureProposalDto>(StatusCodes.Status201Created)
            .Produces<AiUnavailable>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests);
        architecture.MapPost("/submit", ArchitectureProposals.Submit).AllowAgent(AgentScope.Propose)
            .WithBodyLimit(RequestBodyLimit.Document)
            .AddEndpointFilter(CallerRate.Filter<ProposalSubmitRate>)
            .Produces<ArchitectureProposalDto>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests);
        architecture.MapGet("/", ArchitectureProposals.List).Produces<Page<ProposalSummary>>();
        architecture.MapGet("/{proposalId}", ArchitectureProposals.Get)
            .Produces<ArchitectureProposalDto>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);
        architecture.MapDelete("/{proposalId}", ArchitectureProposals.Delete)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);
        architecture.MapPost("/{proposalId}/decide", ArchitectureProposals.Decide).RequireHuman()
            .Produces<ArchitectureProposalDto>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ProposalItemsRefused>(StatusCodes.Status409Conflict);

        ai.MapPost("/explain", Explain.Handle)
            .WithMetadata(generates)
            .Produces<AiUnavailable>()
            .Produces<ExplainDelta>(StatusCodes.Status200OK, "text/event-stream")
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests);

        return app;
    }
}
