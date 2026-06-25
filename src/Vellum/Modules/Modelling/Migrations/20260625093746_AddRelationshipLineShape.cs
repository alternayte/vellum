using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Modules.Modelling.Migrations
{
    /// <inheritdoc />
    public partial class AddRelationshipLineShape : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "line_shape",
                schema: "modelling",
                table: "relationships",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "line_shape",
                schema: "modelling",
                table: "relationships");
        }
    }
}
