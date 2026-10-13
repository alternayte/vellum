using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Vellum.Modules.Settings.Migrations
{
    /// <inheritdoc />
    public partial class InitialSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "settings");

            migrationBuilder.CreateTable(
                name: "audit_entries",
                schema: "settings",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    target = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "data_protection_keys",
                schema: "settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    friendly_name = table.Column<string>(type: "text", nullable: true),
                    xml = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_protection_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "instance_admins",
                schema: "settings",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "text", nullable: false),
                    granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_instance_admins", x => x.user_id);
                });

            migrationBuilder.CreateTable(
                name: "instance_settings",
                schema: "settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    policy = table.Column<string>(type: "jsonb", nullable: true),
                    defaults = table.Column<string>(type: "jsonb", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_instance_settings", x => x.id);
                    table.CheckConstraint("ck_instance_settings_singleton", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "secrets",
                schema: "settings",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    master_key_id = table.Column<string>(type: "text", nullable: false),
                    wrap_nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    wrapped_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    fingerprint = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_secrets", x => new { x.workspace_id, x.name });
                });

            migrationBuilder.CreateTable(
                name: "workspace_settings",
                schema: "settings",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    overrides = table.Column<string>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workspace_settings", x => x.workspace_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_workspace_id_id",
                schema: "settings",
                table: "audit_entries",
                columns: new[] { "workspace_id", "id" });

            // The audit log is append-only in the database, not only in the app.
            migrationBuilder.Sql("""
                CREATE FUNCTION settings.audit_entries_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'settings.audit_entries is append-only';
                END $$;

                CREATE TRIGGER audit_entries_no_change BEFORE UPDATE OR DELETE ON settings.audit_entries
                    FOR EACH ROW EXECUTE FUNCTION settings.audit_entries_append_only();

                CREATE TRIGGER audit_entries_no_truncate BEFORE TRUNCATE ON settings.audit_entries
                    FOR EACH STATEMENT EXECUTE FUNCTION settings.audit_entries_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS settings.audit_entries_append_only() CASCADE;");

            migrationBuilder.DropTable(
                name: "audit_entries",
                schema: "settings");

            migrationBuilder.DropTable(
                name: "data_protection_keys",
                schema: "settings");

            migrationBuilder.DropTable(
                name: "instance_admins",
                schema: "settings");

            migrationBuilder.DropTable(
                name: "instance_settings",
                schema: "settings");

            migrationBuilder.DropTable(
                name: "secrets",
                schema: "settings");

            migrationBuilder.DropTable(
                name: "workspace_settings",
                schema: "settings");
        }
    }
}
