using Archivio.Application.Abstractions;
using Archivio.Domain;
using Archivio.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Archivio.IntegrationTests;

public sealed class AudiobookExecutionJournalStoreTests
{
    public AudiobookExecutionJournalStoreTests() => SQLitePCL.Batteries_V2.Init();

    [Fact]
    public async Task ExecutionRunAndOperations_RoundTripWithStatusUpdates()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ArchivioDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestDbContextFactory(options);
        var source = new LibrarySource(
            "Audiobooks",
            Path.Combine(Path.GetTempPath(), "Metaroq.Execution.Store.Tests", Guid.NewGuid().ToString("N")),
            LibrarySourceType.Audiobooks);
        await using (var context = factory.CreateDbContext())
        {
            await context.Database.EnsureCreatedAsync();
            context.LibrarySources.Add(source);
            await context.SaveChangesAsync();
        }

        var operationId = Guid.NewGuid();
        var started = DateTime.UtcNow;
        var run = new AudiobookExecutionRunEntry(
            Guid.NewGuid(),
            source.Id,
            AudiobookExecutionRunStatus.Prepared,
            1,
            0,
            0,
            started,
            started,
            null,
            null,
            [new AudiobookExecutionOperationEntry(
                operationId,
                0,
                "plan-1",
                "signature",
                Guid.NewGuid(),
                "Incoming\\Book.mp3",
                "Author\\Book\\Author - Book.mp3",
                AudiobookFileOperationKind.MoveAndRename,
                AudiobookExecutionOperationStatus.Pending,
                1234,
                started)]);
        var store = new AudiobookExecutionJournalStore(factory);

        await store.CreateAsync(run);
        await store.UpdateOperationAsync(
            operationId,
            AudiobookExecutionOperationStatus.Completed,
            null);
        await store.UpdateRunAsync(
            run.Id,
            AudiobookExecutionRunStatus.Completed,
            1,
            0,
            null,
            started.AddMinutes(1),
            started.AddMinutes(1));
        var loaded = await store.LoadLatestAsync(source.Id);

        Assert.NotNull(loaded);
        Assert.Equal(AudiobookExecutionRunStatus.Completed, loaded.Status);
        Assert.Equal(1, loaded.CompletedOperationCount);
        var operation = Assert.Single(loaded.Operations);
        Assert.Equal(AudiobookExecutionOperationStatus.Completed, operation.Status);
        Assert.Equal("signature", operation.InputSignature);
        Assert.Equal(1234, operation.SourceSizeBytes);
    }

    private sealed class TestDbContextFactory(
        DbContextOptions<ArchivioDbContext> options) : IDbContextFactory<ArchivioDbContext>
    {
        public ArchivioDbContext CreateDbContext() => new(options);

        public Task<ArchivioDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
