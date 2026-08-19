using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archivio.Persistence.Migrations;

[DbContext(typeof(ArchivioDbContext))]
[Migration("20260819120000_AddAudiobookOrganisationProposals")]
public partial class AddAudiobookOrganisationProposals : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AudiobookOrganisationProposals",
            columns: table => new
            {
                CandidateKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                LibrarySourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                InputSignature = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                GeneratedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                ProposalJson = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AudiobookOrganisationProposals", x => x.CandidateKey);
                table.ForeignKey(
                    name: "FK_AudiobookOrganisationProposals_LibrarySources_LibrarySourceId",
                    column: x => x.LibrarySourceId,
                    principalTable: "LibrarySources",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AudiobookOrganisationProposals_LibrarySourceId",
            table: "AudiobookOrganisationProposals",
            column: "LibrarySourceId");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "AudiobookOrganisationProposals");
}
