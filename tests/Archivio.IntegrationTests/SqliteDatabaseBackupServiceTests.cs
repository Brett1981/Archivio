using Archivio.Application.Abstractions;
using Archivio.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archivio.IntegrationTests;

public sealed class SqliteDatabaseBackupServiceTests
{
    [Fact]
    public async Task CreateBackupAsync_CreatesReadableConsistentCopy()
    {
        SQLitePCL.Batteries_V2.Init();
        var root = Path.Combine(
            Path.GetTempPath(),
            "Metaroq.Backup.Tests",
            Guid.NewGuid().ToString("N"));
        var paths = new TestAppDataPaths(root);

        try
        {
            paths.EnsureCreated();
            await using (var source = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False"))
            {
                await source.OpenAsync();
                await using var command = source.CreateCommand();
                command.CommandText = "CREATE TABLE Sample(Value TEXT NOT NULL); INSERT INTO Sample VALUES ('preserved');";
                await command.ExecuteNonQueryAsync();
            }

            var service = new SqliteDatabaseBackupService(
                paths,
                NullLogger<SqliteDatabaseBackupService>.Instance);

            var backupPath = await service.CreateBackupAsync("test");

            Assert.NotNull(backupPath);
            Assert.True(File.Exists(backupPath));
            await using (var backup = new SqliteConnection(
                             $"Data Source={backupPath};Mode=ReadOnly;Pooling=False"))
            {
                await backup.OpenAsync();
                await using var query = backup.CreateCommand();
                query.CommandText = "SELECT Value FROM Sample;";
                Assert.Equal("preserved", await query.ExecuteScalarAsync());
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class TestAppDataPaths(string rootDirectory) : IAppDataPaths
    {
        public string RootDirectory { get; } = rootDirectory;
        public string DataDirectory { get; } = Path.Combine(rootDirectory, "data");
        public string LogsDirectory { get; } = Path.Combine(rootDirectory, "logs");
        public string BackupsDirectory { get; } = Path.Combine(rootDirectory, "backups");
        public string DatabasePath { get; } = Path.Combine(rootDirectory, "data", "archivio.db");

        public void EnsureCreated()
        {
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(LogsDirectory);
            Directory.CreateDirectory(BackupsDirectory);
        }
    }
}
