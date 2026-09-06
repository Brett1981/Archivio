using Archivio.Domain;
using Archivio.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Archivio.IntegrationTests;

public sealed class LibrarySourceRepositoryTests
{
    public LibrarySourceRepositoryTests()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    [Fact]
    public async Task AddAndGetById_RoundTripsLibrarySource()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var source = new LibrarySource(
            "Films",
            CreatePath("films"),
            LibrarySourceType.Movies,
            CreatePath("organised-films"));

        await fixture.Repository.AddAsync(source);
        await fixture.Repository.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var persisted = await fixture.Repository.GetByIdAsync(source.Id);

        Assert.NotNull(persisted);
        Assert.Equal(source.Id, persisted.Id);
        Assert.Equal("Films", persisted.Name);
        Assert.Equal(Path.GetFullPath(source.Path), persisted.Path);
        Assert.Equal(Path.GetFullPath(source.DestinationPath!), persisted.DestinationPath);
        Assert.Equal(persisted.DestinationPath, persisted.EffectiveDestinationPath);
        Assert.Equal(LibrarySourceType.Movies, persisted.Type);
        Assert.True(persisted.IsEnabled);
    }

    [Fact]
    public async Task GetAll_ReturnsSourcesOrderedByNameThenPath()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var sources = new[]
        {
            new LibrarySource("Television", CreatePath("television"), LibrarySourceType.Television),
            new LibrarySource("Films", CreatePath("films-b"), LibrarySourceType.Movies),
            new LibrarySource("Films", CreatePath("films-a"), LibrarySourceType.Movies)
        };

        foreach (var source in sources)
        {
            await fixture.Repository.AddAsync(source);
        }

        await fixture.Repository.SaveChangesAsync();

        var result = await fixture.Repository.GetAllAsync();

        Assert.Equal(3, result.Count);
        Assert.Equal("Films", result[0].Name);
        Assert.Equal("Films", result[1].Name);
        Assert.Equal("Television", result[2].Name);
        Assert.True(string.Compare(result[0].Path, result[1].Path, StringComparison.Ordinal) < 0);
    }

    [Fact]
    public async Task PathExists_NormalizesTrailingSeparatorAndHonoursExcludedId()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var source = new LibrarySource("Music", CreatePath("music"), LibrarySourceType.Music);
        await fixture.Repository.AddAsync(source);
        await fixture.Repository.SaveChangesAsync();

        var withTrailingSeparator = source.Path + Path.DirectorySeparatorChar;

        Assert.True(await fixture.Repository.PathExistsAsync(withTrailingSeparator));
        Assert.False(await fixture.Repository.PathExistsAsync(withTrailingSeparator, source.Id));
    }

    [Fact]
    public async Task Remove_DeletesPersistedSource()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var source = new LibrarySource("Documents", CreatePath("documents"), LibrarySourceType.Documents);
        await fixture.Repository.AddAsync(source);
        await fixture.Repository.SaveChangesAsync();

        fixture.Repository.Remove(source);
        await fixture.Repository.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        Assert.Null(await fixture.Repository.GetByIdAsync(source.Id));
    }

    [Fact]
    public async Task SaveChanges_RejectsDuplicatePathsAtDatabaseBoundary()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var path = CreatePath("duplicate");
        await fixture.Repository.AddAsync(new LibrarySource("First", path, LibrarySourceType.Mixed));
        await fixture.Repository.AddAsync(new LibrarySource("Second", path, LibrarySourceType.Documents));

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Repository.SaveChangesAsync());
    }

    private static string CreatePath(string suffix) =>
        Path.Combine(Path.GetTempPath(), "Archivio.Tests", Guid.NewGuid().ToString("N"), suffix);

    private sealed class RepositoryFixture : IAsyncDisposable
    {
        private RepositoryFixture(
            SqliteConnection connection,
            ArchivioDbContext context,
            LibrarySourceRepository repository)
        {
            Connection = connection;
            Context = context;
            Repository = repository;
        }

        private SqliteConnection Connection { get; }
        public ArchivioDbContext Context { get; }
        public LibrarySourceRepository Repository { get; }

        public static async Task<RepositoryFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<ArchivioDbContext>()
                .UseSqlite(connection)
                .Options;

            var context = new ArchivioDbContext(options);
            await context.Database.EnsureCreatedAsync();

            return new RepositoryFixture(connection, context, new LibrarySourceRepository(context));
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
