using Archivio.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archivio.Persistence;

public static class DependencyInjection
{
    public static IServiceCollection AddPersistence(this IServiceCollection services)
    {
        SQLitePCL.Batteries_V2.Init();

        services.AddDbContext<ArchivioDbContext>((provider, options) =>
        {
            var paths = provider.GetRequiredService<IAppDataPaths>();
            paths.EnsureCreated();
            options.UseSqlite($"Data Source={paths.DatabasePath}");
        });

        services.AddScoped<ILibrarySourceRepository, LibrarySourceRepository>();
        services.AddScoped<IMediaItemRepository, MediaItemRepository>();
        services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
        return services;
    }
}
