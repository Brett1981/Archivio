using Archivio.Application.Abstractions;
using Archivio.Application.Services;
using Archivio.Domain;

namespace Archivio.UnitTests;

public sealed class LibraryScanProgressTests
{
    [Fact]
    public async Task ScanAsync_ReportsLifecycleAndFinalCounts()
    {
        var source = new LibrarySource(
            "Progress library",
            Path.Combine(Path.GetTempPath(), "Archivio.Tests", Guid.NewGuid().ToString("N")),
            LibrarySourceType.Mixed);
        var now = DateTime.UtcNow;
        var file = new DiscoveredFile(
            Path.Combine(source.Path, "movie.mkv"),
            "movie.mkv",
            ".mkv",
            1024,
            now);
        var progressValues = new List<LibraryScanProgress>();
        var progress = new InlineProgress<LibraryScanProgress>(progressValues.Add);
        var service = new LibraryScanService(
            new SourceRepository(source),
            new MediaRepository(),
            new DiscoveryService(new FileDiscoveryResult(source.Path, [file], [], now, now)));

        var result = await service.ScanAsync(source.Id, progress);

        Assert.Equal(1, result.AddedCount);
        Assert.Equal(LibraryScanStage.Starting, progressValues.First().Stage);
        Assert.Contains(progressValues, value => value.Stage == LibraryScanStage.Discovering);
        Assert.Contains(progressValues, value =>
            value.Stage == LibraryScanStage.Discovering &&
            value.DiscoveredCount == 1 &&
            value.CurrentPath == file.FullPath);
        Assert.Contains(progressValues, value => value.Stage == LibraryScanStage.Reconciling && value.ProcessedCount == 1);
        Assert.Contains(progressValues, value => value.Stage == LibraryScanStage.Saving);
        var completed = Assert.Single(progressValues, value => value.Stage == LibraryScanStage.Completed);
        Assert.Equal(1, completed.DiscoveredCount);
        Assert.Equal(1, completed.AddedCount);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class DiscoveryService(FileDiscoveryResult result) : IFileDiscoveryService
    {
        public Task<FileDiscoveryResult> DiscoverAsync(string rootPath, CancellationToken cancellationToken = default) =>
            DiscoverAsync(rootPath, FileDiscoveryOptions.Default, null, cancellationToken);

        public Task<FileDiscoveryResult> DiscoverAsync(
            string rootPath,
            FileDiscoveryOptions options,
            IProgress<FileDiscoveryProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            progress?.Report(new FileDiscoveryProgress(result.Files[0].FullPath, result.Files.Count, 2, 0));
            return Task.FromResult(result);
        }
    }

    private sealed class SourceRepository(LibrarySource source) : ILibrarySourceRepository
    {
        public Task<IReadOnlyList<LibrarySource>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LibrarySource>>([source]);
        public Task<LibrarySource?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<LibrarySource?>(id == source.Id ? source : null);
        public Task<bool> PathExistsAsync(string path, Guid? excludingId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
        public Task AddAsync(LibrarySource value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Remove(LibrarySource value) { }
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class MediaRepository : IMediaItemRepository
    {
        public Task<IReadOnlyList<MediaItem>> GetByLibrarySourceIdAsync(Guid librarySourceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MediaItem>>([]);
        public Task<MediaItem?> GetByPathAsync(Guid librarySourceId, string fullPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<MediaItem?>(null);
        public Task AddRangeAsync(IEnumerable<MediaItem> items, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(1);
    }
}
