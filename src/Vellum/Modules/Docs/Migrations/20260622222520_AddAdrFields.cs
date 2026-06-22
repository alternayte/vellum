using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Modules.Docs.Migrations
{
    /// <inheritdoc />
    public partial class AddAdrFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "adr_status",
                schema: "docs",
                table: "documents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "draft_id",
                schema: "docs",
                table: "documents",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "adr_status",
                schema: "docs",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "draft_id",
                schema: "docs",
                table: "documents");
        }
    }
}
