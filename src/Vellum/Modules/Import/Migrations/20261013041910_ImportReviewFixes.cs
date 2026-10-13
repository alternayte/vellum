using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Modules.Import.Migrations
{
    /// <inheritdoc />
    public partial class ImportReviewFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // This migration upgrades a database that holds scans, links and proposals. Each
            // SQL step below fills a new column from the rows that are there.

            // A path or a fact key longer than the new bound cannot be kept: the scan and
            // the mapping refuse such text from now on. No row of a real repository has one.
            migrationBuilder.Sql("""
                DELETE FROM import.scan_files WHERE char_length(path) > 1024;
                DELETE FROM import.snapshot_facts WHERE char_length(fact_key) > 1000;
                DELETE FROM import.proposals WHERE char_length(fact_key) > 1000;
                DELETE FROM import.fact_links WHERE char_length(fact_key) > 1000;
                DELETE FROM import.rejections WHERE char_length(fact_key) > 1000;
                """);

            migrationBuilder.DropPrimaryKey(
                name: "pk_scan_files",
                schema: "import",
                table: "scan_files");

            migrationBuilder.DropPrimaryKey(
                name: "pk_rejections",
                schema: "import",
                table: "rejections");

            migrationBuilder.DropPrimaryKey(
                name: "pk_fact_links",
                schema: "import",
                table: "fact_links");

            migrationBuilder.AddColumn<string>(
                name: "rule_fingerprint",
                schema: "import",
                table: "snapshots",
                type: "text",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "fact_key",
                schema: "import",
                table: "snapshot_facts",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "claimed_at",
                schema: "import",
                table: "scans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "coverage",
                schema: "import",
                table: "scans",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "crashes",
                schema: "import",
                table: "scans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "extraction_gap",
                schema: "import",
                table: "scans",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "files_extracted",
                schema: "import",
                table: "scans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "internal_failures",
                schema: "import",
                table: "scans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "lease_token",
                schema: "import",
                table: "scans",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "manual_resumes",
                schema: "import",
                table: "scans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "needs_review",
                schema: "import",
                table: "scans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "relinked",
                schema: "import",
                table: "scans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "repository_id",
                schema: "import",
                table: "scans",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "repository_name",
                schema: "import",
                table: "scans",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "resolved_ref",
                schema: "import",
                table: "scans",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "tracked_at",
                schema: "import",
                table: "scans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tracked_by",
                schema: "import",
                table: "scans",
                type: "text",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "path",
                schema: "import",
                table: "scan_files",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "path_hash",
                schema: "import",
                table: "scan_files",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<string>(
                name: "fact_key",
                schema: "import",
                table: "rejections",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<Guid>(
                name: "id",
                schema: "import",
                table: "rejections",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "fact_key_hash",
                schema: "import",
                table: "rejections",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "repository_id",
                schema: "import",
                table: "rejections",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "subject",
                schema: "import",
                table: "rejections",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "fact");

            migrationBuilder.AlterColumn<string>(
                name: "fact_key",
                schema: "import",
                table: "proposals",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "consumer_keys",
                schema: "import",
                table: "proposals",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "subject",
                schema: "import",
                table: "proposals",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "fact");

            migrationBuilder.AlterColumn<string>(
                name: "fact_key",
                schema: "import",
                table: "fact_links",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<Guid>(
                name: "id",
                schema: "import",
                table: "fact_links",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "consumer_keys",
                schema: "import",
                table: "fact_links",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "fact_key_hash",
                schema: "import",
                table: "fact_links",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "repository_id",
                schema: "import",
                table: "fact_links",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rule_fingerprint",
                schema: "import",
                table: "fact_links",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "subject",
                schema: "import",
                table: "fact_links",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "fact");

            // The keys of the existing rows, before the new primary keys and unique indexes
            // are made. The hash is the one KeyHash computes: SHA-256 of the UTF-8 text, in hex.
            migrationBuilder.Sql("""
                UPDATE import.scan_files SET path_hash = encode(sha256(convert_to(path, 'UTF8')), 'hex');

                UPDATE import.fact_links SET id = gen_random_uuid();
                UPDATE import.rejections SET id = gen_random_uuid();

                -- The repository itself was a link with a made-up key. It becomes a subject of its own.
                UPDATE import.fact_links SET subject = 'repository', fact_key = ''
                WHERE fact_key = 'repository:' || host || '/' || repository;
                UPDATE import.rejections SET subject = 'repository', fact_key = ''
                WHERE fact_key = 'repository:' || host || '/' || repository;
                UPDATE import.proposals p SET subject = 'repository', fact_key = ''
                FROM import.scans s
                WHERE s.id = p.scan_id AND p.fact_key = 'repository:' || s.host || '/' || s.repository;

                -- The observed consumers of a message were one attribute, joined with commas.
                UPDATE import.fact_links
                SET consumer_keys = to_jsonb(string_to_array(attributes->>'vellum.consumers', ',')),
                    attributes = attributes - 'vellum.consumers'
                WHERE jsonb_exists(attributes, 'vellum.consumers');
                UPDATE import.proposals
                SET consumer_keys = to_jsonb(string_to_array(attributes->>'vellum.consumers', ',')),
                    attributes = attributes - 'vellum.consumers'
                WHERE jsonb_exists(attributes, 'vellum.consumers');

                -- The accepted consumers of a message were IDs joined with commas.
                UPDATE import.fact_links
                SET accepted = jsonb_set(accepted, '{consumerIds}',
                    CASE WHEN accepted->>'consumerIds' = '' THEN '[]'::jsonb
                         ELSE to_jsonb(string_to_array(accepted->>'consumerIds', ',')) END)
                WHERE jsonb_typeof(accepted->'consumerIds') = 'string';

                UPDATE import.fact_links SET fact_key_hash = encode(sha256(convert_to(fact_key, 'UTF8')), 'hex');
                UPDATE import.rejections SET fact_key_hash = encode(sha256(convert_to(fact_key, 'UTF8')), 'hex');

                -- A scan that named a ref read that ref. One that named none read the default
                -- ref, whose name was not recorded; it stays unknown.
                UPDATE import.scans SET resolved_ref = requested_ref
                WHERE requested_ref IS NOT NULL AND requested_ref !~ '^[0-9a-fA-F]{40}$';
                """);

            migrationBuilder.AddPrimaryKey(
                name: "pk_scan_files",
                schema: "import",
                table: "scan_files",
                columns: new[] { "scan_id", "path_hash" });

            migrationBuilder.AddPrimaryKey(
                name: "pk_rejections",
                schema: "import",
                table: "rejections",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_fact_links",
                schema: "import",
                table: "fact_links",
                column: "id");

            migrationBuilder.CreateTable(
                name: "scan_charges",
                schema: "import",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    charged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scan_charges", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tracked_refs",
                schema: "import",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    host = table.Column<string>(type: "text", nullable: false),
                    repository = table.Column<string>(type: "text", nullable: false),
                    repository_id = table.Column<long>(type: "bigint", nullable: true),
                    ref_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    commit_sha = table.Column<string>(type: "text", nullable: true),
                    scan_id = table.Column<Guid>(type: "uuid", nullable: true),
                    set_by = table.Column<string>(type: "text", nullable: false),
                    set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tracked_refs", x => new { x.project_id, x.host, x.repository });
                });

            migrationBuilder.CreateIndex(
                name: "ix_scans_workspace_id_claimed_at",
                schema: "import",
                table: "scans",
                columns: new[] { "workspace_id", "claimed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_rejections_project_id_host_repository_subject_fact_key_hash",
                schema: "import",
                table: "rejections",
                columns: new[] { "project_id", "host", "repository", "subject", "fact_key_hash", "fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_fact_links_project_id_host_repository_subject_fact_key_hash",
                schema: "import",
                table: "fact_links",
                columns: new[] { "project_id", "host", "repository", "subject", "fact_key_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_scan_charges_workspace_id_charged_at",
                schema: "import",
                table: "scan_charges",
                columns: new[] { "workspace_id", "charged_at" });

            // The accepted facts of a repository follow the ref of the scan they were last
            // accepted from. An empty name is the default ref: the next scan without a ref
            // names it. The rule fingerprint of every existing link stays null, which means
            // "unknown": the next tracking scan relinks it before it compares it.
            migrationBuilder.Sql("""
                INSERT INTO import.tracked_refs (project_id, host, repository, repository_id, ref_name, commit_sha, scan_id, set_by, set_at)
                SELECT DISTINCT ON (l.project_id, l.host, l.repository)
                       l.project_id, l.host, l.repository, NULL, COALESCE(s.resolved_ref, ''), l.commit_sha, l.scan_id, l.accepted_by, l.accepted_at
                FROM import.fact_links l
                LEFT JOIN import.scans s ON s.id = l.scan_id
                ORDER BY l.project_id, l.host, l.repository, l.accepted_at DESC, l.id;

                -- Each scan that was started is one unit of usage.maxScansPerDay.
                INSERT INTO import.scan_charges (id, workspace_id, scan_id, charged_at)
                SELECT gen_random_uuid(), workspace_id, id, created_at FROM import.scans;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scan_charges",
                schema: "import");

            migrationBuilder.DropTable(
                name: "tracked_refs",
                schema: "import");

            migrationBuilder.DropIndex(
                name: "ix_scans_workspace_id_claimed_at",
                schema: "import",
                table: "scans");

            migrationBuilder.DropPrimaryKey(
                name: "pk_scan_files",
                schema: "import",
                table: "scan_files");

            migrationBuilder.DropPrimaryKey(
                name: "pk_rejections",
                schema: "import",
                table: "rejections");

            migrationBuilder.DropIndex(
                name: "ix_rejections_project_id_host_repository_subject_fact_key_hash",
                schema: "import",
                table: "rejections");

            migrationBuilder.DropPrimaryKey(
                name: "pk_fact_links",
                schema: "import",
                table: "fact_links");

            migrationBuilder.DropIndex(
                name: "ix_fact_links_project_id_host_repository_subject_fact_key_hash",
                schema: "import",
                table: "fact_links");

            migrationBuilder.DropColumn(
                name: "rule_fingerprint",
                schema: "import",
                table: "snapshots");

            migrationBuilder.DropColumn(
                name: "claimed_at",
                schema: "import",
                table: "scans");

            migrationBuilder.DropColumn(
                name: "coverage",
                schema: "import",
                table: "scans");

            migrationBuilder.DropColumn(
                name: "crashes",
                schema: "import",
                table: "scans");

            migrationBuilder.DropColumn(
                name: "extraction_gap",
                schema: "import",
                table: "scans");

            migrationBuilder.DropColumn(
                name: "files_extracted",
                schema: "import",
                table: "scans");

            migrationBuilder.DropColumn(
                name: "internal_failures",
                schema: "import",
                table: "scans");

            migrationBuilder.DropColumn(
                name: "lease_token",
                schema: "import",
                table: "scans");

            migrationBuilder.DropColumn(
                name: "manual_resumes",
                schema: "import",
                table: "scans");

            migrationBuilder.DropColumn(
                name: "needs_review",
                schema: "import",
                table: "scans");

            migrationBuilder.DropColumn(
                name: "relinked",
                schema: "import",
                table: "scans");

            migrationBuilder.DropColumn(
                name: "repository_id",
                schema: "import",
                table: "scans");

            migrationBuilder.DropColumn(
                name: "repository_name",
                schema: "import",
                table: "scans");

            migrationBuilder.DropColumn(
                name: "resolved_ref",
                schema: "import",
                table: "scans");

            migrationBuilder.DropColumn(
                name: "tracked_at",
                schema: "import",
                table: "scans");

            migrationBuilder.DropColumn(
                name: "tracked_by",
                schema: "import",
                table: "scans");

            migrationBuilder.DropColumn(
                name: "path_hash",
                schema: "import",
                table: "scan_files");

            migrationBuilder.DropColumn(
                name: "id",
                schema: "import",
                table: "rejections");

            migrationBuilder.DropColumn(
                name: "fact_key_hash",
                schema: "import",
                table: "rejections");

            migrationBuilder.DropColumn(
                name: "repository_id",
                schema: "import",
                table: "rejections");

            migrationBuilder.DropColumn(
                name: "subject",
                schema: "import",
                table: "rejections");

            migrationBuilder.DropColumn(
                name: "consumer_keys",
                schema: "import",
                table: "proposals");

            migrationBuilder.DropColumn(
                name: "subject",
                schema: "import",
                table: "proposals");

            migrationBuilder.DropColumn(
                name: "id",
                schema: "import",
                table: "fact_links");

            migrationBuilder.DropColumn(
                name: "consumer_keys",
                schema: "import",
                table: "fact_links");

            migrationBuilder.DropColumn(
                name: "fact_key_hash",
                schema: "import",
                table: "fact_links");

            migrationBuilder.DropColumn(
                name: "repository_id",
                schema: "import",
                table: "fact_links");

            migrationBuilder.DropColumn(
                name: "rule_fingerprint",
                schema: "import",
                table: "fact_links");

            migrationBuilder.DropColumn(
                name: "subject",
                schema: "import",
                table: "fact_links");

            migrationBuilder.AlterColumn<string>(
                name: "fact_key",
                schema: "import",
                table: "snapshot_facts",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000);

            migrationBuilder.AlterColumn<string>(
                name: "path",
                schema: "import",
                table: "scan_files",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(1024)",
                oldMaxLength: 1024);

            migrationBuilder.AlterColumn<string>(
                name: "fact_key",
                schema: "import",
                table: "rejections",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000);

            migrationBuilder.AlterColumn<string>(
                name: "fact_key",
                schema: "import",
                table: "proposals",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000);

            migrationBuilder.AlterColumn<string>(
                name: "fact_key",
                schema: "import",
                table: "fact_links",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000);

            migrationBuilder.AddPrimaryKey(
                name: "pk_scan_files",
                schema: "import",
                table: "scan_files",
                columns: new[] { "scan_id", "path" });

            migrationBuilder.AddPrimaryKey(
                name: "pk_rejections",
                schema: "import",
                table: "rejections",
                columns: new[] { "project_id", "host", "repository", "fact_key", "fingerprint" });

            migrationBuilder.AddPrimaryKey(
                name: "pk_fact_links",
                schema: "import",
                table: "fact_links",
                columns: new[] { "project_id", "host", "repository", "fact_key" });
        }
    }
}
