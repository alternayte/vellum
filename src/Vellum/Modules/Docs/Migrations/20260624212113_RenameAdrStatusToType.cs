using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Modules.Docs.Migrations
{
    /// <inheritdoc />
    public partial class RenameAdrStatusToType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "type",
                schema: "docs",
                table: "documents",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE docs.documents SET type = 'adr' WHERE adr_status IS NOT NULL");

            migrationBuilder.DropColumn(
                name: "adr_status",
                schema: "docs",
                table: "documents");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "adr_status",
                schema: "docs",
                table: "documents",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE docs.documents SET adr_status = 'proposed' WHERE type = 'adr'");

            migrationBuilder.DropColumn(
                name: "type",
                schema: "docs",
                table: "documents");
        }
    }
}
