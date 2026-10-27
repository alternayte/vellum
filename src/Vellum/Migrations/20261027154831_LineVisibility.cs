using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Migrations
{
    /// <inheritdoc />
    public partial class LineVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "line_visibility",
                schema: "views",
                columns: table => new
                {
                    view_id = table.Column<Guid>(type: "uuid", nullable: false),
                    derived_lines = table.Column<bool>(type: "boolean", nullable: false),
                    structural = table.Column<bool>(type: "boolean", nullable: false),
                    behavioural = table.Column<bool>(type: "boolean", nullable: false),
                    cross_level = table.Column<bool>(type: "boolean", nullable: false),
                    hidden_relationship_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    hidden_derived_lines = table.Column<JsonDocument>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_line_visibility", x => x.view_id);
                    table.ForeignKey(
                        name: "fk_line_visibility_views_view_id",
                        column: x => x.view_id,
                        principalSchema: "views",
                        principalTable: "views",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "line_visibility",
                schema: "views");
        }
    }
}
