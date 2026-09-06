using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archivio.Persistence.Migrations;

[DbContext(typeof(ArchivioDbContext))]
[Migration("20260906160000_AddLibraryDestinationPath")]
public partial class AddLibraryDestinationPath : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "DestinationPath",
            table: "LibrarySources",
            type: "TEXT",
            maxLength: 1024,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SourceRoot",
            table: "AudiobookExecutionRuns",
            type: "TEXT",
            maxLength: 1024,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "DestinationRoot",
            table: "AudiobookExecutionRuns",
            type: "TEXT",
            maxLength: 1024,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "DestinationPath",
            table: "LibrarySources");

        migrationBuilder.DropColumn(
            name: "SourceRoot",
            table: "AudiobookExecutionRuns");

        migrationBuilder.DropColumn(
            name: "DestinationRoot",
            table: "AudiobookExecutionRuns");
    }
}
