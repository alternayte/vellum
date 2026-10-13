using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Modules.Import.Proposals;
using Vellum.Modules.Import.Scans;
using Vellum.Shared;

namespace Vellum.Modules.Import;

public static class ImportModule
{
    /// <param name="runWorker">False when the host is started only to write the OpenAPI document.</param>
    public static IServiceCollection AddImportModule(this IServiceCollection services, bool runWorker)
    {
        // The connection string is read when the context is first used, so configuration that
        // a test host overrides applies.
        services.AddDbContext<ImportDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection"))
                   .UseSnakeCaseNamingConvention());

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ScanSignal>();
        services.AddSingleton<ScanWorker>();

        // Import:Worker:Enabled=false turns the worker of this instance off; scans then wait
        // for an instance that runs one. Import:Worker:Parallelism is how many scans one
        // instance runs at a time.
        services.AddSingleton(provider =>
        {
            var section = provider.GetRequiredService<IConfiguration>().GetSection("Import:Worker");
            var defaults = ScanWorkerOptions.Default;
            return defaults with
            {
                Enabled = section.GetValue("Enabled", defaults.Enabled),
                PollInterval = TimeSpan.FromSeconds(Math.Clamp(section.GetValue("PollSeconds", defaults.PollInterval.TotalSeconds), 0.05, 3600)),
                LeaseDuration = TimeSpan.FromSeconds(Math.Clamp(section.GetValue("LeaseSeconds", defaults.LeaseDuration.TotalSeconds), 5, 3600)),
                Parallelism = Math.Clamp(section.GetValue("Parallelism", defaults.Parallelism), 1, 32),
                RetentionInterval = TimeSpan.FromSeconds(Math.Clamp(section.GetValue("RetentionSeconds", defaults.Retention.TotalSeconds), 1, 86400)),
            };
        });

        if (runWorker)
            services.AddHostedService<ScanWorkerService>();
        return services;
    }

    public static WebApplication MapImportEndpoints(this WebApplication app)
    {
        var imports = app.MapGroup("/api/projects/{projectId}/imports")
            .RequireAuthorization()
            .WithTags("Imports");

        imports.MapPost("/", ScanRequests.Start)
            .Produces<StartScanResponse>(StatusCodes.Status202Accepted)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests);

        imports.MapGet("/", ScanRequests.List)
            .Produces<Page<ScanDto>>();

        imports.MapGet("/{importId}", ScanRequests.Get)
            .Produces<ScanDetailDto>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        imports.MapPost("/{importId}/cancel", ScanRequests.Cancel)
            .Produces(StatusCodes.Status202Accepted)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        imports.MapPost("/{importId}/resume", ScanRequests.Resume)
            .Produces(StatusCodes.Status202Accepted)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests);

        imports.MapPost("/{importId}/track", ScanRequests.Track)
            .Produces<ScanDetailDto>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        imports.MapGet("/{importId}/proposals", ProposalReads.List)
            .Produces<ProposalPage>();

        imports.MapGet("/{importId}/proposals/{proposalId}", ProposalReads.Get)
            .Produces<ProposalDto>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        imports.MapPost("/{importId}/proposals/decide", DecideProposals.Handle).RequireHuman()
            .Produces<DecideProposalsResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        imports.MapGet("/{importId}/evidence", ProposalReads.Evidence)
            .Produces<Page<EvidenceItemDto>>();

        imports.MapGet("/{importId}/source", SourceWindow.Handle)
            .Produces<SourceWindowDto>()
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        return app;
    }
}
