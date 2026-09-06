using Archivio.Application.Abstractions;
using Archivio.Application.Services;
using Archivio.Domain;

namespace Archivio.UnitTests;

public sealed class LibrarySourceServiceTests
{
    [Fact]
    public async Task CreateAsync_AddsAndSavesValidSource()
    {
        var repository = new FakeLibrarySourceRepository();
        var directoryService = new FakeDirectoryService(true);
        var service = new LibrarySourceService(repository, directoryService);
        var path = Path.Combine(Path.GetTempPath(), "Archivio", "Documents");

        var source = await service.CreateAsync(" Documents ", path, LibrarySourceType.Documents);

        Assert.Single(repository.Sources);
        Assert.Same(source, repository.Sources[0]);
        Assert.Equal("Documents", source.Name);
        Assert.Equal(Path.GetFullPath(path), source.Path);
        Assert.Equal(1, repository.SaveChangesCallCount);
    }

    [Fact]
    public async Task CreateAsync_SavesSeparateExistingDestination()
    {
        var repository = new FakeLibrarySourceRepository();
        var service = new LibrarySourceService(repository, new FakeDirectoryService(true));
        var sourcePath = Path.Combine(Path.GetTempPath(), "Incoming");
        var destinationPath = Path.Combine(Path.GetTempPath(), "Organised");

        var source = await service.CreateAsync(
            "Audiobooks",
            sourcePath,
            LibrarySourceType.Audiobooks,
            destinationPath);

        Assert.Equal(Path.GetFullPath(destinationPath), source.DestinationPath);
        Assert.Equal(source.DestinationPath, source.EffectiveDestinationPath);
    }

    [Fact]
    public async Task CreateAsync_RejectsMissingDirectory()
    {
        var repository = new FakeLibrarySourceRepository();
        var service = new LibrarySourceService(repository, new FakeDirectoryService(false));

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            service.CreateAsync("Documents", Path.Combine(Path.GetTempPath(), "Missing"), LibrarySourceType.Documents));

        Assert.Empty(repository.Sources);
        Assert.Equal(0, repository.SaveChangesCallCount);
    }

    [Fact]
    public async Task CreateAsync_RejectsDuplicatePath()
    {
        var path = Path.Combine(Path.GetTempPath(), "Archivio", "Music");
        var existing = new LibrarySource("Music", path, LibrarySourceType.Music);
        var repository = new FakeLibrarySourceRepository(existing);
        var service = new LibrarySourceService(repository, new FakeDirectoryService(true));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync("More music", path, LibrarySourceType.Music));

        Assert.Single(repository.Sources);
        Assert.Equal(0, repository.SaveChangesCallCount);
    }

    [Fact]
    public async Task UpdateAsync_UpdatesAndSavesExistingSource()
    {
        var originalPath = Path.Combine(Path.GetTempPath(), "Archivio", "Photos");
        var updatedPath = Path.Combine(Path.GetTempPath(), "Archivio", "Pictures");
        var source = new LibrarySource("Photos", originalPath, LibrarySourceType.Photos);
        var repository = new FakeLibrarySourceRepository(source);
        var service = new LibrarySourceService(repository, new FakeDirectoryService(true));

        var updated = await service.UpdateAsync(
            source.Id,
            "Pictures",
            updatedPath,
            LibrarySourceType.Mixed,
            false);

        Assert.Same(source, updated);
        Assert.Equal("Pictures", source.Name);
        Assert.Equal(Path.GetFullPath(updatedPath), source.Path);
        Assert.Equal(LibrarySourceType.Mixed, source.Type);
        Assert.False(source.IsEnabled);
        Assert.Equal(1, repository.SaveChangesCallCount);
    }

    [Fact]
    public async Task UpdateAsync_AllowsExistingSourceToKeepItsOwnPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "Archivio", "Movies");
        var source = new LibrarySource("Movies", path, LibrarySourceType.Movies);
        var repository = new FakeLibrarySourceRepository(source);
        var service = new LibrarySourceService(repository, new FakeDirectoryService(true));

        await service.UpdateAsync(source.Id, "Films", path, LibrarySourceType.Movies, true);

        Assert.Equal("Films", source.Name);
        Assert.Equal(1, repository.SaveChangesCallCount);
    }

    [Fact]
    public async Task UpdateAsync_RejectsPathUsedByAnotherSource()
    {
        var first = new LibrarySource("Movies", Path.Combine(Path.GetTempPath(), "Movies"), LibrarySourceType.Movies);
        var second = new LibrarySource("TV", Path.Combine(Path.GetTempPath(), "TV"), LibrarySourceType.Television);
        var repository = new FakeLibrarySourceRepository(first, second);
        var service = new LibrarySourceService(repository, new FakeDirectoryService(true));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateAsync(first.Id, first.Name, second.Path, first.Type, first.IsEnabled));

        Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Movies")), first.Path);
        Assert.Equal(0, repository.SaveChangesCallCount);
    }

    [Fact]
    public async Task UpdateAsync_RejectsUnknownSource()
    {
        var service = new LibrarySourceService(
            new FakeLibrarySourceRepository(),
            new FakeDirectoryService(true));

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.UpdateAsync(Guid.NewGuid(), "Unknown", Path.GetTempPath(), LibrarySourceType.Mixed, true));
    }

    [Fact]
    public async Task DeleteAsync_RemovesAndSavesExistingSource()
    {
        var source = new LibrarySource("Audiobooks", Path.Combine(Path.GetTempPath(), "Audiobooks"), LibrarySourceType.Audiobooks);
        var repository = new FakeLibrarySourceRepository(source);
        var service = new LibrarySourceService(repository, new FakeDirectoryService(true));

        await service.DeleteAsync(source.Id);

        Assert.Empty(repository.Sources);
        Assert.Equal(1, repository.SaveChangesCallCount);
    }

    [Fact]
    public async Task DeleteAsync_RejectsUnknownSource()
    {
        var repository = new FakeLibrarySourceRepository();
        var service = new LibrarySourceService(repository, new FakeDirectoryService(true));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.DeleteAsync(Guid.NewGuid()));

        Assert.Equal(0, repository.SaveChangesCallCount);
    }

    private sealed class FakeDirectoryService(bool exists) : IDirectoryService
    {
        public bool Exists(string path) => exists;
    }

    private sealed class FakeLibrarySourceRepository(params LibrarySource[] sources) : ILibrarySourceRepository
    {
        public List<LibrarySource> Sources { get; } = [.. sources];
        public int SaveChangesCallCount { get; private set; }

        public Task<IReadOnlyList<LibrarySource>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LibrarySource>>(Sources.ToArray());

        public Task<LibrarySource?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Sources.SingleOrDefault(source => source.Id == id));

        public Task<bool> PathExistsAsync(
            string path,
            Guid? excludingId = null,
            CancellationToken cancellationToken = default)
        {
            var normalizedPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var exists = Sources.Any(source =>
                source.Id != excludingId &&
                string.Equals(source.Path, normalizedPath, StringComparison.OrdinalIgnoreCase));

            return Task.FromResult(exists);
        }

        public Task AddAsync(LibrarySource source, CancellationToken cancellationToken = default)
        {
            Sources.Add(source);
            return Task.CompletedTask;
        }

        public void Remove(LibrarySource source) => Sources.Remove(source);

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesCallCount++;
            return Task.FromResult(1);
        }
    }
}
