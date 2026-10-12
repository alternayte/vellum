using Deedbox;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Modules.Modelling.Migrations
{
    /// <summary>
    /// Creates the Deedbox event-store tables in the <c>deedbox</c> schema. The tables stay
    /// out of the EF Core model. The script is idempotent and records itself in
    /// <c>deedbox.schema_version</c>. When a Deedbox upgrade needs a newer schema, add a
    /// migration with <c>PostgresSchema.Script(fromVersion: n)</c>.
    /// </summary>
    public partial class AddDeedbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PostgresSchema.Script(fromVersion: 0));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP SCHEMA IF EXISTS deedbox CASCADE;");
        }
    }
}
