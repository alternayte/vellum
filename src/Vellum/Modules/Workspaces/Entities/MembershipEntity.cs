namespace Vellum.Modules.Workspaces.Entities;

public class MembershipEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string UserId { get; set; } = null!;
    public string Role { get; set; } = null!;
    /// <summary>
    /// When the membership was made. No writer set this before the member list showed it,
    /// so a row from that time holds the zero date; the list reads that as not recorded.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
}
