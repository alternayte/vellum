using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Migrations
{
    /// <inheritdoc />
    public partial class LayoutEdgeSides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "source_side",
                schema: "views",
                table: "layout_edges",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "target_side",
                schema: "views",
                table: "layout_edges",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "source_side",
                schema: "views",
                table: "layout_edges");

            migrationBuilder.DropColumn(
                name: "target_side",
                schema: "views",
                table: "layout_edges");
        }
    }
}
