using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archivio.Persistence.Migrations;

[DbContext(typeof(ArchivioDbContext))]
[Migration("20260905150000_AddAudiobookReviewOverrides")]
public partial class AddAudiobookReviewOverrides : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AudiobookReviewOverrides",
            columns: table => new
            {
                LibrarySourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                PlanKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                GenreCategory = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AudiobookReviewOverrides", x => new { x.LibrarySourceId, x.PlanKey });
                table.ForeignKey(
                    name: "FK_AudiobookReviewOverrides_LibrarySources_LibrarySourceId",
                    column: x => x.LibrarySourceId,
                    principalTable: "LibrarySources",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "AudiobookReviewOverrides");
}
