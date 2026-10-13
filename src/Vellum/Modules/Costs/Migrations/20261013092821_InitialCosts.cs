using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Modules.Costs.Migrations
{
    /// <inheritdoc />
    public partial class InitialCosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "costs");

            migrationBuilder.CreateTable(
                name: "estimates",
                schema: "costs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    model_version = table.Column<long>(type: "bigint", nullable: false),
                    as_of = table.Column<DateOnly>(type: "date", nullable: false),
                    inputs_fingerprint = table.Column<string>(type: "text", nullable: false),
                    hash = table.Column<string>(type: "text", nullable: false),
                    inputs = table.Column<string>(type: "jsonb", nullable: false),
                    result = table.Column<string>(type: "jsonb", nullable: false),
                    saved_by = table.Column<string>(type: "text", nullable: false),
                    saved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_estimates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pins",
                schema: "costs",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    price_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pinned_by = table.Column<string>(type: "text", nullable: false),
                    pinned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pins", x => new { x.project_id, x.price_entry_id });
                });

            migrationBuilder.CreateTable(
                name: "price_entries",
                schema: "costs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    region = table.Column<string>(type: "text", nullable: false),
                    sku = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    unit = table.Column<string>(type: "text", nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    accepted_by = table.Column<string>(type: "text", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_url = table.Column<string>(type: "text", nullable: true),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    meter_id = table.Column<string>(type: "text", nullable: true),
                    provider_effective_date = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_price_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sheets",
                schema: "costs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sheets", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_estimates_project_id_branch_id_saved_at",
                schema: "costs",
                table: "estimates",
                columns: new[] { "project_id", "branch_id", "saved_at" });

            migrationBuilder.CreateIndex(
                name: "ix_pins_price_entry_id",
                schema: "costs",
                table: "pins",
                column: "price_entry_id");

            migrationBuilder.CreateIndex(
                name: "ix_price_entries_workspace_id_provider_sku_id",
                schema: "costs",
                table: "price_entries",
                columns: new[] { "workspace_id", "provider", "sku", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_sheets_project_id_branch_id",
                schema: "costs",
                table: "sheets",
                columns: new[] { "project_id", "branch_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "estimates",
                schema: "costs");

            migrationBuilder.DropTable(
                name: "pins",
                schema: "costs");

            migrationBuilder.DropTable(
                name: "price_entries",
                schema: "costs");

            migrationBuilder.DropTable(
                name: "sheets",
                schema: "costs");
        }
    }
}
