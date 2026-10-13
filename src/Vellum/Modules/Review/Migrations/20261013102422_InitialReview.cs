using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Modules.Review.Migrations
{
    /// <inheritdoc />
    public partial class InitialReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "review");

            migrationBuilder.CreateTable(
                name: "critiques",
                schema: "review",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    question = table.Column<string>(type: "text", nullable: false),
                    document = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_critiques", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "decision_numbering",
                schema: "review",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    next_number = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_decision_numbering", x => x.project_id);
                });

            migrationBuilder.CreateTable(
                name: "decisions",
                schema: "review",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    document = table.Column<string>(type: "jsonb", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_decisions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "dispositions",
                schema: "review",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_version = table.Column<int>(type: "integer", nullable: false),
                    fingerprint = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    decided_by = table.Column<string>(type: "text", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dispositions", x => new { x.project_id, x.rule_id, x.subject_id });
                });

            migrationBuilder.CreateTable(
                name: "questions",
                schema: "review",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    about = table.Column<string>(type: "jsonb", nullable: false),
                    origin = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    answer = table.Column<string>(type: "text", nullable: true),
                    answered_by = table.Column<string>(type: "text", nullable: true),
                    answered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_questions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "snapshots",
                schema: "review",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch = table.Column<Guid>(type: "uuid", nullable: false),
                    model_version = table.Column<long>(type: "bigint", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    inputs_fingerprint = table.Column<string>(type: "text", nullable: false),
                    rule_set_version = table.Column<string>(type: "text", nullable: false),
                    hash = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    findings = table.Column<string>(type: "jsonb", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_snapshots", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_critiques_project_id_created_at_id",
                schema: "review",
                table: "critiques",
                columns: new[] { "project_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_decisions_project_id_number",
                schema: "review",
                table: "decisions",
                columns: new[] { "project_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_questions_project_id_created_at_id",
                schema: "review",
                table: "questions",
                columns: new[] { "project_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_snapshots_project_id_created_at_id",
                schema: "review",
                table: "snapshots",
                columns: new[] { "project_id", "created_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "critiques",
                schema: "review");

            migrationBuilder.DropTable(
                name: "decision_numbering",
                schema: "review");

            migrationBuilder.DropTable(
                name: "decisions",
                schema: "review");

            migrationBuilder.DropTable(
                name: "dispositions",
                schema: "review");

            migrationBuilder.DropTable(
                name: "questions",
                schema: "review");

            migrationBuilder.DropTable(
                name: "snapshots",
                schema: "review");
        }
    }
}
