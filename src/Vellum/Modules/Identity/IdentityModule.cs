using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Identity.AgentTokens;

namespace Vellum.Modules.Identity;

public static class IdentityModule
{
    public const int MinimumPasswordLength = 10;
    public const string GitHubScheme = "GitHub";
    public const string GitHubCallbackPath = "/api/auth/external/callback";

    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("DefaultConnection")!;

        services.AddDbContext<AppIdentityDbContext>(options =>
            options.UseNpgsql(connectionString)
                   .UseSnakeCaseNamingConvention());

        services.AddIdentity<ApplicationUser, IdentityRole>(options =>
            {
                options.Password.RequireDigit = true;
                // For new passwords. An account from before this rule signs in as before.
                options.Password.RequiredLength = MinimumPasswordLength;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireUppercase = true;
                options.User.RequireUniqueEmail = true;
                // Five wrong passwords lock the account for a short time. The sign-in endpoint
                // answers a locked account like a wrong password.
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
            })
            .AddEntityFrameworkStores<AppIdentityDbContext>()
            .AddDefaultTokenProviders();

        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromDays(7);
            options.SlidingExpiration = true;
            options.Events.OnRedirectToLogin = ctx =>
            {
                ctx.Response.StatusCode = 401;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = ctx =>
            {
                ctx.Response.StatusCode = 403;
                return Task.CompletedTask;
            };
        });

        var authBuilder = services.AddAuthentication().AddAgentTokens();

        var githubSection = config.GetSection("Authentication:GitHub");
        if (githubSection.Exists())
        {
            authBuilder.AddOAuth(GitHubScheme, options =>
            {
                options.ClientId = githubSection["ClientId"]!;
                options.ClientSecret = githubSection["ClientSecret"]!;
                options.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
                options.TokenEndpoint = "https://github.com/login/oauth/access_token";
                options.UserInformationEndpoint = "https://api.github.com/user";
                // The handler owns this path: GitHub sends the browser here with the code. It
                // is the callback URL that the operator registered at GitHub, so it keeps its
                // value. The app's own endpoint is IdentityEndpoints.ExternalCompletePath.
                options.CallbackPath = GitHubCallbackPath;
                options.Scope.Add("user:email");
                options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
                options.ClaimActions.MapJsonKey(ClaimTypes.Name, "name");
                options.Events.OnCreatingTicket = GitHubSignIn.CreateTicketAsync;
                options.Events.OnRemoteFailure = GitHubSignIn.RefuseAsync;
            });
        }

        var googleSection = config.GetSection("Authentication:Google");
        if (googleSection.Exists())
        {
            authBuilder.AddGoogle(options =>
            {
                options.ClientId = googleSection["ClientId"]!;
                options.ClientSecret = googleSection["ClientSecret"]!;
            });
        }

        services.AddAuthorization();

        return services;
    }
}
