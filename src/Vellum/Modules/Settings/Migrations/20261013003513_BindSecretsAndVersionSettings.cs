using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Modules.Settings.Migrations
{
    /// <inheritdoc />
    public partial class BindSecretsAndVersionSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "version",
                schema: "settings",
                table: "workspace_settings",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<string>(
                name: "bound_origin",
                schema: "settings",
                table: "secrets",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "version",
                schema: "settings",
                table: "instance_settings",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "version",
                schema: "settings",
                table: "workspace_settings");

            migrationBuilder.DropColumn(
                name: "bound_origin",
                schema: "settings",
                table: "secrets");

            migrationBuilder.DropColumn(
                name: "version",
                schema: "settings",
                table: "instance_settings");
        }
    }
}
