using Archivio.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Archivio.Media;

public static class DependencyInjection
{
    public static IServiceCollection AddMedia(this IServiceCollection services)
    {
        services.AddSingleton<IFileDiscoveryService, SafeFileDiscoveryService>();
        return services;
    }
}
