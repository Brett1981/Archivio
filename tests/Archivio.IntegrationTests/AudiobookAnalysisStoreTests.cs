using Archivio.Application.Abstractions;
using Archivio.Domain;
using Archivio.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Archivio.IntegrationTests;

public sealed class AudiobookAnalysisStoreTests
{
    public AudiobookAnalysisStoreTests()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    [Fact]
    public async Task CompletedAnalysisAndMetadataCache_RoundTripAndRejectChangedMedia()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ArchivioDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestDbContextFactory(options);
        var now = DateTime.UtcNow;
        var source = new LibrarySource(
            "Audiobooks",
            Path.Combine(Path.GetTempPath(), "Archivio.Tests", Guid.NewGuid().ToString("N")),
            LibrarySourceType.Audiobooks);
        var item = new MediaItem(
            source.Id,
            Path.Combine(source.Path, "Author - Book.m4b"),
            "Author - Book.m4b",
            1234,
            now.AddDays(-1),
            now,
            now);

        await using (var context = factory.CreateDbContext())
        {
            await context.Database.EnsureCreatedAsync();
            context.LibrarySources.Add(source);
            context.MediaItems.Add(item);
            await context.SaveChangesAsync();
        }

        var metadata = CreateMetadata(item.FullPath);
        var cacheEntry = new AudiobookMetadataCacheEntry(
            item.Id,
            item.SizeBytes,
            item.ModifiedAtUtc,
            now,
            metadata);
        var candidate = new AudiobookCandidateGroup(
            "Author - Book",
            "Author",
            "Book",
            MetadataValueSource.EmbeddedTag,
            MetadataValueSource.EmbeddedTag,
            true,
            [new AudiobookCandidatePart(item, 1, false, metadata)],
            1.0m,
            []);
        var store = new AudiobookAnalysisStore(factory);

        await store.BeginAnalysisAsync(source.Id, 1, 0, 0);
        await store.SaveCheckpointAsync(source.Id, [cacheEntry], 1, 1, 0);
        await store.CompleteAnalysisAsync(source.Id, [candidate], 1, 1, 0);
        var onlineSuggestion = new OnlineMetadataSuggestion(
            "Open Library",
            "/works/OL1W",
            "Book",
            ["Author"],
            2000,
            ["Fiction"],
            null,
            "https://openlibrary.org/works/OL1W",
            1m,
            "Exact match",
            now);
        await store.SaveOnlineMetadataCacheAsync(
            source.Id,
            [new OnlineMetadataCacheEntry(
                candidate.CandidateKey,
                OnlineMetadataIdentity.CreateInputSignature(candidate.Title, candidate.Author),
                now,
                onlineSuggestion)]);
        var organisationProposal = new AudiobookOrganisationProposal(
            "plan-key",
            "Author",
            "Book",
            2000,
            "Fiction",
            Path.Combine("Fiction", "Author", "Book (2000)"),
            "Author - Book.m4b",
            AudiobookOrganisationAction.MoveAndRename,
            1,
            1,
            true,
            true,
            1m,
            true,
            false,
            ["Canonical identity came from Open Library."],
            [],
            now);
        var organisationStore = new AudiobookOrganisationStore(factory);
        await organisationStore.SaveAsync(
            source.Id,
            [new AudiobookOrganisationCacheEntry(
                candidate.CandidateKey,
                "proposal-signature",
                now,
                organisationProposal)]);
        var batchDecisionStore = new AudiobookBatchDecisionStore(factory);
        await batchDecisionStore.SaveAsync(
            source.Id,
            [new AudiobookBatchDecisionEntry(
                organisationProposal.PlanKey,
                "batch-signature",
                AudiobookBatchDecision.Approved,
                now)]);

        var cached = await store.LoadMetadataCacheAsync(source.Id);
        var onlineCached = await store.LoadOnlineMetadataCacheAsync(source.Id);
        var organisationCached = await organisationStore.LoadAsync(source.Id);
        var batchDecisions = await batchDecisionStore.LoadAsync(source.Id);
        var saved = await store.LoadCompletedAnalysisAsync(source.Id, [item]);

        Assert.Equal("Book", cached[item.Id].Metadata.Title.Value);
        Assert.NotNull(saved);
        Assert.Equal("Author - Book", Assert.Single(saved.Candidates).DisplayName);
        Assert.Equal("Book", Assert.Single(saved.Candidates).OnlineSuggestion?.Title);
        Assert.Equal("Book", onlineCached[candidate.CandidateKey].Suggestion?.Title);
        Assert.Equal(
            "Book",
            organisationCached[candidate.CandidateKey].Proposal.CanonicalTitle);
        Assert.Equal(
            AudiobookBatchDecision.Approved,
            batchDecisions[organisationProposal.PlanKey].Decision);
        Assert.Equal(item.Id, Assert.Single(Assert.Single(saved.Candidates).Parts).MediaItem.Id);

        item.Refresh(
            item.FullPath,
            item.RelativePath,
            item.SizeBytes + 1,
            item.CreatedAtUtc,
            item.ModifiedAtUtc.AddMinutes(1),
            now.AddMinutes(1));

        Assert.Null(await store.LoadCompletedAnalysisAsync(source.Id, [item]));
    }

    private static LocalMediaMetadata CreateMetadata(string path) =>
        new(
            path,
            new MetadataValue("Book", MetadataValueSource.EmbeddedTag),
            new MetadataValue("Author", MetadataValueSource.EmbeddedTag),
            new MetadataValue(null, MetadataValueSource.None),
            new MetadataValue(null, MetadataValueSource.None),
            null,
            null,
            TimeSpan.FromHours(1),
            64,
            44100,
            2,
            "AAC",
            false,
            [],
            []);

    private sealed class TestDbContextFactory(
        DbContextOptions<ArchivioDbContext> options) : IDbContextFactory<ArchivioDbContext>
    {
        public ArchivioDbContext CreateDbContext() => new(options);

        public Task<ArchivioDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
