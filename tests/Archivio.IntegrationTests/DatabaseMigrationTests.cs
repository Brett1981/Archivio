using Archivio.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Archivio.IntegrationTests;

public sealed class DatabaseMigrationTests
{
    [Fact]
    public async Task Migrations_CreateExpectedSchema()
    {
        SQLitePCL.Batteries_V2.Init();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ArchivioDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ArchivioDbContext(options);
        await context.Database.MigrateAsync();

        Assert.Equal(1, await CountTableAsync(connection, "SystemRecords"));
        Assert.Equal(1, await CountTableAsync(connection, "LibrarySources"));
        Assert.Equal(1, await CountMigrationAsync(connection, "202607210001_InitialCreate"));
        Assert.Equal(1, await CountMigrationAsync(connection, "202607210002_AddLibrarySources"));
    }

    private static async Task<long> CountTableAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$tableName;";
        command.Parameters.AddWithValue("$tableName", tableName);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<long> CountMigrationAsync(SqliteConnection connection, string migrationId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId=$migrationId;";
        command.Parameters.AddWithValue("$migrationId", migrationId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
