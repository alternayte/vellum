namespace Vellum.Modules.Settings.Entities;

/// <summary>One row (<see cref="SingletonId"/>). A null column means "use the profile default".</summary>
public class InstanceSettingsEntity
{
    public const int SingletonId = 1;

    public int Id { get; set; }
    public string? Policy { get; set; }
    public string? Defaults { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = null!;
}

public class WorkspaceSettingsEntity
{
    public Guid WorkspaceId { get; set; }
    public string Overrides { get; set; } = null!;
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = null!;
}

/// <summary>An envelope-encrypted credential. No column holds the value.</summary>
public class SecretEntity
{
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = null!;
    public string MasterKeyId { get; set; } = null!;
    public byte[] WrapNonce { get; set; } = null!;
    public byte[] WrappedKey { get; set; } = null!;
    public byte[] Nonce { get; set; } = null!;
    public byte[] Ciphertext { get; set; } = null!;
    public string Fingerprint { get; set; } = null!;
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = null!;
}

/// <summary>Append-only: a database trigger refuses UPDATE, DELETE and TRUNCATE.</summary>
public class AuditEntryEntity
{
    public long Id { get; set; }
    public Guid? WorkspaceId { get; set; }
    public DateTimeOffset At { get; set; }
    public string ActorId { get; set; } = null!;
    public string Action { get; set; } = null!;
    public string Target { get; set; } = null!;
}

public class InstanceAdminEntity
{
    public string UserId { get; set; } = null!;
    public DateTimeOffset GrantedAt { get; set; }
}

public class DataProtectionKeyEntity
{
    public int Id { get; set; }
    public string? FriendlyName { get; set; }
    public string Xml { get; set; } = null!;
}
