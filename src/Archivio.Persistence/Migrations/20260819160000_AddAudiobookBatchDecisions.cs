using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archivio.Persistence.Migrations;

[DbContext(typeof(ArchivioDbContext))]
[Migration("20260819160000_AddAudiobookBatchDecisions")]
public partial class AddAudiobookBatchDecisions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AudiobookBatchDecisions",
            columns: table => new
            {
                LibrarySourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                PlanKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                InputSignature = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Decision = table.Column<int>(type: "INTEGER", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AudiobookBatchDecisions", x => new { x.LibrarySourceId, x.PlanKey });
                table.ForeignKey(
                    name: "FK_AudiobookBatchDecisions_LibrarySources_LibrarySourceId",
                    column: x => x.LibrarySourceId,
                    principalTable: "LibrarySources",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "AudiobookBatchDecisions");
}
