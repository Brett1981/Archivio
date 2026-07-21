using Archivio.Application.Abstractions;
using Archivio.Domain;

namespace Archivio.Application.Services;

public sealed class MediaCatalogueService(IMediaItemRepository mediaItemRepository) : IMediaCatalogueService
{
    public async Task<IReadOnlyList<MediaItem>> GetByLibrarySourceAsync(
        Guid librarySourceId,
        CancellationToken cancellationToken = default)
    {
        if (librarySourceId == Guid.Empty)
        {
            throw new ArgumentException("Library source id is required.", nameof(librarySourceId));
        }

        var items = await mediaItemRepository.GetByLibrarySourceIdAsync(librarySourceId, cancellationToken);

        return items
            .OrderBy(item => item.IsMissing)
            .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
