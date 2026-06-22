using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Vellum.Modules.Identity;

public static class IdentityModule
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("DefaultConnection")!;

        services.AddDbContext<AppIdentityDbContext>(options =>
            options.UseNpgsql(connectionString)
                   .UseSnakeCaseNamingConvention());

        services.AddIdentity<ApplicationUser, IdentityRole>(options =>
            {
                options.Password.RequireDigit = true;
                options.Password.RequiredLength = 6;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireUppercase = true;
                options.User.RequireUniqueEmail = true;
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

        var authBuilder = services.AddAuthentication();

        var githubSection = config.GetSection("Authentication:GitHub");
        if (githubSection.Exists())
        {
            authBuilder.AddOAuth("GitHub", options =>
            {
                options.ClientId = githubSection["ClientId"]!;
                options.ClientSecret = githubSection["ClientSecret"]!;
                options.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
                options.TokenEndpoint = "https://github.com/login/oauth/access_token";
                options.UserInformationEndpoint = "https://api.github.com/user";
                options.CallbackPath = "/api/auth/external/callback";
                options.Scope.Add("user:email");
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
