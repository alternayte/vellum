using Deedbox;
using Npgsql;

namespace Vellum.Shared;

/// <summary>
/// The database objects that are not in the EF Core model: the Deedbox event-store tables in
/// the schema <c>deedbox</c>, and the trigger that keeps <c>settings.audit_entries</c>
/// append-only. They are applied after every migrate, so a new migration
/// (<c>just migration</c>) never loses them. Every statement is idempotent; the
/// Deedbox script records its version in <c>deedbox.schema_version</c>.
/// </summary>
public static class DatabaseObjects
{
    private const string AuditTrigger = """
        CREATE OR REPLACE FUNCTION settings.audit_entries_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'settings.audit_entries is append-only';
        END $$;

        CREATE OR REPLACE TRIGGER audit_entries_no_change BEFORE UPDATE OR DELETE ON settings.audit_entries
            FOR EACH ROW EXECUTE FUNCTION settings.audit_entries_append_only();

        CREATE OR REPLACE TRIGGER audit_entries_no_truncate BEFORE TRUNCATE ON settings.audit_entries
            FOR EACH STATEMENT EXECUTE FUNCTION settings.audit_entries_append_only();
        """;

    public static async Task EnsureAsync(string connectionString, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        foreach (var sql in new[] { PostgresSchema.Script(fromVersion: 0), AuditTrigger })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(ct);
        }
    }
}
