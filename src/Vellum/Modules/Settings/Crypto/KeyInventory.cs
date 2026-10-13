using System.Xml.Linq;
using Npgsql;

namespace Vellum.Modules.Settings.Crypto;

/// <summary>How many rows the database holds under one master key.</summary>
public sealed record MasterKeyUsage(string MasterKeyId, long Secrets, long DataProtectionKeys)
{
    public long Rows => Secrets + DataProtectionKeys;
}

/// <summary>
/// Reads which master keys the rows in the database are encrypted under. It uses its own
/// connection, because it runs before the host starts and from maintenance commands.
/// </summary>
public static class KeyInventory
{
    private const string SecretsSql = "SELECT master_key_id, count(*) FROM settings.secrets GROUP BY master_key_id";
    private const string DataProtectionSql = "SELECT id, xml FROM settings.data_protection_keys ORDER BY id";

    /// <summary>An empty list for a database that has no settings tables yet.</summary>
    public static IReadOnlyList<MasterKeyUsage> Read(string connectionString)
    {
        var secrets = new Dictionary<string, long>();
        var dataProtection = new List<(int Id, string Xml)>();
        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            using (var command = new NpgsqlCommand(SecretsSql, connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                    secrets[reader.GetString(0)] = reader.GetInt64(1);
            }

            using (var command = new NpgsqlCommand(DataProtectionSql, connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                    dataProtection.Add((reader.GetInt32(0), reader.GetString(1)));
            }
        }
        catch (PostgresException ex) when (IsMissingTable(ex))
        {
            return [];
        }

        return Combine(secrets, dataProtection);
    }

    public static async Task<IReadOnlyList<MasterKeyUsage>> ReadAsync(string connectionString, CancellationToken ct = default)
    {
        var secrets = new Dictionary<string, long>();
        var dataProtection = new List<(int Id, string Xml)>();
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await using (var command = new NpgsqlCommand(SecretsSql, connection))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    secrets[reader.GetString(0)] = reader.GetInt64(1);
            }

            await using (var command = new NpgsqlCommand(DataProtectionSql, connection))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    dataProtection.Add((reader.GetInt32(0), reader.GetString(1)));
            }
        }
        catch (PostgresException ex) when (IsMissingTable(ex))
        {
            return [];
        }

        return Combine(secrets, dataProtection);
    }

    /// <summary>The IDs of the stored Data Protection rows that have an envelope under one of <paramref name="masterKeyIds"/>.</summary>
    internal static async Task<List<int>> DataProtectionRowsUnderAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyCollection<string> masterKeyIds, CancellationToken ct)
    {
        var rows = new List<int>();
        await using var command = new NpgsqlCommand(DataProtectionSql + " FOR UPDATE", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (DataProtectionEnvelope.MasterKeyIds(reader.GetString(1)).Any(masterKeyIds.Contains))
                rows.Add(reader.GetInt32(0));
        }

        return rows;
    }

    // A database that no migration has run on has no settings schema and so no rows.
    private static bool IsMissingTable(PostgresException ex) =>
        ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidSchemaName;

    private static IReadOnlyList<MasterKeyUsage> Combine(Dictionary<string, long> secrets, List<(int Id, string Xml)> dataProtection)
    {
        var keys = new Dictionary<string, long>();
        foreach (var (_, xml) in dataProtection)
        {
            foreach (var id in DataProtectionEnvelope.MasterKeyIds(xml))
                keys[id] = keys.GetValueOrDefault(id) + 1;
        }

        return secrets.Keys.Union(keys.Keys).Order(StringComparer.Ordinal)
            .Select(id => new MasterKeyUsage(id, secrets.GetValueOrDefault(id), keys.GetValueOrDefault(id)))
            .ToList();
    }
}

/// <summary>
/// Stops the start when the database holds rows under a master key that is not loaded. An
/// instance that started in that state could read no credential, would sign sessions with new
/// keys and would write new rows under the wrong key.
/// </summary>
public static class MasterKeyGuard
{
    /// <exception cref="BootstrapException">A row is under a key that is not loaded.</exception>
    public static void EnsureLoaded(MasterKeyRing keys, IReadOnlyList<MasterKeyUsage> inUse)
    {
        var missing = inUse.Where(u => !keys.IsLoaded(u.MasterKeyId)).ToList();
        if (missing.Count == 0)
            return;

        throw new BootstrapException(
            $"The database holds rows that are encrypted under a master key that is not loaded: {Describe(missing)}. " +
            $"The loaded keys are: {string.Join(", ", keys.LoadedIds)}. Vellum does not start, because it cannot read these rows. " +
            "Start Vellum with the missing key as Vellum__MasterKey. If you are changing the key, keep the new key as Vellum__MasterKey " +
            "and set the missing key as Vellum__PreviousMasterKey. " +
            "If the key is lost and you accept the loss of these rows, run 'dotnet Vellum.dll keys forget --yes' with the same environment and start again.");
    }

