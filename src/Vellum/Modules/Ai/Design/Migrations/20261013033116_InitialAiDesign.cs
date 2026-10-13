using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Modules.Ai.Design.Migrations
{
    /// <inheritdoc />
    public partial class InitialAiDesign : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ai");

            migrationBuilder.CreateTable(
                name: "design_proposals",
                schema: "ai",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    request = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    document = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_design_proposals", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_design_proposals_project_id_kind_created_at_id",
                schema: "ai",
                table: "design_proposals",
                columns: new[] { "project_id", "kind", "created_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "design_proposals",
                schema: "ai");
        }
    }
}
