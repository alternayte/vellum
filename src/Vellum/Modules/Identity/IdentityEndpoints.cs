using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Vellum.Shared;

namespace Vellum.Modules.Identity;

public sealed record RegisterRequest(string Email, string Password, string? DisplayName);
public sealed record LoginRequest(string Email, string Password);
public sealed record UserInfoResponse(string Id, string Email, string? DisplayName);

public static class IdentityEndpoints
{
    public static WebApplication MapIdentityEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/register", async (
            RegisterRequest request,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager) =>
        {
            var user = new ApplicationUser
            {
                UserName = request.Email,
                Email = request.Email,
                DisplayName = request.DisplayName
            };

            var result = await userManager.CreateAsync(user, request.Password);
            if (!result.Succeeded)
            {
                var errors = result.Errors
                    .Select(e => new FieldError(e.Code, e.Description))
                    .ToList();
                return Results.BadRequest(new ErrorResponse("validation_error", "Registration failed", Errors: errors));
            }

            await signInManager.SignInAsync(user, isPersistent: true);
            return Results.Created($"/api/auth/me", new UserInfoResponse(user.Id, user.Email!, user.DisplayName));
        });

        group.MapPost("/login", async (
            LoginRequest request,
            SignInManager<ApplicationUser> signInManager) =>
        {
            var result = await signInManager.PasswordSignInAsync(
                request.Email, request.Password, isPersistent: true, lockoutOnFailure: false);

            if (!result.Succeeded)
                return Results.Unauthorized();

            return Results.Ok();
        });

        group.MapPost("/logout", async (SignInManager<ApplicationUser> signInManager) =>
        {
            await signInManager.SignOutAsync();
            return Results.Ok();
        }).RequireAuthorization();

        group.MapGet("/me", async (
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null) return Results.Unauthorized();
            return Results.Ok(new UserInfoResponse(user.Id, user.Email!, user.DisplayName));
        }).RequireAuthorization();

        group.MapGet("/external/{provider}", (
            string provider,
            SignInManager<ApplicationUser> signInManager) =>
        {
            var properties = signInManager.ConfigureExternalAuthenticationProperties(
                provider, "/api/auth/external/callback");
            return Results.Challenge(properties, [provider]);
        });

        group.MapGet("/external/callback", async (
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser> userManager) =>
        {
            var info = await signInManager.GetExternalLoginInfoAsync();
            if (info is null)
                return Results.Unauthorized();

            var signInResult = await signInManager.ExternalLoginSignInAsync(
                info.LoginProvider, info.ProviderKey, isPersistent: true);

            if (signInResult.Succeeded)
                return Results.Redirect("/");

            var email = info.Principal.FindFirstValue(ClaimTypes.Email);
            if (email is null)
                return Results.BadRequest(new ErrorResponse("validation_error", "Email not provided by external provider"));

            var user = await userManager.FindByEmailAsync(email);
            if (user is null)
            {
                user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    DisplayName = info.Principal.FindFirstValue(ClaimTypes.Name)
                };
                await userManager.CreateAsync(user);
            }

            await userManager.AddLoginAsync(user, info);
            await signInManager.SignInAsync(user, isPersistent: true);
            return Results.Redirect("/");
        });

        return app;
    }
}