    /// <summary>For example: <c>key 0123456789abcdef (2 stored credentials, 1 session key)</c>.</summary>
    public static string Describe(IEnumerable<MasterKeyUsage> usage) => string.Join("; ", usage.Select(u =>
        $"key {u.MasterKeyId} ({u.Secrets} stored credential{(u.Secrets == 1 ? "" : "s")}, {u.DataProtectionKeys} session key{(u.DataProtectionKeys == 1 ? "" : "s")})"));
}

/// <summary>
/// <c>dotnet Vellum.dll keys forget --yes</c>: deletes every row that is encrypted under a
/// master key that the environment does not give. It is the one way to start again after a
/// master key is lost, and it never runs by itself.
/// </summary>
public static class KeysCommand
{
    public const string ForgottenAction = "keys.forgotten";

    public static bool Matches(string[] args) => args is ["keys", ..];

    /// <returns>The process exit code: 0 done or nothing to do, 1 not confirmed, 2 wrong arguments.</returns>
    public static async Task<int> RunAsync(string[] args, IConfiguration configuration, TextWriter output, CancellationToken ct = default)
    {
        if (args is not (["keys", "forget"] or ["keys", "forget", "--yes"]))
        {
            await output.WriteLineAsync("Usage: dotnet Vellum.dll keys forget [--yes]");
            return 2;
        }

        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new BootstrapException("ConnectionStrings__DefaultConnection is not set.");
        var loaded = Bootstrap.AvailableKeyIds(configuration);
        var orphans = (await KeyInventory.ReadAsync(connectionString, ct)).Where(u => !loaded.Contains(u.MasterKeyId)).ToList();

        await output.WriteLineAsync(loaded.Count == 0
            ? "The environment gives no master key."
            : $"The environment gives these master keys: {string.Join(", ", loaded)}.");
        if (orphans.Count == 0)
        {
            await output.WriteLineAsync("No row is encrypted under another key. Nothing to forget.");
            return 0;
        }

        await output.WriteLineAsync($"Rows under keys that the environment does not give: {MasterKeyGuard.Describe(orphans)}.");
        if (args.Length < 3)
        {
            await output.WriteLineAsync(
                "Nothing was deleted. These rows cannot be read again after they are deleted. " +
                "Every user must sign in again and every listed credential must be entered again. Run the command with --yes to delete them.");
            return 1;
        }

        var ids = orphans.Select(o => o.MasterKeyId).ToArray();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Each workspace sees in its audit log which of its credentials were deleted.
        await using (var secrets = new NpgsqlCommand("""
            WITH deleted AS (
                DELETE FROM settings.secrets WHERE master_key_id = ANY(@ids) RETURNING workspace_id, name)
            INSERT INTO settings.audit_entries (workspace_id, actor_id, action, target)
            SELECT workspace_id, 'operator', 'secret.deleted', name FROM deleted ORDER BY workspace_id, name
            """, connection, transaction))
        {
            secrets.Parameters.AddWithValue("ids", ids);
            await secrets.ExecuteNonQueryAsync(ct);
        }

        var rows = await KeyInventory.DataProtectionRowsUnderAsync(connection, transaction, ids, ct);
        await using (var sessionKeys = new NpgsqlCommand("DELETE FROM settings.data_protection_keys WHERE id = ANY(@rows)", connection, transaction))
        {
            sessionKeys.Parameters.AddWithValue("rows", rows.ToArray());
            await sessionKeys.ExecuteNonQueryAsync(ct);
        }

        foreach (var orphan in orphans)
        {
            await using var audit = new NpgsqlCommand(
                "INSERT INTO settings.audit_entries (workspace_id, actor_id, action, target) VALUES (NULL, 'operator', @action, @target)",
                connection, transaction);
            audit.Parameters.AddWithValue("action", ForgottenAction);
            audit.Parameters.AddWithValue("target",
                $"master-key:{orphan.MasterKeyId} secrets:{orphan.Secrets} data-protection-keys:{orphan.DataProtectionKeys}");
            await audit.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        await output.WriteLineAsync("The rows were deleted. Start Vellum again.");
        return 0;
    }
}
