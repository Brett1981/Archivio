using Archivio.Application.Abstractions;
using Archivio.Application.Configuration;
using Archivio.Application.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Archivio.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ArchivioOptions>()
            .Bind(configuration.GetSection(ArchivioOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<ILibrarySourceService, LibrarySourceService>();

        return services;
    }
}
