using Archivio.Application.Abstractions;
using Archivio.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Archivio.Persistence;

public sealed class DatabaseInitializer(
    IServiceScopeFactory scopeFactory,
    IDatabaseBackupService databaseBackupService,
    ILogger<DatabaseInitializer> logger) : IDatabaseInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ArchivioDbContext>();

        var pendingMigrations = (await context.Database
            .GetPendingMigrationsAsync(cancellationToken))
            .ToList();
        if (pendingMigrations.Count > 0)
        {
            await databaseBackupService.CreateBackupAsync("before-migration", cancellationToken);
        }

        logger.LogInformation(
            "Applying {MigrationCount} pending Archivio database migrations",
            pendingMigrations.Count);
        await context.Database.MigrateAsync(cancellationToken);

        if (!await context.SystemRecords.AnyAsync(x => x.Name == "Schema", cancellationToken))
        {
            context.SystemRecords.Add(new SystemRecord("Schema", "Milestone1"));
            await context.SaveChangesAsync(cancellationToken);
        }
    }
}
