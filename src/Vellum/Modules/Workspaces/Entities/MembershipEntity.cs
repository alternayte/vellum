namespace Vellum.Modules.Workspaces.Entities;

public class MembershipEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string UserId { get; set; } = null!;
    public string Role { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}
