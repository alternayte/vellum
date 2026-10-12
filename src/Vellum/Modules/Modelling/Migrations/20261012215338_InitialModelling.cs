using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Modules.Modelling.Migrations
{
    /// <inheritdoc />
    public partial class InitialModelling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "modelling");

            migrationBuilder.CreateTable(
                name: "elements",
                schema: "modelling",
                columns: table => new
                {
                    row_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    technology = table.Column<string>(type: "text", nullable: true),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tags = table.Column<string[]>(type: "text[]", nullable: false),
                    icon = table.Column<string>(type: "text", nullable: true),
                    fact_status = table.Column<string>(type: "text", nullable: false),
                    provenance_source = table.Column<string>(type: "text", nullable: true),
                    provenance_revision = table.Column<string>(type: "text", nullable: true),
                    provenance_confidence = table.Column<double>(type: "double precision", nullable: true),
                    provenance_accepted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    provenance_accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_elements", x => x.row_id);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                schema: "modelling",
                columns: table => new
                {
                    row_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    producer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    schema_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tags = table.Column<string[]>(type: "text[]", nullable: false),
                    fact_status = table.Column<string>(type: "text", nullable: false),
                    provenance_source = table.Column<string>(type: "text", nullable: true),
                    provenance_revision = table.Column<string>(type: "text", nullable: true),
                    provenance_confidence = table.Column<double>(type: "double precision", nullable: true),
                    provenance_accepted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    provenance_accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_messages", x => x.row_id);
                });

            migrationBuilder.CreateTable(
                name: "relationships",
                schema: "modelling",
                columns: table => new
                {
                    row_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch = table.Column<Guid>(type: "uuid", nullable: false),
                    from_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "text", nullable: true),
                    technology = table.Column<string>(type: "text", nullable: true),
                    message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    line_shape = table.Column<string>(type: "text", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    fact_status = table.Column<string>(type: "text", nullable: false),
                    provenance_source = table.Column<string>(type: "text", nullable: true),
                    provenance_revision = table.Column<string>(type: "text", nullable: true),
                    provenance_confidence = table.Column<double>(type: "double precision", nullable: true),
                    provenance_accepted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    provenance_accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_relationships", x => x.row_id);
                });

            migrationBuilder.CreateTable(
                name: "revisions",
                schema: "modelling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    stream_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_revisions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "variants",
                schema: "modelling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    stream_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_stream_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    fork_version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_variants", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_elements_project_id_branch_id",
                schema: "modelling",
                table: "elements",
                columns: new[] { "project_id", "branch", "id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_elements_project_id_branch_parent_id",
                schema: "modelling",
                table: "elements",
                columns: new[] { "project_id", "branch", "parent_id" });

            migrationBuilder.CreateIndex(
                name: "ix_messages_project_id_branch_id",
                schema: "modelling",
                table: "messages",
                columns: new[] { "project_id", "branch", "id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_messages_project_id_branch_producer_id",
                schema: "modelling",
                table: "messages",
                columns: new[] { "project_id", "branch", "producer_id" });

            migrationBuilder.CreateIndex(
                name: "ix_relationships_project_id_branch_id",
                schema: "modelling",
                table: "relationships",
                columns: new[] { "project_id", "branch", "id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_revisions_project_id_name",
                schema: "modelling",
                table: "revisions",
                columns: new[] { "project_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_variants_project_id_name",
                schema: "modelling",
                table: "variants",
                columns: new[] { "project_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_variants_stream_id",
                schema: "modelling",
                table: "variants",
                column: "stream_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "elements",
                schema: "modelling");

            migrationBuilder.DropTable(
                name: "messages",
                schema: "modelling");

            migrationBuilder.DropTable(
                name: "relationships",
                schema: "modelling");

            migrationBuilder.DropTable(
                name: "revisions",
                schema: "modelling");

            migrationBuilder.DropTable(
                name: "variants",
                schema: "modelling");
        }
    }
}
