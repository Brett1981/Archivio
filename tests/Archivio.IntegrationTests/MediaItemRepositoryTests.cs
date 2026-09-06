using Archivio.Domain;
using Archivio.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Archivio.IntegrationTests;

public sealed class MediaItemRepositoryTests
{
    public MediaItemRepositoryTests()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    [Fact]
    public async Task AddRangeAndGetByLibrarySource_RoundTripsCatalogueItemsInRelativePathOrder()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var source = await fixture.AddSourceAsync("Films");
        var now = DateTime.UtcNow;
        var second = CreateItem(source.Id, source.Path, "B.mkv", 20, now);
        var first = CreateItem(source.Id, source.Path, Path.Combine("A", "A.mkv"), 10, now);

        await fixture.Repository.AddRangeAsync([second, first]);
        await fixture.Repository.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var result = await fixture.Repository.GetByLibrarySourceIdAsync(source.Id);

        Assert.Equal(2, result.Count);
        Assert.Equal(first.RelativePath, result[0].RelativePath);
        Assert.Equal(second.RelativePath, result[1].RelativePath);
        Assert.All(result, item => Assert.Equal(source.Id, item.LibrarySourceId));
    }

    [Fact]
    public async Task GetByPath_NormalizesInputPath()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var source = await fixture.AddSourceAsync("Photos");
        var now = DateTime.UtcNow;
        var item = CreateItem(source.Id, source.Path, Path.Combine("Trips", "photo.JPG"), 100, now);
        await fixture.Repository.AddRangeAsync([item]);
        await fixture.Repository.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var result = await fixture.Repository.GetByPathAsync(source.Id, $"  {item.FullPath}  ");

        Assert.NotNull(result);
        Assert.Equal(item.Id, result.Id);
        Assert.Equal(".jpg", result.Extension);
    }

    [Fact]
    public async Task SaveChanges_PersistsRefreshAndMissingStateChanges()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var source = await fixture.AddSourceAsync("Documents");
        var now = DateTime.UtcNow;
        var item = CreateItem(source.Id, source.Path, "document.pdf", 50, now);
        await fixture.Repository.AddRangeAsync([item]);
        await fixture.Repository.SaveChangesAsync();

        item.MarkMissing(now.AddMinutes(1));
        await fixture.Repository.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var missing = await fixture.Repository.GetByPathAsync(source.Id, item.FullPath);
        Assert.NotNull(missing);
        Assert.True(missing.IsMissing);

        missing.Refresh(missing.FullPath, missing.RelativePath, 75, missing.CreatedAtUtc, now.AddMinutes(2), now.AddMinutes(3));
        await fixture.Repository.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var refreshed = await fixture.Repository.GetByPathAsync(source.Id, item.FullPath);
        Assert.NotNull(refreshed);
        Assert.False(refreshed.IsMissing);
        Assert.Equal(75, refreshed.SizeBytes);
        Assert.Equal(now.AddMinutes(3), refreshed.LastScannedAtUtc);
    }

    [Fact]
    public async Task GetByLibrarySource_ReloadsMediaChangedByBackgroundScanContext()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var source = await fixture.AddSourceAsync("Audiobooks");
        var now = DateTime.UtcNow;
        var original = CreateItem(source.Id, source.Path, "Original.m4b", 50, now);
        await fixture.Repository.AddRangeAsync([original]);
        await fixture.Repository.SaveChangesAsync();

        var initial = await fixture.Repository.GetByLibrarySourceIdAsync(source.Id);
        Assert.False(Assert.Single(initial).IsMissing);

        await using (var backgroundContext = fixture.CreateAdditionalContext())
        {
            var backgroundOriginal = await backgroundContext.MediaItems.SingleAsync(item => item.Id == original.Id);
            backgroundOriginal.MarkMissing(now.AddMinutes(1));
            backgroundContext.MediaItems.Add(CreateItem(
                source.Id,
                source.Path,
                Path.Combine("Organised", "Original.m4b"),
                50,
                now.AddMinutes(1)));
            await backgroundContext.SaveChangesAsync();
        }

        var reloaded = await fixture.Repository.GetByLibrarySourceIdAsync(source.Id);

        Assert.Equal(2, reloaded.Count);
        Assert.True(reloaded.Single(item => item.Id == original.Id).IsMissing);
        Assert.False(reloaded.Single(item => item.Id != original.Id).IsMissing);
    }

    [Fact]
    public async Task SaveChanges_RejectsDuplicatePathWithinSameLibrarySource()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var source = await fixture.AddSourceAsync("Music");
        var now = DateTime.UtcNow;
        var first = CreateItem(source.Id, source.Path, "track.flac", 10, now);
        var duplicate = CreateItem(source.Id, source.Path, "track.flac", 20, now);

        await fixture.Repository.AddRangeAsync([first, duplicate]);

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Repository.SaveChangesAsync());
    }

    [Fact]
    public async Task RemovingLibrarySource_CascadeDeletesItsCatalogueItemsOnly()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var firstSource = await fixture.AddSourceAsync("First");
        var secondSource = await fixture.AddSourceAsync("Second");
        var now = DateTime.UtcNow;
        await fixture.Repository.AddRangeAsync([
            CreateItem(firstSource.Id, firstSource.Path, "first.bin", 1, now),
            CreateItem(secondSource.Id, secondSource.Path, "second.bin", 2, now)]);
        await fixture.Repository.SaveChangesAsync();

        fixture.Context.LibrarySources.Remove(firstSource);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        Assert.Empty(await fixture.Repository.GetByLibrarySourceIdAsync(firstSource.Id));
        Assert.Single(await fixture.Repository.GetByLibrarySourceIdAsync(secondSource.Id));
    }

    private static MediaItem CreateItem(Guid sourceId, string rootPath, string relativePath, long size, DateTime now) =>
        new(sourceId, Path.Combine(rootPath, relativePath), relativePath, size, now.AddDays(-1), now, now);

    private sealed class RepositoryFixture : IAsyncDisposable
    {
        private RepositoryFixture(SqliteConnection connection, ArchivioDbContext context)
        {
            Connection = connection;
            Context = context;
            Repository = new MediaItemRepository(context);
        }

        private SqliteConnection Connection { get; }
        public ArchivioDbContext Context { get; }
        public MediaItemRepository Repository { get; }

        public static async Task<RepositoryFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ArchivioDbContext>().UseSqlite(connection).Options;
            var context = new ArchivioDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new RepositoryFixture(connection, context);
        }

        public async Task<LibrarySource> AddSourceAsync(string name)
        {
            var path = Path.Combine(Path.GetTempPath(), "Archivio.Tests", Guid.NewGuid().ToString("N"), name);
            var source = new LibrarySource(name, path, LibrarySourceType.Mixed);
            Context.LibrarySources.Add(source);
            await Context.SaveChangesAsync();
            return source;
        }

        public ArchivioDbContext CreateAdditionalContext()
        {
            var options = new DbContextOptionsBuilder<ArchivioDbContext>().UseSqlite(Connection).Options;
            return new ArchivioDbContext(options);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
