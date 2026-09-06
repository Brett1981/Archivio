using Archivio.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Archivio.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IAppDataPaths, LocalAppDataPaths>();
        services.AddSingleton<IDirectoryService, LocalDirectoryService>();
        services.AddSingleton<IAudiobookFileOperator, LocalAudiobookFileOperator>();
        services.AddSingleton<IAudiobookCoverArtworkProvider, OpenLibraryCoverArtworkProvider>();
        services.AddSingleton<IAudioPreviewService, WindowsAudioPreviewService>();
        return services;
    }
}
