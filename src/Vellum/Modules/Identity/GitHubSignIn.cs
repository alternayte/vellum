using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Vellum.Shared;

namespace Vellum.Modules.Identity;

/// <summary>
/// Reads who signed in at GitHub, with the access token of that sign-in and the handler's own
/// back channel. The token is used for these two reads and is not stored. The account ID and
/// the display name come from <c>/user</c>. The email address comes from <c>/user/emails</c>,
/// and only the primary address that GitHub verified is taken: the public address of a
/// profile is text that its owner typed.
/// </summary>
public static class GitHubSignIn
{
    public const string EmailsEndpoint = "https://api.github.com/user/emails";

    public static async Task CreateTicketAsync(OAuthCreatingTicketContext context)
    {
        using (var user = await GetAsync(context, context.Options.UserInformationEndpoint))
            context.RunClaimActions(user.RootElement);

        using var emails = await GetAsync(context, EmailsEndpoint);
        if (emails.RootElement.ValueKind != JsonValueKind.Array)
            return;

        foreach (var entry in emails.RootElement.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True
                && entry.TryGetProperty("verified", out var verified) && verified.ValueKind == JsonValueKind.True
                && entry.TryGetProperty("email", out var email) && email.GetString() is { Length: > 0 } address)
            {
                context.Identity!.AddClaim(new Claim(ClaimTypes.Email, address, ClaimValueTypes.String, context.Options.ClaimsIssuer));
                return;
            }
        }
    }

    /// <summary>
    /// A sign-in that did not complete: the person refused at GitHub, the answer came without
    /// the state of this browser, or GitHub refused the code. The answer is the error envelope
    /// and no detail of the failure, which can repeat a value of the request.
    /// </summary>
    public static async Task RefuseAsync(RemoteFailureContext context)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new ErrorResponse("external_sign_in_failed", "The sign-in with GitHub did not complete",
            "Start the sign-in again from Vellum."));
        context.HandleResponse();
    }

    private static async Task<JsonDocument> GetAsync(OAuthCreatingTicketContext context, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Vellum", null));

        using var response = await context.Backchannel.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.HttpContext.RequestAborted);
        // The text of the answer is not put into the failure: it can repeat the token.
        if (!response.IsSuccessStatusCode)
            throw new AuthenticationFailureException($"GitHub answered {(int)response.StatusCode} to a read of the signed-in account.");

        await using var body = await response.Content.ReadAsStreamAsync(context.HttpContext.RequestAborted);
        return await JsonDocument.ParseAsync(body, cancellationToken: context.HttpContext.RequestAborted);
    }
}
