using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Modules.Promises.Migrations
{
    /// <inheritdoc />
    public partial class InitialPromises : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "promises");

            migrationBuilder.CreateTable(
                name: "numbering",
                schema: "promises",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    prefix = table.Column<string>(type: "text", nullable: false),
                    next_number = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_numbering", x => x.project_id);
                });

            migrationBuilder.CreateTable(
                name: "promises",
                schema: "promises",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requirement_id = table.Column<string>(type: "text", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    lifecycle = table.Column<string>(type: "text", nullable: false),
                    origin = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    document = table.Column<string>(type: "jsonb", nullable: false),
                    latest_runs = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_promises", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "runs",
                schema: "promises",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    promise_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    obligation_id = table.Column<string>(type: "text", nullable: false),
                    promise_revision = table.Column<int>(type: "integer", nullable: false),
                    result = table.Column<string>(type: "text", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    document = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_runs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_promises_project_id_number",
                schema: "promises",
                table: "promises",
                columns: new[] { "project_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_runs_promise_id_version",
                schema: "promises",
                table: "runs",
                columns: new[] { "promise_id", "version" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "numbering",
                schema: "promises");

            migrationBuilder.DropTable(
                name: "promises",
                schema: "promises");

            migrationBuilder.DropTable(
                name: "runs",
                schema: "promises");
        }
    }
}
