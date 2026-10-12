using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Modules.Modelling.Migrations
{
    /// <inheritdoc />
    public partial class AddFactsKindsRevisionsVariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "fact_status",
                schema: "modelling",
                table: "relationships",
                type: "text",
                nullable: false,
                defaultValue: "declared");

            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "modelling",
                table: "relationships",
                type: "text",
                nullable: false,
                defaultValue: "structural");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "provenance_accepted_at",
                schema: "modelling",
                table: "relationships",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "provenance_accepted_by",
                schema: "modelling",
                table: "relationships",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "provenance_confidence",
                schema: "modelling",
                table: "relationships",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provenance_revision",
                schema: "modelling",
                table: "relationships",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provenance_source",
                schema: "modelling",
                table: "relationships",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fact_status",
                schema: "modelling",
                table: "messages",
                type: "text",
                nullable: false,
                defaultValue: "declared");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "provenance_accepted_at",
                schema: "modelling",
                table: "messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "provenance_accepted_by",
                schema: "modelling",
                table: "messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "provenance_confidence",
                schema: "modelling",
                table: "messages",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provenance_revision",
                schema: "modelling",
                table: "messages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provenance_source",
                schema: "modelling",
                table: "messages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fact_status",
                schema: "modelling",
                table: "elements",
                type: "text",
                nullable: false,
                defaultValue: "declared");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "provenance_accepted_at",
                schema: "modelling",
                table: "elements",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "provenance_accepted_by",
                schema: "modelling",
                table: "elements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "provenance_confidence",
                schema: "modelling",
                table: "elements",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provenance_revision",
                schema: "modelling",
                table: "elements",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provenance_source",
                schema: "modelling",
                table: "elements",
                type: "text",
                nullable: true);

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
                name: "revisions",
                schema: "modelling");

            migrationBuilder.DropTable(
                name: "variants",
                schema: "modelling");

            migrationBuilder.DropColumn(
                name: "fact_status",
                schema: "modelling",
                table: "relationships");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "modelling",
                table: "relationships");

            migrationBuilder.DropColumn(
                name: "provenance_accepted_at",
                schema: "modelling",
                table: "relationships");

            migrationBuilder.DropColumn(
                name: "provenance_accepted_by",
                schema: "modelling",
                table: "relationships");

            migrationBuilder.DropColumn(
                name: "provenance_confidence",
                schema: "modelling",
                table: "relationships");

            migrationBuilder.DropColumn(
                name: "provenance_revision",
                schema: "modelling",
                table: "relationships");

            migrationBuilder.DropColumn(
                name: "provenance_source",
                schema: "modelling",
                table: "relationships");

            migrationBuilder.DropColumn(
                name: "fact_status",
                schema: "modelling",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "provenance_accepted_at",
                schema: "modelling",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "provenance_accepted_by",
                schema: "modelling",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "provenance_confidence",
                schema: "modelling",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "provenance_revision",
                schema: "modelling",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "provenance_source",
                schema: "modelling",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "fact_status",
                schema: "modelling",
                table: "elements");

            migrationBuilder.DropColumn(
                name: "provenance_accepted_at",
                schema: "modelling",
                table: "elements");

            migrationBuilder.DropColumn(
                name: "provenance_accepted_by",
                schema: "modelling",
                table: "elements");

            migrationBuilder.DropColumn(
                name: "provenance_confidence",
                schema: "modelling",
                table: "elements");

            migrationBuilder.DropColumn(
                name: "provenance_revision",
                schema: "modelling",
                table: "elements");

            migrationBuilder.DropColumn(
                name: "provenance_source",
                schema: "modelling",
                table: "elements");
        }
    }
}
