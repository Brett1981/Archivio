using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archivio.Persistence.Migrations;

[DbContext(typeof(ArchivioDbContext))]
[Migration("20260905170000_AddAudiobookIdentityReviewOverrides")]
public partial class AddAudiobookIdentityReviewOverrides : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "CanonicalAuthor",
            table: "AudiobookReviewOverrides",
            type: "TEXT",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CanonicalTitle",
            table: "AudiobookReviewOverrides",
            type: "TEXT",
            maxLength: 300,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CanonicalAuthor",
            table: "AudiobookReviewOverrides");

        migrationBuilder.DropColumn(
            name: "CanonicalTitle",
            table: "AudiobookReviewOverrides");
    }
}
