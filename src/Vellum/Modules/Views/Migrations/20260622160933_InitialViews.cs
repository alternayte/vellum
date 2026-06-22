using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Modules.Views.Migrations
{
    /// <inheritdoc />
    public partial class InitialViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "views");

            migrationBuilder.CreateTable(
                name: "layout_edges",
                schema: "views",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    view_id = table.Column<Guid>(type: "uuid", nullable: false),
                    relationship_id = table.Column<Guid>(type: "uuid", nullable: false),
                    route_points = table.Column<JsonDocument>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_layout_edges", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "layout_positions",
                schema: "views",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    view_id = table.Column<Guid>(type: "uuid", nullable: false),
                    element_id = table.Column<Guid>(type: "uuid", nullable: false),
                    x = table.Column<double>(type: "double precision", nullable: false),
                    y = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_layout_positions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "views",
                schema: "views",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    root_element_id = table.Column<Guid>(type: "uuid", nullable: true),
                    visible_element_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    active_lens = table.Column<string>(type: "text", nullable: true),
                    active_flow_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_views", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_layout_edges_view_id_relationship_id",
                schema: "views",
                table: "layout_edges",
                columns: new[] { "view_id", "relationship_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_layout_positions_view_id_element_id",
                schema: "views",
                table: "layout_positions",
                columns: new[] { "view_id", "element_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_views_project_id",
                schema: "views",
                table: "views",
                column: "project_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "layout_edges",
                schema: "views");

            migrationBuilder.DropTable(
                name: "layout_positions",
                schema: "views");

            migrationBuilder.DropTable(
                name: "views",
                schema: "views");
        }
    }
}
