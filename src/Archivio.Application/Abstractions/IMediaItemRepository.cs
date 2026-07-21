using Archivio.Domain;

namespace Archivio.Application.Abstractions;

public interface IMediaItemRepository
{
    Task<IReadOnlyList<MediaItem>> GetByLibrarySourceIdAsync(Guid librarySourceId, CancellationToken cancellationToken = default);
    Task<MediaItem?> GetByPathAsync(Guid librarySourceId, string fullPath, CancellationToken cancellationToken = default);
    Task AddRangeAsync(IEnumerable<MediaItem> items, CancellationToken cancellationToken = default);
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
