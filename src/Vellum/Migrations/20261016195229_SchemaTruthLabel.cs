using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Vellum.Modules.Modelling;

#nullable disable

namespace Vellum.Migrations
{
    /// <inheritdoc />
    public partial class SchemaTruthLabel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<FactStatus>(
                name: "fact_status",
                schema: "schemas",
                table: "schemas",
                type: "modelling.fact_status",
                nullable: false,
                defaultValue: FactStatus.Declared);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "provenance_accepted_at",
                schema: "schemas",
                table: "schemas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "provenance_accepted_by",
                schema: "schemas",
                table: "schemas",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "provenance_confidence",
                schema: "schemas",
                table: "schemas",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "provenance_content_not_held",
                schema: "schemas",
                table: "schemas",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "provenance_revision",
                schema: "schemas",
                table: "schemas",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provenance_source",
                schema: "schemas",
                table: "schemas",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "fact_status",
                schema: "schemas",
                table: "schemas");

            migrationBuilder.DropColumn(
                name: "provenance_accepted_at",
                schema: "schemas",
                table: "schemas");

            migrationBuilder.DropColumn(
                name: "provenance_accepted_by",
                schema: "schemas",
                table: "schemas");

            migrationBuilder.DropColumn(
                name: "provenance_confidence",
                schema: "schemas",
                table: "schemas");

            migrationBuilder.DropColumn(
                name: "provenance_content_not_held",
                schema: "schemas",
                table: "schemas");

            migrationBuilder.DropColumn(
                name: "provenance_revision",
                schema: "schemas",
                table: "schemas");

            migrationBuilder.DropColumn(
                name: "provenance_source",
                schema: "schemas",
                table: "schemas");
        }
    }
}
