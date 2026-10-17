using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Migrations
{
    /// <inheritdoc />
    public partial class LayoutLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "layout_lines",
                schema: "views",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    view_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_id = table.Column<Guid>(type: "uuid", nullable: false),
                    route = table.Column<JsonDocument>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_layout_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_layout_lines_views_view_id",
                        column: x => x.view_id,
                        principalSchema: "views",
                        principalTable: "views",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_layout_lines_view_id_from_id_to_id",
                schema: "views",
                table: "layout_lines",
                columns: new[] { "view_id", "from_id", "to_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "layout_lines",
                schema: "views");
        }
    }
}
