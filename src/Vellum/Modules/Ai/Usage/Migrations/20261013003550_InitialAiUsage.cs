using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Modules.Ai.Usage.Migrations
{
    /// <inheritdoc />
    public partial class InitialAiUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ai");

            migrationBuilder.CreateTable(
                name: "model_prices",
                schema: "ai",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    input_usd_per_million_tokens = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    output_usd_per_million_tokens = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    effective_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_prices", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "usage_records",
                schema: "ai",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reported_model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    task = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    purpose = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    prompt_template_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    input_tokens = table.Column<int>(type: "integer", nullable: true),
                    output_tokens = table.Column<int>(type: "integer", nullable: true),
                    cost_usd = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    reserved_usd = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: true),
                    outcome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    error_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_records", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_model_prices_provider_kind_model_effective_from",
                schema: "ai",
                table: "model_prices",
                columns: new[] { "provider_kind", "model", "effective_from" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_usage_records_workspace_id_provider_id_started_at",
                schema: "ai",
                table: "usage_records",
                columns: new[] { "workspace_id", "provider_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_usage_records_workspace_id_started_at",
                schema: "ai",
                table: "usage_records",
                columns: new[] { "workspace_id", "started_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_prices",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "usage_records",
                schema: "ai");
        }
    }
}
