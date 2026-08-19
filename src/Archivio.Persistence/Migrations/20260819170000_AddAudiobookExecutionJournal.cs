using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archivio.Persistence.Migrations;

[DbContext(typeof(ArchivioDbContext))]
[Migration("20260819170000_AddAudiobookExecutionJournal")]
public partial class AddAudiobookExecutionJournal : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AudiobookExecutionRuns",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                LibrarySourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                PlannedOperationCount = table.Column<int>(type: "INTEGER", nullable: false),
                CompletedOperationCount = table.Column<int>(type: "INTEGER", nullable: false),
                RolledBackOperationCount = table.Column<int>(type: "INTEGER", nullable: false),
                StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AudiobookExecutionRuns", x => x.Id);
                table.ForeignKey(
                    name: "FK_AudiobookExecutionRuns_LibrarySources_LibrarySourceId",
                    column: x => x.LibrarySourceId,
                    principalTable: "LibrarySources",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AudiobookExecutionOperations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                PlanKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                InputSignature = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                SourceRelativePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                DestinationRelativePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                Kind = table.Column<int>(type: "INTEGER", nullable: false),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                SourceSizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                SourceModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AudiobookExecutionOperations", x => x.Id);
                table.ForeignKey(
                    name: "FK_AudiobookExecutionOperations_AudiobookExecutionRuns_RunId",
                    column: x => x.RunId,
                    principalTable: "AudiobookExecutionRuns",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AudiobookExecutionRuns_LibrarySourceId_StartedAtUtc",
            table: "AudiobookExecutionRuns",
            columns: new[] { "LibrarySourceId", "StartedAtUtc" });
        migrationBuilder.CreateIndex(
            name: "IX_AudiobookExecutionOperations_RunId_SortOrder",
            table: "AudiobookExecutionOperations",
            columns: new[] { "RunId", "SortOrder" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "AudiobookExecutionOperations");
        migrationBuilder.DropTable(name: "AudiobookExecutionRuns");
    }
}
