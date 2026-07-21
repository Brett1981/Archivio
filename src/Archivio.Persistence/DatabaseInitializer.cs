using Archivio.Application.Abstractions;
using Archivio.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Archivio.Persistence;

public sealed class DatabaseInitializer(
    IServiceScopeFactory scopeFactory,
    ILogger<DatabaseInitializer> logger) : IDatabaseInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ArchivioDbContext>();

        logger.LogInformation("Applying Archivio database migrations");
        await context.Database.MigrateAsync(cancellationToken);

        if (!await context.SystemRecords.AnyAsync(x => x.Name == "Schema", cancellationToken))
        {
            context.SystemRecords.Add(new SystemRecord("Schema", "Milestone1"));
            await context.SaveChangesAsync(cancellationToken);
        }
    }
}
