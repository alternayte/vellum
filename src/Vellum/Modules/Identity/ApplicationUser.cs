using Microsoft.AspNetCore.Identity;

namespace Vellum.Modules.Identity;

public class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }
}
