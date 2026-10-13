using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Modules.Import.Migrations
{
    /// <inheritdoc />
    public partial class InitialImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "import");

            migrationBuilder.CreateTable(
                name: "blobs",
                schema: "import",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    blob_sha = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: true),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    withheld_reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_blobs", x => new { x.workspace_id, x.blob_sha });
                });

            migrationBuilder.CreateTable(
                name: "fact_links",
                schema: "import",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    host = table.Column<string>(type: "text", nullable: false),
                    repository = table.Column<string>(type: "text", nullable: false),
                    fact_key = table.Column<string>(type: "text", nullable: false),
                    entity_kind = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    commit_sha = table.Column<string>(type: "text", nullable: false),
                    attributes = table.Column<string>(type: "jsonb", nullable: false),
                    accepted = table.Column<string>(type: "jsonb", nullable: false),
                    evidence = table.Column<string>(type: "jsonb", nullable: false),
                    accepted_by = table.Column<string>(type: "text", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fact_links", x => new { x.project_id, x.host, x.repository, x.fact_key });
                });

            migrationBuilder.CreateTable(
                name: "rejections",
                schema: "import",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    host = table.Column<string>(type: "text", nullable: false),
                    repository = table.Column<string>(type: "text", nullable: false),
                    fact_key = table.Column<string>(type: "text", nullable: false),
                    fingerprint = table.Column<string>(type: "text", nullable: false),
                    scan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rejected_by = table.Column<string>(type: "text", nullable: false),
                    rejected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rejections", x => new { x.project_id, x.host, x.repository, x.fact_key, x.fingerprint });
                });

            migrationBuilder.CreateTable(
                name: "scans",
                schema: "import",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    host = table.Column<string>(type: "text", nullable: false),
                    repository = table.Column<string>(type: "text", nullable: false),
                    requested_ref = table.Column<string>(type: "text", nullable: true),
                    commit_sha = table.Column<string>(type: "text", nullable: true),
                    repository_is_private = table.Column<bool>(type: "boolean", nullable: true),
                    include_paths = table.Column<string[]>(type: "text[]", nullable: false),
                    exclude_paths = table.Column<string[]>(type: "text[]", nullable: false),
                    limits = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    tree_listed = table.Column<bool>(type: "boolean", nullable: false),
                    files_listed = table.Column<int>(type: "integer", nullable: false),
                    files_read = table.Column<int>(type: "integer", nullable: false),
                    files_skipped = table.Column<int>(type: "integer", nullable: false),
                    bytes_read = table.Column<long>(type: "bigint", nullable: false),
                    partial = table.Column<bool>(type: "boolean", nullable: false),
                    is_complete = table.Column<bool>(type: "boolean", nullable: false),
                    notes = table.Column<string>(type: "jsonb", nullable: false),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    retry_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    requested_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    lease_owner = table.Column<string>(type: "text", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancel_requested = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scans", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "proposals",
                schema: "import",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    entity_kind = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fact_key = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    proposed = table.Column<string>(type: "jsonb", nullable: true),
                    current = table.Column<string>(type: "jsonb", nullable: true),
                    changes = table.Column<string>(type: "jsonb", nullable: false),
                    evidence = table.Column<string>(type: "jsonb", nullable: false),
                    attributes = table.Column<string>(type: "jsonb", nullable: false),
                    depends_on = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    partial = table.Column<bool>(type: "boolean", nullable: false),
                    fingerprint = table.Column<string>(type: "text", nullable: false),
                    decided_by = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_proposals", x => x.id);
                    table.ForeignKey(
                        name: "fk_proposals_scans_scan_id",
                        column: x => x.scan_id,
                        principalSchema: "import",
                        principalTable: "scans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "scan_files",
                schema: "import",
                columns: table => new
                {
                    scan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    blob_sha = table.Column<string>(type: "text", nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scan_files", x => new { x.scan_id, x.path });
                    table.ForeignKey(
                        name: "fk_scan_files_scans_scan_id",
                        column: x => x.scan_id,
                        principalSchema: "import",
                        principalTable: "scans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "snapshots",
                schema: "import",
                columns: table => new
                {
                    scan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    host = table.Column<string>(type: "text", nullable: false),
                    repository = table.Column<string>(type: "text", nullable: false),
                    commit_sha = table.Column<string>(type: "text", nullable: false),
                    is_complete = table.Column<bool>(type: "boolean", nullable: false),
                    rule_versions = table.Column<string>(type: "jsonb", nullable: false),
                    limits = table.Column<string>(type: "jsonb", nullable: false),
                    fact_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_snapshots", x => x.scan_id);
                    table.ForeignKey(
                        name: "fk_snapshots_scans_scan_id",
                        column: x => x.scan_id,
                        principalSchema: "import",
                        principalTable: "scans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "snapshot_facts",
                schema: "import",
                columns: table => new
                {
                    scan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    fact_key = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    attributes = table.Column<string>(type: "jsonb", nullable: false),
                    evidence = table.Column<string>(type: "jsonb", nullable: false),
                    disposition = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_snapshot_facts", x => new { x.scan_id, x.seq });
                    table.ForeignKey(
                        name: "fk_snapshot_facts_snapshots_scan_id",
                        column: x => x.scan_id,
                        principalSchema: "import",
                        principalTable: "snapshots",
                        principalColumn: "scan_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_fact_links_project_id_entity_id",
                schema: "import",
                table: "fact_links",
                columns: new[] { "project_id", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_proposals_scan_id_seq",
                schema: "import",
                table: "proposals",
                columns: new[] { "scan_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_proposals_scan_id_status",
                schema: "import",
                table: "proposals",
                columns: new[] { "scan_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_scan_files_blob_sha",
                schema: "import",
                table: "scan_files",
                column: "blob_sha");

            migrationBuilder.CreateIndex(
                name: "ix_scan_files_scan_id_state",
                schema: "import",
                table: "scan_files",
                columns: new[] { "scan_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_scans_one_active_per_repository",
                schema: "import",
                table: "scans",
                columns: new[] { "project_id", "host", "repository" },
                unique: true,
                filter: "status IN ('queued', 'running', 'waiting')");

            migrationBuilder.CreateIndex(
                name: "ix_scans_project_id_created_at_id",
                schema: "import",
                table: "scans",
                columns: new[] { "project_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_scans_status_created_at",
                schema: "import",
                table: "scans",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_scans_workspace_id_created_at",
                schema: "import",
                table: "scans",
                columns: new[] { "workspace_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_snapshot_facts_scan_id_disposition_seq",
                schema: "import",
                table: "snapshot_facts",
                columns: new[] { "scan_id", "disposition", "seq" });

            migrationBuilder.CreateIndex(
                name: "ix_snapshots_project_id_host_repository_created_at",
                schema: "import",
                table: "snapshots",
                columns: new[] { "project_id", "host", "repository", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "blobs",
                schema: "import");

            migrationBuilder.DropTable(
                name: "fact_links",
                schema: "import");

            migrationBuilder.DropTable(
                name: "proposals",
                schema: "import");

            migrationBuilder.DropTable(
                name: "rejections",
                schema: "import");

            migrationBuilder.DropTable(
                name: "scan_files",
                schema: "import");

            migrationBuilder.DropTable(
                name: "snapshot_facts",
                schema: "import");

            migrationBuilder.DropTable(
                name: "snapshots",
                schema: "import");

            migrationBuilder.DropTable(
                name: "scans",
                schema: "import");
        }
    }
}
