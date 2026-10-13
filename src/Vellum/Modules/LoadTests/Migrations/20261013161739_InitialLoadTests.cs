using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Modules.LoadTests.Migrations
{
    /// <inheritdoc />
    public partial class InitialLoadTests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "loadtests");

            migrationBuilder.CreateTable(
                name: "scripts",
                schema: "loadtests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hash = table.Column<string>(type: "text", nullable: false),
                    scenario_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scenario_version = table.Column<long>(type: "bigint", nullable: false),
                    model_stream_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_version = table.Column<long>(type: "bigint", nullable: false),
                    profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    profile_version = table.Column<int>(type: "integer", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_name = table.Column<string>(type: "text", nullable: false),
                    base_url = table.Column<string>(type: "text", nullable: false),
                    environment = table.Column<string>(type: "text", nullable: false),
                    authorised_by = table.Column<string>(type: "text", nullable: false),
                    authorised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    plan = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scripts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "targets",
                schema: "loadtests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    removed = table.Column<bool>(type: "boolean", nullable: false),
                    document = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_targets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "results",
                schema: "loadtests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    script_id = table.Column<Guid>(type: "uuid", nullable: false),
                    script_hash = table.Column<string>(type: "text", nullable: false),
                    scenario_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scenario_version = table.Column<long>(type: "bigint", nullable: false),
                    model_stream_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_version = table.Column<long>(type: "bigint", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment = table.Column<string>(type: "text", nullable: false),
                    format = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    document = table.Column<string>(type: "jsonb", nullable: false),
                    imported_by = table.Column<string>(type: "text", nullable: false),
                    imported_via = table.Column<string>(type: "text", nullable: true),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_results", x => x.id);
                    table.ForeignKey(
                        name: "fk_results_scripts_script_id",
                        column: x => x.script_id,
                        principalSchema: "loadtests",
                        principalTable: "scripts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_results_project_id_content_hash",
                schema: "loadtests",
                table: "results",
                columns: new[] { "project_id", "content_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_results_project_id_scenario_id_target_id_started_at",
                schema: "loadtests",
                table: "results",
                columns: new[] { "project_id", "scenario_id", "target_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_results_script_id",
                schema: "loadtests",
                table: "results",
                column: "script_id");

            migrationBuilder.CreateIndex(
                name: "ix_scripts_project_id_created_at",
                schema: "loadtests",
                table: "scripts",
                columns: new[] { "project_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_scripts_project_id_hash",
                schema: "loadtests",
                table: "scripts",
                columns: new[] { "project_id", "hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_targets_project_id_name",
                schema: "loadtests",
                table: "targets",
                columns: new[] { "project_id", "name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "results",
                schema: "loadtests");

            migrationBuilder.DropTable(
                name: "targets",
                schema: "loadtests");

            migrationBuilder.DropTable(
                name: "scripts",
                schema: "loadtests");
        }
    }
}
