using Archivio.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Archivio.IntegrationTests;

public sealed class DatabaseMigrationTests
{
    [Fact]
    public async Task InitialMigration_CreatesSystemRecordsTable()
    {
        SQLitePCL.Batteries_V2.Init();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ArchivioDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ArchivioDbContext(options);
        await context.Database.MigrateAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='SystemRecords';";
        var count = Convert.ToInt64(await command.ExecuteScalarAsync());

        Assert.Equal(1, count);

        command.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId='202607210001_InitialCreate';";
        var migrationCount = Convert.ToInt64(await command.ExecuteScalarAsync());

        Assert.Equal(1, migrationCount);
    }
}
