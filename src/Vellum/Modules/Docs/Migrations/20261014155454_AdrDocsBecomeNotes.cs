using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vellum.Modules.Docs.Migrations
{
    /// <summary>
    /// An ADR is a Review decision, not a document type. A document of type <c>adr</c> keeps its
    /// title and text and becomes a note.
    /// </summary>
    public partial class AdrDocsBecomeNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE docs.documents SET type = 'note' WHERE lower(type) = 'adr';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A note does not record that it was an ADR, so nothing is turned back.
        }
    }
}
