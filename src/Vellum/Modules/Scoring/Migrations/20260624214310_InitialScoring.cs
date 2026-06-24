using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Modules.Scoring.Migrations
{
    /// <inheritdoc />
    public partial class InitialScoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "scoring");

            migrationBuilder.CreateTable(
                name: "scores",
                schema: "scoring",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    doc_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    doc_type = table.Column<string>(type: "text", nullable: false),
                    overall_score = table.Column<decimal>(type: "numeric", nullable: false),
                    criteria_results_json = table.Column<string>(type: "jsonb", nullable: false),
                    suggested_content = table.Column<string>(type: "text", nullable: true),
                    scored_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scores", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_scores_doc_id",
                schema: "scoring",
                table: "scores",
                column: "doc_id");

            migrationBuilder.CreateIndex(
                name: "ix_scores_project_id",
                schema: "scoring",
                table: "scores",
                column: "project_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scores",
                schema: "scoring");
        }
    }
}
