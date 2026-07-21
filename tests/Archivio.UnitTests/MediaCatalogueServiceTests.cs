using Archivio.Application.Abstractions;
using Archivio.Application.Services;
using Archivio.Domain;

namespace Archivio.UnitTests;

public sealed class MediaCatalogueServiceTests
{
    [Fact]
    public async Task GetByLibrarySourceAsync_OrdersAvailableItemsBeforeMissingItemsByRelativePath()
    {
        var sourceId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var availableB = CreateItem(sourceId, "B/movie.mkv", now);
        var availableA = CreateItem(sourceId, "A/photo.jpg", now);
        var missing = CreateItem(sourceId, "0/missing.mp4", now);
        missing.MarkMissing(now);
        var service = new MediaCatalogueService(new Repository([availableB, missing, availableA]));

        var result = await service.GetByLibrarySourceAsync(sourceId);

        Assert.Collection(
            result,
            item => Assert.Same(availableA, item),
            item => Assert.Same(availableB, item),
            item => Assert.Same(missing, item));
    }

    [Fact]
    public async Task GetByLibrarySourceAsync_RejectsEmptySourceId()
    {
        var service = new MediaCatalogueService(new Repository([]));

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetByLibrarySourceAsync(Guid.Empty));
    }

    private static MediaItem CreateItem(Guid sourceId, string relativePath, DateTime now)
    {
        var root = Path.Combine(Path.GetTempPath(), "Archivio.Tests", Guid.NewGuid().ToString("N"));
        return new MediaItem(sourceId, Path.Combine(root, relativePath), relativePath, 1024, now, now, now);
    }

    private sealed class Repository(IReadOnlyList<MediaItem> items) : IMediaItemRepository
    {
        public Task<IReadOnlyList<MediaItem>> GetByLibrarySourceIdAsync(
            Guid librarySourceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MediaItem>>(items.Where(item => item.LibrarySourceId == librarySourceId).ToList());

        public Task<MediaItem?> GetByPathAsync(
            Guid librarySourceId,
            string fullPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(items.SingleOrDefault(item =>
                item.LibrarySourceId == librarySourceId &&
                string.Equals(item.FullPath, fullPath, StringComparison.OrdinalIgnoreCase)));

        public Task AddRangeAsync(IEnumerable<MediaItem> values, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
