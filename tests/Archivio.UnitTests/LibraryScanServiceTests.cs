using Archivio.Application.Abstractions;
using Archivio.Application.Services;
using Archivio.Domain;

namespace Archivio.UnitTests;

public sealed class LibraryScanServiceTests
{
    [Fact]
    public async Task ScanAsync_AddsRefreshesAndMarksMissingItemsInSingleSave()
    {
        var source = CreateSource();
        var now = DateTime.UtcNow;
        var existingPath = Path.Combine(source.Path, "existing.mp4");
        var missingPath = Path.Combine(source.Path, "missing.mp4");
        var existing = new MediaItem(source.Id, existingPath, "existing.mp4", 10, now.AddDays(-2), now.AddDays(-1), now.AddHours(-1));
        var missing = new MediaItem(source.Id, missingPath, "missing.mp4", 20, now.AddDays(-2), now.AddDays(-1), now.AddHours(-1));
        var repository = new FakeMediaItemRepository([existing, missing]);
        var discovery = new FakeFileDiscoveryService(new FileDiscoveryResult(
            source.Path,
            [
                new DiscoveredFile(existingPath, "existing.mp4", ".mp4", 100, now.AddMinutes(-5)),
                new DiscoveredFile(Path.Combine(source.Path, "new.mkv"), "new.mkv", ".mkv", 200, now.AddMinutes(-3))
            ],
            [],
            now.AddSeconds(-2),
            now));
        var service = CreateService(source, repository, discovery);

        var result = await service.ScanAsync(source.Id);

        Assert.Equal(2, result.DiscoveredCount);
        Assert.Equal(1, result.AddedCount);
        Assert.Equal(1, result.RefreshedCount);
        Assert.Equal(1, result.MissingCount);
        Assert.Equal(100, existing.SizeBytes);
        Assert.False(existing.IsMissing);
        Assert.True(missing.IsMissing);
        Assert.Single(repository.AddedItems);
        Assert.Equal("new.mkv", repository.AddedItems[0].RelativePath);
        Assert.Equal(1, repository.SaveChangesCallCount);
    }

    [Fact]
    public async Task ScanAsync_RestoresPreviouslyMissingItemWhenRediscovered()
    {
        var source = CreateSource();
        var now = DateTime.UtcNow;
        var path = Path.Combine(source.Path, "restored.jpg");
        var item = new MediaItem(source.Id, path, "restored.jpg", 10, now.AddDays(-2), now.AddDays(-1), now.AddHours(-2));
        item.MarkMissing(now.AddHours(-1));
        var repository = new FakeMediaItemRepository([item]);
        var discovery = new FakeFileDiscoveryService(new FileDiscoveryResult(
            source.Path,
            [new DiscoveredFile(path, "restored.jpg", ".jpg", 25, now.AddMinutes(-1))],
            [],
            now.AddSeconds(-1),
            now));

        var result = await CreateService(source, repository, discovery).ScanAsync(source.Id);

        Assert.Equal(1, result.RefreshedCount);
        Assert.Equal(0, result.MissingCount);
        Assert.False(item.IsMissing);
        Assert.Equal(25, item.SizeBytes);
    }

    [Fact]
    public async Task ScanAsync_RejectsUnknownAndDisabledSourcesBeforeDiscovery()
    {
        var source = CreateSource();
        source.SetEnabled(false);
        var discovery = new FakeFileDiscoveryService(CreateEmptyDiscovery(source.Path));
        var repository = new FakeMediaItemRepository([]);

        var unknownService = new LibraryScanService(new FakeLibrarySourceRepository(null), repository, discovery);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => unknownService.ScanAsync(Guid.NewGuid()));

        var disabledService = CreateService(source, repository, discovery);
        await Assert.ThrowsAsync<InvalidOperationException>(() => disabledService.ScanAsync(source.Id));
        Assert.Equal(0, discovery.CallCount);
    }

    [Fact]
    public async Task ScanAsync_PreservesDiscoveryIssuesInResult()
    {
        var source = CreateSource();
        var now = DateTime.UtcNow;
        var issue = new FileDiscoveryIssue(Path.Combine(source.Path, "locked"), "Access denied");
        var discovery = new FakeFileDiscoveryService(new FileDiscoveryResult(source.Path, [], [issue], now, now));

        var result = await CreateService(source, new FakeMediaItemRepository([]), discovery).ScanAsync(source.Id);

        Assert.Single(result.Issues);
        Assert.Equal(issue, result.Issues[0]);
    }

    private static LibraryScanService CreateService(
        LibrarySource source,
        FakeMediaItemRepository repository,
        FakeFileDiscoveryService discovery) =>
        new(new FakeLibrarySourceRepository(source), repository, discovery);

    private static LibrarySource CreateSource() =>
        new("Test library", Path.Combine(Path.GetTempPath(), "Archivio.Tests", Guid.NewGuid().ToString("N")), LibrarySourceType.Mixed);

    private static FileDiscoveryResult CreateEmptyDiscovery(string rootPath)
    {
        var now = DateTime.UtcNow;
        return new FileDiscoveryResult(rootPath, [], [], now, now);
    }

    private sealed class FakeFileDiscoveryService(FileDiscoveryResult result) : IFileDiscoveryService
    {
        public int CallCount { get; private set; }

        public Task<FileDiscoveryResult> DiscoverAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeLibrarySourceRepository(LibrarySource? source) : ILibrarySourceRepository
    {
        public Task<IReadOnlyList<LibrarySource>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LibrarySource>>(source is null ? [] : [source]);

        public Task<LibrarySource?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(source?.Id == id ? source : null);

        public Task<bool> PathExistsAsync(string path, Guid? excludingId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task AddAsync(LibrarySource source, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Remove(LibrarySource source) { }
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class FakeMediaItemRepository(IEnumerable<MediaItem> items) : IMediaItemRepository
    {
        private readonly List<MediaItem> _items = [.. items];
        public List<MediaItem> AddedItems { get; } = [];
        public int SaveChangesCallCount { get; private set; }

        public Task<IReadOnlyList<MediaItem>> GetByLibrarySourceIdAsync(Guid librarySourceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MediaItem>>(_items.Where(item => item.LibrarySourceId == librarySourceId).ToList());

        public Task<MediaItem?> GetByPathAsync(Guid librarySourceId, string fullPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.SingleOrDefault(item => item.LibrarySourceId == librarySourceId && item.FullPath == fullPath));

        public Task AddRangeAsync(IEnumerable<MediaItem> items, CancellationToken cancellationToken = default)
        {
            var added = items.ToList();
            AddedItems.AddRange(added);
            _items.AddRange(added);
            return Task.CompletedTask;
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesCallCount++;
            return Task.FromResult(1);
        }
    }
}
