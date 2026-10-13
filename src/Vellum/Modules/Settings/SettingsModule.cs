using System.Net;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Settings.Audit;
using Vellum.Modules.Settings.Crypto;
using Vellum.Modules.Settings.Document;
using Vellum.Modules.Settings.Instance;
using Vellum.Modules.Settings.Secrets;
using Vellum.Modules.Settings.Setup;
using Vellum.Modules.Settings.Workspace;
using Vellum.Shared;

namespace Vellum.Modules.Settings;

public static class SettingsModule
{
    /// <param name="generatingOpenApi">
    /// True in the build-time OpenAPI generator. That process reads no environment and writes no key file.
    /// </param>
    public static IServiceCollection AddSettingsModule(this IServiceCollection services, bool generatingOpenApi)
    {
        // Read when first used, so configuration that a test host overrides applies.
        services.AddSingleton(provider => generatingOpenApi
            ? Bootstrap.Ephemeral()
            : Bootstrap.Load(provider.GetRequiredService<IConfiguration>()));

        services.AddDbContext<SettingsDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection"))
                   .UseSnakeCaseNamingConvention());

        services.AddSingleton<HostResolver>(_ => (host, ct) => Dns.GetHostAddressesAsync(host, ct));
        services.AddSingleton<OutboundUrlPolicy>();
        services.AddScoped<SettingsValidator>();
        services.AddScoped<SettingsResolver>();
        services.AddScoped<InstanceAuthorization>();
        services.AddScoped<RegistrationPolicy>();
        services.AddScoped<SecretReader>();

        // The keys that sign the auth cookie: stored in Postgres, sealed with the master key,
        // and named for the app so every instance on the database reads the same keys.
        var dataProtection = services.AddDataProtection().SetApplicationName("Vellum");
        if (generatingOpenApi)
        {
            // Data Protection loads its keys when the host starts. The generator has no database.
            dataProtection.UseEphemeralDataProtectionProvider();
        }
        else
        {
            services.AddOptions<KeyManagementOptions>().Configure<IServiceProvider>((options, provider) =>
            {
                options.XmlRepository = new PostgresXmlRepository(provider.GetRequiredService<IConfiguration>());
                options.XmlEncryptor = new MasterKeyXmlEncryptor(provider.GetRequiredService<Bootstrap>().Keys);
            });
        }

        // The hosted profile sends its cookies over HTTPS only. Bootstrap refuses any other value there.
        foreach (var scheme in (string[])[IdentityConstants.ApplicationScheme, IdentityConstants.ExternalScheme])
        {
            services.AddOptions<CookieAuthenticationOptions>(scheme).Configure<Bootstrap>((options, bootstrap) =>
                options.Cookie.SecurePolicy = bootstrap.SecureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest);
        }

        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");

        return services;
    }

    public static WebApplication MapSettingsEndpoints(this WebApplication app)
    {
        // Liveness and readiness in one probe. The body is the status word and nothing else.
        app.MapHealthChecks("/health").AllowAnonymous();

        var setup = app.MapGroup("/api/setup").WithTags("Setup").AllowAnonymous();
        setup.MapGet("/status", GetSetupStatus.Handle).Produces<SetupStatusResponse>();
        setup.MapPost("/", RunSetup.Handle)
            .Produces<SetupResponse>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        var instance = app.MapGroup("/api/instance").WithTags("Instance").RequireAuthorization();
        instance.MapGet("/", GetInstance.Handle).Produces<InstanceInfoResponse>();
        instance.MapGet("/settings", GetInstanceSettings.Handle).Produces<InstanceSettingsResponse>();
        instance.MapPut("/settings", UpdateInstanceSettings.Handle)
            .Produces<InstanceSettingsResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);
        instance.MapPost("/keys/rotate", RotateKeys.Handle)
            .Produces<RotateKeysResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);
        instance.MapGet("/diagnostics", GetDiagnostics.Handle).Produces<DiagnosticsResponse>();

        var workspace = app.MapGroup("/api/workspaces/{workspaceId}").WithTags("Settings").RequireAuthorization();
        workspace.MapGet("/settings", GetWorkspaceSettings.Handle).Produces<WorkspaceSettingsResponse>();
        workspace.MapPut("/settings", UpdateWorkspaceSettings.Handle)
            .Produces<WorkspaceSettingsResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);
        workspace.MapGet("/secrets", ListSecrets.Handle).Produces<List<SecretStatusDto>>();
        workspace.MapPut("/secrets/{name}", SetSecret.Handle)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);
        workspace.MapDelete("/secrets/{name}", DeleteSecret.Handle)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);
        workspace.MapGet("/audit", ListAudit.Handle).Produces<List<AuditEntryDto>>();

        return app;
    }
}
