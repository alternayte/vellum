using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Vellum.Kernel.EventStore.Migrations
{
    /// <inheritdoc />
    public partial class InitialEventStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "es");

            migrationBuilder.CreateTable(
                name: "events",
                schema: "es",
                columns: table => new
                {
                    stream_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    global_position = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    metadata = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_events", x => new { x.stream_id, x.version });
                });

            migrationBuilder.CreateTable(
                name: "streams",
                schema: "es",
                columns: table => new
                {
                    stream_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stream_type = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_streams", x => x.stream_id);
                });

            migrationBuilder.Sql(
                "ALTER TABLE es.events ADD COLUMN xid xid8 NOT NULL DEFAULT pg_current_xact_id();");

            migrationBuilder.CreateIndex(
                name: "ix_events_global_position",
                schema: "es",
                table: "events",
                column: "global_position",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "events",
                schema: "es");

            migrationBuilder.DropTable(
                name: "streams",
                schema: "es");
        }
    }
}
