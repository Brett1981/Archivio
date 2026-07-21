using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archivio.Persistence.Migrations;

public partial class AddMediaItems : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "MediaItems",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                LibrarySourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                FullPath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                RelativePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                FileName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                Extension = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                LastScannedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                ContentHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                IsMissing = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MediaItems", x => x.Id);
                table.ForeignKey(
                    name: "FK_MediaItems_LibrarySources_LibrarySourceId",
                    column: x => x.LibrarySourceId,
                    principalTable: "LibrarySources",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_MediaItems_LibrarySourceId_FullPath",
            table: "MediaItems",
            columns: new[] { "LibrarySourceId", "FullPath" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_MediaItems_LibrarySourceId_RelativePath",
            table: "MediaItems",
            columns: new[] { "LibrarySourceId", "RelativePath" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "MediaItems");
    }
}
