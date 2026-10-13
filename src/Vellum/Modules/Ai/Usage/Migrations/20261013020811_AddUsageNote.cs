using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Modules.Ai.Usage.Migrations
{
    /// <inheritdoc />
    public partial class AddUsageNote : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "usage_note",
                schema: "ai",
                table: "usage_records",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "usage_note",
                schema: "ai",
                table: "usage_records");
        }
    }
}
