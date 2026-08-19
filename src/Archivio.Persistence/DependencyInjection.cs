using Archivio.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archivio.Persistence;

public static class DependencyInjection
{
    public static IServiceCollection AddPersistence(this IServiceCollection services)
    {
        SQLitePCL.Batteries_V2.Init();

        services.AddDbContextFactory<ArchivioDbContext>((provider, options) =>
        {
            var paths = provider.GetRequiredService<IAppDataPaths>();
            paths.EnsureCreated();
            options.UseSqlite($"Data Source={paths.DatabasePath}");
        });
        services.AddScoped(provider =>
            provider.GetRequiredService<IDbContextFactory<ArchivioDbContext>>().CreateDbContext());

        services.AddScoped<ILibrarySourceRepository, LibrarySourceRepository>();
        services.AddScoped<IMediaItemRepository, MediaItemRepository>();
        services.AddSingleton<IAudiobookAnalysisStore, AudiobookAnalysisStore>();
        services.AddSingleton<IAudiobookOrganisationStore, AudiobookOrganisationStore>();
        services.AddSingleton<IAudiobookBatchDecisionStore, AudiobookBatchDecisionStore>();
        services.AddSingleton<IAudiobookExecutionJournalStore, AudiobookExecutionJournalStore>();
        services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
        return services;
    }
}
