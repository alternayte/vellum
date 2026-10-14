using System.Security.Claims;

namespace Vellum.Shared;

public static class UserIdentity
{
    /// <summary>
    /// The ID of the signed-in user, or of the user who created the agent token of the call.
    /// Only for endpoints that require authorization: an anonymous caller has none.
    /// </summary>
    public static string UserId(this ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)!;
}
