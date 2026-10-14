using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Modules.Import.Migrations
{
    /// <inheritdoc />
    public partial class ScanPullRequest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "pull_request",
                schema: "import",
                table: "scans",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "pull_request_number",
                schema: "import",
                table: "scans",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pull_request",
                schema: "import",
                table: "scans");

            migrationBuilder.DropColumn(
                name: "pull_request_number",
                schema: "import",
                table: "scans");
        }
    }
}
