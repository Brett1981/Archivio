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
        services.AddScoped<ILibraryScanService, LibraryScanService>();
        services.AddScoped<IMediaCatalogueService, MediaCatalogueService>();
        services.AddSingleton<IAudiobookAnalysisService, AudiobookAnalysisService>();
        services.AddSingleton<IBookMetadataProvider, OpenLibraryMetadataProvider>();
        services.AddSingleton<IOnlineMetadataLookupService, OnlineMetadataLookupService>();
        services.AddSingleton<IAudiobookOrganisationService, AudiobookOrganisationService>();

        return services;
    }
}
