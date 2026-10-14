using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Modules.Workspaces.Migrations
{
    /// <inheritdoc />
    public partial class InvitationSecret : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "secret_hash",
                schema: "workspaces",
                table: "invitations",
                type: "bytea",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "secret_hash",
                schema: "workspaces",
                table: "invitations");
        }
    }
}
