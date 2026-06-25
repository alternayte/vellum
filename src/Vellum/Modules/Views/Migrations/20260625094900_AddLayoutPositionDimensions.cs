using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Modules.Views.Migrations
{
    /// <inheritdoc />
    public partial class AddLayoutPositionDimensions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "height",
                schema: "views",
                table: "layout_positions",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "width",
                schema: "views",
                table: "layout_positions",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "height",
                schema: "views",
                table: "layout_positions");

            migrationBuilder.DropColumn(
                name: "width",
                schema: "views",
                table: "layout_positions");
        }
    }
}
