using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archivio.Persistence.Migrations;

[DbContext(typeof(ArchivioDbContext))]
[Migration("20260818170000_PersistAudiobookAnalysis")]
public partial class PersistAudiobookAnalysis : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AudiobookAnalysisRuns",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                LibrarySourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                ProcessedCount = table.Column<int>(type: "INTEGER", nullable: false),
                TotalCount = table.Column<int>(type: "INTEGER", nullable: false),
                WarningCount = table.Column<int>(type: "INTEGER", nullable: false),
                CandidateCount = table.Column<int>(type: "INTEGER", nullable: false),
                ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AudiobookAnalysisRuns", x => x.Id);
                table.ForeignKey(
                    name: "FK_AudiobookAnalysisRuns_LibrarySources_LibrarySourceId",
                    column: x => x.LibrarySourceId,
                    principalTable: "LibrarySources",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AudiobookMetadataCache",
            columns: table => new
            {
                MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                LibrarySourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                AnalysedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                MetadataJson = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AudiobookMetadataCache", x => x.MediaItemId);
                table.ForeignKey(
                    name: "FK_AudiobookMetadataCache_LibrarySources_LibrarySourceId",
                    column: x => x.LibrarySourceId,
                    principalTable: "LibrarySources",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_AudiobookMetadataCache_MediaItems_MediaItemId",
                    column: x => x.MediaItemId,
                    principalTable: "MediaItems",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AudiobookCandidateSnapshots",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                AnalysisRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                CandidateJson = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AudiobookCandidateSnapshots", x => x.Id);
                table.ForeignKey(
                    name: "FK_AudiobookCandidateSnapshots_AudiobookAnalysisRuns_AnalysisRunId",
                    column: x => x.AnalysisRunId,
                    principalTable: "AudiobookAnalysisRuns",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AudiobookAnalysisRuns_LibrarySourceId",
            table: "AudiobookAnalysisRuns",
            column: "LibrarySourceId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_AudiobookCandidateSnapshots_AnalysisRunId_SortOrder",
            table: "AudiobookCandidateSnapshots",
            columns: new[] { "AnalysisRunId", "SortOrder" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_AudiobookMetadataCache_LibrarySourceId",
            table: "AudiobookMetadataCache",
            column: "LibrarySourceId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "AudiobookCandidateSnapshots");
        migrationBuilder.DropTable(name: "AudiobookMetadataCache");
        migrationBuilder.DropTable(name: "AudiobookAnalysisRuns");
    }
}
