using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Modules.Trace.Scenarios.Migrations
{
    /// <inheritdoc />
    public partial class InitialScenarios : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "trace");

            migrationBuilder.CreateTable(
                name: "scenarios",
                schema: "trace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scenarios", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_scenarios_project_id_name",
                schema: "trace",
                table: "scenarios",
                columns: new[] { "project_id", "name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scenarios",
                schema: "trace");
        }
    }
}
