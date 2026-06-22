using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Modules.Drafts.Migrations
{
    /// <inheritdoc />
    public partial class InitialDrafts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "drafts");

            migrationBuilder.CreateTable(
                name: "drafts",
                schema: "drafts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    stream_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_stream_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fork_version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    merged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    abandoned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    base_snapshot = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_drafts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "comments",
                schema: "drafts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    draft_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    entity_type = table.Column<string>(type: "text", nullable: true),
                    author = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comments", x => x.id);
                    table.ForeignKey(
                        name: "fk_comments_drafts_draft_id",
                        column: x => x.draft_id,
                        principalSchema: "drafts",
                        principalTable: "drafts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_comments_draft_id",
                schema: "drafts",
                table: "comments",
                column: "draft_id");

            migrationBuilder.CreateIndex(
                name: "ix_drafts_project_id",
                schema: "drafts",
                table: "drafts",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_drafts_project_id_status",
                schema: "drafts",
                table: "drafts",
                columns: new[] { "project_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "comments",
                schema: "drafts");

            migrationBuilder.DropTable(
                name: "drafts",
                schema: "drafts");
        }
    }
}
