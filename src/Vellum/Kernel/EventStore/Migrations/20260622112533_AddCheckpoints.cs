using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Kernel.EventStore.Migrations
{
    /// <inheritdoc />
    public partial class AddCheckpoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "checkpoints",
                schema: "es",
                columns: table => new
                {
                    projection_name = table.Column<string>(type: "text", nullable: false),
                    last_processed_position = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_checkpoints", x => x.projection_name);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "checkpoints",
                schema: "es");
        }
    }
}
