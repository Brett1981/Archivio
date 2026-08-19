using Archivio.Domain;

namespace Archivio.Application.Abstractions;

public interface IMediaCatalogueService
{
    Task<IReadOnlyList<MediaItem>> GetByLibrarySourceAsync(
        Guid librarySourceId,
        CancellationToken cancellationToken = default);
}
