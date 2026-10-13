using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Modules.Modelling.Migrations
{
    /// <inheritdoc />
    public partial class AddStaleMarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "stale_reason",
                schema: "modelling",
                table: "relationships",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stale_since_revision",
                schema: "modelling",
                table: "relationships",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stale_reason",
                schema: "modelling",
                table: "messages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stale_since_revision",
                schema: "modelling",
                table: "messages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stale_reason",
                schema: "modelling",
                table: "elements",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stale_since_revision",
                schema: "modelling",
                table: "elements",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "stale_reason",
                schema: "modelling",
                table: "relationships");

            migrationBuilder.DropColumn(
                name: "stale_since_revision",
                schema: "modelling",
                table: "relationships");

            migrationBuilder.DropColumn(
                name: "stale_reason",
                schema: "modelling",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "stale_since_revision",
                schema: "modelling",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "stale_reason",
                schema: "modelling",
                table: "elements");

            migrationBuilder.DropColumn(
                name: "stale_since_revision",
                schema: "modelling",
                table: "elements");
        }
    }
}
