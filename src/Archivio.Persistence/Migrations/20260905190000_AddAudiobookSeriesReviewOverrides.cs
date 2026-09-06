using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archivio.Persistence.Migrations;

[DbContext(typeof(ArchivioDbContext))]
[Migration("20260905190000_AddAudiobookSeriesReviewOverrides")]
public partial class AddAudiobookSeriesReviewOverrides : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "CollectionHandling",
            table: "AudiobookReviewOverrides",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "SeriesName",
            table: "AudiobookReviewOverrides",
            type: "TEXT",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "SeriesPosition",
            table: "AudiobookReviewOverrides",
            type: "INTEGER",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "CollectionHandling", table: "AudiobookReviewOverrides");
        migrationBuilder.DropColumn(name: "SeriesName", table: "AudiobookReviewOverrides");
        migrationBuilder.DropColumn(name: "SeriesPosition", table: "AudiobookReviewOverrides");
    }
}
