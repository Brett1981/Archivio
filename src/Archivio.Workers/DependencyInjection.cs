using Archivio.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Archivio.Workers;

public static class DependencyInjection
{
    public static IServiceCollection AddWorkers(this IServiceCollection services)
    {
        services.AddSingleton<BackgroundScanWorker>();
        services.AddSingleton<IBackgroundScanService>(provider => provider.GetRequiredService<BackgroundScanWorker>());
        services.AddHostedService(provider => provider.GetRequiredService<BackgroundScanWorker>());
        return services;
    }
}
