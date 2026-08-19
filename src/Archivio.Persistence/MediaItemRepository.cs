using Archivio.Application.Abstractions;
using Archivio.Domain;
using Microsoft.EntityFrameworkCore;

namespace Archivio.Persistence;

internal sealed class MediaItemRepository(ArchivioDbContext dbContext) : IMediaItemRepository
{
    public async Task<IReadOnlyList<MediaItem>> GetByLibrarySourceIdAsync(
        Guid librarySourceId,
        CancellationToken cancellationToken = default)
    {
        DetachTrackedMediaItems();
        return await dbContext.MediaItems
            .Where(item => item.LibrarySourceId == librarySourceId)
            .OrderBy(item => item.RelativePath)
            .ToListAsync(cancellationToken);
    }

    public Task<MediaItem?> GetByPathAsync(
        Guid librarySourceId,
        string fullPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        var normalizedPath = Path.GetFullPath(fullPath.Trim());

        DetachTrackedMediaItems();
        return dbContext.MediaItems.SingleOrDefaultAsync(
            item => item.LibrarySourceId == librarySourceId && item.FullPath == normalizedPath,
            cancellationToken);
    }

    public Task AddRangeAsync(IEnumerable<MediaItem> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        return dbContext.MediaItems.AddRangeAsync(items, cancellationToken);
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        dbContext.SaveChangesAsync(cancellationToken);

    private void DetachTrackedMediaItems()
    {
        foreach (var entry in dbContext.ChangeTracker.Entries<MediaItem>().ToList())
        {
            entry.State = EntityState.Detached;
        }
    }
}
