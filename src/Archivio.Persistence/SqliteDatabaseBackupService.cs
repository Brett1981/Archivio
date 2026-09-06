using Archivio.Application.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Archivio.Persistence;

public sealed class SqliteDatabaseBackupService(
    IAppDataPaths paths,
    ILogger<SqliteDatabaseBackupService> logger) : IDatabaseBackupService
{
    private const int RetainedBackupCount = 20;

    public async Task<string?> CreateBackupAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        paths.EnsureCreated();

        if (!File.Exists(paths.DatabasePath) || new FileInfo(paths.DatabasePath).Length == 0)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var safeReason = string.Concat(reason.Trim().Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-'));
        var backupPath = Path.Combine(
            paths.BackupsDirectory,
            $"archivio-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{safeReason}.db");

        var sourceBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        var destinationBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };

        await using var source = new SqliteConnection(sourceBuilder.ToString());
        await using var destination = new SqliteConnection(destinationBuilder.ToString());
        await source.OpenAsync(cancellationToken);
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);

        logger.LogInformation("Created SQLite safety backup {BackupFile}", Path.GetFileName(backupPath));
        PruneOldBackups(backupPath);
        return backupPath;
    }

    private void PruneOldBackups(string currentBackupPath)
    {
        try
        {
            var oldBackups = Directory
                .EnumerateFiles(paths.BackupsDirectory, "archivio-*.db")
                .Where(path => !string.Equals(path, currentBackupPath, StringComparison.OrdinalIgnoreCase))
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.CreationTimeUtc)
                .Skip(RetainedBackupCount - 1)
                .ToList();

            foreach (var backup in oldBackups)
            {
                backup.Delete();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Old SQLite backups could not be pruned");
        }
    }
}
