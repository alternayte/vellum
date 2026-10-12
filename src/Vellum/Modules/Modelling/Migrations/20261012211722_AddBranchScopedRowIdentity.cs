using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Modules.Modelling.Migrations
{
    /// <inheritdoc />
    public partial class AddBranchScopedRowIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_relationships",
                schema: "modelling",
                table: "relationships");

            migrationBuilder.DropIndex(
                name: "ix_relationships_project_id_branch",
                schema: "modelling",
                table: "relationships");

            migrationBuilder.DropPrimaryKey(
                name: "pk_messages",
                schema: "modelling",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "ix_messages_project_id_branch",
                schema: "modelling",
                table: "messages");

            migrationBuilder.DropPrimaryKey(
                name: "pk_elements",
                schema: "modelling",
                table: "elements");

            migrationBuilder.DropIndex(
                name: "ix_elements_project_id_branch",
                schema: "modelling",
                table: "elements");

            migrationBuilder.AddColumn<Guid>(
                name: "row_id",
                schema: "modelling",
                table: "relationships",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<Guid>(
                name: "row_id",
                schema: "modelling",
                table: "messages",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<Guid>(
                name: "row_id",
                schema: "modelling",
                table: "elements",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddPrimaryKey(
                name: "pk_relationships",
                schema: "modelling",
                table: "relationships",
                column: "row_id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_messages",
                schema: "modelling",
                table: "messages",
                column: "row_id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_elements",
                schema: "modelling",
                table: "elements",
                column: "row_id");

            migrationBuilder.CreateIndex(
                name: "ix_relationships_project_id_branch_id",
                schema: "modelling",
                table: "relationships",
                columns: new[] { "project_id", "branch", "id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_messages_project_id_branch_id",
                schema: "modelling",
                table: "messages",
                columns: new[] { "project_id", "branch", "id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_elements_project_id_branch_id",
                schema: "modelling",
                table: "elements",
                columns: new[] { "project_id", "branch", "id" },
                unique: true);
        }

        /// <inheritdoc />
        /// <summary>
        /// Not reversible once any draft or variant has copied rows onto another branch:
        /// restoring the single-column ID primary key fails on duplicate architecture IDs.
        /// Reversing requires removing branch copies first.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_relationships",
                schema: "modelling",
                table: "relationships");

            migrationBuilder.DropIndex(
                name: "ix_relationships_project_id_branch_id",
                schema: "modelling",
                table: "relationships");

            migrationBuilder.DropPrimaryKey(
                name: "pk_messages",
                schema: "modelling",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "ix_messages_project_id_branch_id",
                schema: "modelling",
                table: "messages");

            migrationBuilder.DropPrimaryKey(
                name: "pk_elements",
                schema: "modelling",
                table: "elements");

            migrationBuilder.DropIndex(
                name: "ix_elements_project_id_branch_id",
                schema: "modelling",
                table: "elements");

            migrationBuilder.DropColumn(
                name: "row_id",
                schema: "modelling",
                table: "relationships");

            migrationBuilder.DropColumn(
                name: "row_id",
                schema: "modelling",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "row_id",
                schema: "modelling",
                table: "elements");

            migrationBuilder.AddPrimaryKey(
                name: "pk_relationships",
                schema: "modelling",
                table: "relationships",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_messages",
                schema: "modelling",
                table: "messages",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_elements",
                schema: "modelling",
                table: "elements",
                column: "id");

            migrationBuilder.CreateIndex(
                name: "ix_relationships_project_id_branch",
                schema: "modelling",
                table: "relationships",
                columns: new[] { "project_id", "branch" });

            migrationBuilder.CreateIndex(
                name: "ix_messages_project_id_branch",
                schema: "modelling",
                table: "messages",
                columns: new[] { "project_id", "branch" });

            migrationBuilder.CreateIndex(
                name: "ix_elements_project_id_branch",
                schema: "modelling",
                table: "elements",
                columns: new[] { "project_id", "branch" });
        }
    }
}
