using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Modules.LoadTests.Migrations
{
    /// <inheritdoc />
    public partial class ResultRunKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_results_script_id",
                schema: "loadtests",
                table: "results");

            migrationBuilder.AddColumn<double>(
                name: "elapsed_ms",
                schema: "loadtests",
                table: "results",
                type: "double precision",
                nullable: true);

            // A stored result gets the length that its document states. When two stored rows are
            // the same run (the result file and the summary export of one run), the first one
            // imported gets the length and the other one keeps null, so the unique index below
            // is created on any data and no row is removed.
            migrationBuilder.Sql("""
                UPDATE loadtests.results r
                SET elapsed_ms = (r.document->>'elapsedMs')::double precision
                WHERE r.id = (
                    SELECT o.id FROM loadtests.results o
                    WHERE o.script_id = r.script_id AND o.started_at = r.started_at
                      AND o.document->>'elapsedMs' = r.document->>'elapsedMs'
                    ORDER BY o.imported_at, o.id
                    LIMIT 1);
                """);

            migrationBuilder.CreateIndex(
                name: "ix_results_script_id_started_at_elapsed_ms",
                schema: "loadtests",
                table: "results",
                columns: new[] { "script_id", "started_at", "elapsed_ms" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_results_script_id_started_at_elapsed_ms",
                schema: "loadtests",
                table: "results");

            migrationBuilder.DropColumn(
                name: "elapsed_ms",
                schema: "loadtests",
                table: "results");

            migrationBuilder.CreateIndex(
                name: "ix_results_script_id",
                schema: "loadtests",
                table: "results",
                column: "script_id");
        }
    }
}
