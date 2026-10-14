namespace Vellum.Modules.Workspaces.Entities;

/// <summary>
/// An offer of a membership to an email address. The person with an account of that address
/// accepts or declines it; only an accept makes a membership. An invitation names an address
/// and no account, so its creation says nothing about whether an account exists.
/// </summary>
public class InvitationEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    /// <summary>The address as the owner typed it, trimmed.</summary>
    public string Email { get; set; } = null!;
    /// <summary>The address as Identity normalizes it for a lookup (upper case).</summary>
    public string NormalizedEmail { get; set; } = null!;
    public string Role { get; set; } = null!;
    /// <summary>One of <see cref="InvitationStatus"/>. An expired invitation stays pending here: expiry is read from <see cref="ExpiresAt"/>.</summary>
    public string Status { get; set; } = InvitationStatus.Pending;
    public string InvitedBy { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    /// <summary>Who accepted, declined or revoked it, and when.</summary>
    public string? RespondedBy { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
}

public static class InvitationStatus
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Declined = "declined";
    public const string Revoked = "revoked";
    /// <summary>Not stored: a pending invitation whose expiry has passed.</summary>
    public const string Expired = "expired";
}
