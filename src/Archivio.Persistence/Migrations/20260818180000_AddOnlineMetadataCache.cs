using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archivio.Persistence.Migrations;

[DbContext(typeof(ArchivioDbContext))]
[Migration("20260818180000_AddOnlineMetadataCache")]
public partial class AddOnlineMetadataCache : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "OnlineMetadataCache",
            columns: table => new
            {
                CandidateKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                LibrarySourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                InputSignature = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                RetrievedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                SuggestionJson = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OnlineMetadataCache", x => x.CandidateKey);
                table.ForeignKey(
                    name: "FK_OnlineMetadataCache_LibrarySources_LibrarySourceId",
                    column: x => x.LibrarySourceId,
                    principalTable: "LibrarySources",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_OnlineMetadataCache_LibrarySourceId",
            table: "OnlineMetadataCache",
            column: "LibrarySourceId");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "OnlineMetadataCache");
}
