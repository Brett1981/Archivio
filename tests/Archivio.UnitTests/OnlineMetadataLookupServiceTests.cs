using System.Net;
using System.Text;
using Archivio.Application.Abstractions;
using Archivio.Application.Services;
using Archivio.Domain;

namespace Archivio.UnitTests;

public sealed class OnlineMetadataLookupServiceTests
{
    [Fact]
    public async Task EnrichCandidates_AcceptsAConservativeExactTitleMatchAndCachesIt()
    {
        var candidate = CreateCandidate("Pride and Prejudice", author: null, confidence: 0.60m);
        var provider = new StubBookMetadataProvider
        {
            Results =
            [
                CreateResult("/works/OL1W", "Pride and Prejudice", "Jane Austen"),
                CreateResult("/works/OL2W", "Pride and Prejudice and Zombies", "Seth Grahame-Smith")
            ]
        };
        var store = new StubAnalysisStore();
        var service = new OnlineMetadataLookupService(provider, store);

        var result = await service.EnrichCandidatesAsync(
            candidate.Parts[0].MediaItem.LibrarySourceId,
            [candidate]);

        var suggestion = Assert.Single(result).OnlineSuggestion;
        Assert.NotNull(suggestion);
        Assert.Equal("Pride and Prejudice", suggestion.Title);
        Assert.Equal("Open Library", suggestion.ProviderName);
        Assert.Single(store.SavedOnlineEntries);
    }

    [Fact]
    public async Task EnrichCandidates_RejectsAmbiguousMatches()
    {
        var candidate = CreateCandidate("The Complete Stand", "Stephen King", 0.70m);
        var provider = new StubBookMetadataProvider
        {
            Results =
            [
                CreateResult("/works/OL1W", "The Stand Complete", "Stephen King"),
                CreateResult("/works/OL2W", "Complete The Stand", "Stephen King")
            ]
        };
        var store = new StubAnalysisStore();
        var service = new OnlineMetadataLookupService(provider, store);

        var result = await service.EnrichCandidatesAsync(
            candidate.Parts[0].MediaItem.LibrarySourceId,
            [candidate]);

        Assert.Null(Assert.Single(result).OnlineSuggestion);
        Assert.Null(Assert.Single(store.SavedOnlineEntries).Suggestion);
    }

    [Fact]
    public async Task EnrichCandidates_AcceptsEquivalentDuplicateProviderRecords()
    {
        var candidate = CreateCandidate("Hyperion", "Dan Simmons", 0.70m);
        var provider = new StubBookMetadataProvider
        {
            Results =
            [
                CreateResult("/works/OL1W", "Hyperion", "Dan Simmons"),
                CreateResult("/works/OL2W", "Hyperion", "Dan Simmons")
            ]
        };
        var service = new OnlineMetadataLookupService(provider, new StubAnalysisStore());

        var result = await service.EnrichCandidatesAsync(
            candidate.Parts[0].MediaItem.LibrarySourceId,
            [candidate]);

        Assert.Equal("Hyperion", Assert.Single(result).OnlineSuggestion?.Title);
    }

    [Fact]
    public async Task EnrichCandidates_SearchesByTitleAndUsesInferredAuthorAsRankingHint()
    {
        var candidate = CreateCandidate("Rage", "Stephen King", 1m) with
        {
            OrganisationProposal = CreateProposal(
                "rage",
                "Stephen King",
                "Rage",
                isPrimary: true)
        };
        var provider = new StubBookMetadataProvider
        {
            Results =
            [
                CreateResult("/works/OL1W", "Rage", "Different Author"),
                CreateResult("/works/OL2W", "Rage", "Stephen King")
            ]
        };
        var service = new OnlineMetadataLookupService(provider, new StubAnalysisStore());

        var result = await service.EnrichCandidatesAsync(
            candidate.Parts[0].MediaItem.LibrarySourceId,
            [candidate]);

        var query = Assert.Single(Assert.Single(provider.QueryBatches));
        Assert.True(query.SearchByTitleOnly);
        Assert.Equal("Stephen King", query.Author);
        Assert.Equal("Stephen King", Assert.Single(result).OnlineSuggestion?.AuthorDisplay);
    }

    [Fact]
    public async Task EnrichCandidates_ReusesCurrentCachedSuggestionWithoutCallingProvider()
    {
        var candidate = CreateCandidate("Dune", "Frank Herbert", 0.70m);
        var suggestion = new OnlineMetadataSuggestion(
            "Open Library",
            "/works/OL1W",
            "Dune",
            ["Frank Herbert"],
            1965,
            ["Science fiction"],
            null,
            "https://openlibrary.org/works/OL1W",
            1m,
            "Exact match",
            DateTime.UtcNow);
        var store = new StubAnalysisStore();
        store.OnlineCache[candidate.CandidateKey] = new OnlineMetadataCacheEntry(
            candidate.CandidateKey,
            OnlineMetadataIdentity.CreateInputSignature(candidate.Title, candidate.Author),
            DateTime.UtcNow,
            suggestion);
        var provider = new StubBookMetadataProvider();
        var service = new OnlineMetadataLookupService(provider, store);

        var result = await service.EnrichCandidatesAsync(
            candidate.Parts[0].MediaItem.LibrarySourceId,
            [candidate]);

        Assert.Same(suggestion, Assert.Single(result).OnlineSuggestion);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task EnrichCandidates_BatchesEligibleCandidates()
    {
        var sourceId = Guid.NewGuid();
        var candidates = Enumerable.Range(1, 9)
            .Select(index => CreateCandidate($"Unknown Book {index}", null, 0.60m, sourceId))
            .ToList();
        var provider = new StubBookMetadataProvider();
        var store = new StubAnalysisStore();
        var service = new OnlineMetadataLookupService(provider, store);

        await service.EnrichCandidatesAsync(sourceId, candidates);

        Assert.Equal(2, provider.CallCount);
        Assert.Equal(9, store.SavedOnlineEntries.Count);
    }

    [Fact]
    public async Task EnrichCandidates_SearchesEveryNeedsReviewCandidateAndDeduplicatesCleanedQueries()
    {
        var sourceId = Guid.NewGuid();
        var candidates = new[]
        {
            CreateCandidate(
                "One Door Away From Heaven (10of17)",
                "Dean Koontz",
                0.80m,
                sourceId,
                ["Filename ordering was used."]),
            CreateCandidate(
                "One Door Away From Heaven (11of17)",
                "Dean Koontz",
                0.80m,
                sourceId,
                ["Filename ordering was used."])
        };
        var provider = new StubBookMetadataProvider
        {
            Results = [CreateResult("/works/OL1W", "One Door Away from Heaven", "Dean Koontz")]
        };
        var store = new StubAnalysisStore();
        var service = new OnlineMetadataLookupService(provider, store);

        var result = await service.EnrichCandidatesAsync(sourceId, candidates);

        Assert.All(result, candidate => Assert.NotNull(candidate.OnlineSuggestion));
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(
            "One Door Away From Heaven",
            Assert.Single(Assert.Single(provider.QueryBatches)).Title);
        Assert.Equal(2, store.SavedOnlineEntries.Count);
    }

    [Fact]
    public async Task EnrichCandidates_RemovesExplicitSeriesSuffixBeforeSearchingForEachBook()
    {
        var candidate = CreateCandidate(
            "The Cosmic Code Earth Chronicles Series, Book 6",
            "Zecharia Sitchin",
            0.70m);
        var provider = new StubBookMetadataProvider
        {
            Results = [CreateResult("/works/OL1W", "The Cosmic Code", "Zecharia Sitchin")]
        };
        var service = new OnlineMetadataLookupService(provider, new StubAnalysisStore());

        var result = await service.EnrichCandidatesAsync(
            candidate.Parts[0].MediaItem.LibrarySourceId,
            [candidate]);

        Assert.Equal("The Cosmic Code", Assert.Single(Assert.Single(provider.QueryBatches)).Title);
        Assert.Equal("The Cosmic Code", Assert.Single(result).OnlineSuggestion?.Title);
    }

    [Fact]
    public async Task EnrichCandidates_SearchesOneLogicalBookWhenGenreIsUnavailable()
    {
        var sourceId = Guid.NewGuid();
        var first = CreateCandidate("Chapter One", "Roald Dahl", 1m, sourceId) with
        {
            OrganisationProposal = CreateProposal(
                "the-bfg",
                "Roald Dahl",
                "The BFG",
                isPrimary: true)
        };
        var second = CreateCandidate("Chapter Two", "Roald Dahl", 1m, sourceId) with
        {
            OrganisationProposal = CreateProposal(
                "the-bfg",
                "Roald Dahl",
                "The BFG",
                isPrimary: false)
        };
        var provider = new StubBookMetadataProvider
        {
            Results = [CreateResult("/works/OL1W", "The BFG", "Roald Dahl")]
        };
        var store = new StubAnalysisStore();
        var service = new OnlineMetadataLookupService(provider, store);

        var result = await service.EnrichCandidatesAsync(sourceId, [first, second]);

        Assert.Equal(1, provider.CallCount);
        Assert.Equal("The BFG", Assert.Single(Assert.Single(provider.QueryBatches)).Title);
        Assert.All(result, candidate => Assert.Equal("The BFG", candidate.OnlineSuggestion?.Title));
        Assert.Equal(2, store.SavedOnlineEntries.Count);
    }

    [Fact]
    public async Task EnrichCandidates_DoesNotSearchCleanPlanWithReliableGenre()
    {
        var candidate = CreateCandidate("Dune", "Frank Herbert", 1m) with
        {
            OrganisationProposal = CreateProposal(
                "dune",
                "Frank Herbert",
                "Dune",
                isPrimary: true,
                genre: "Science Fiction")
        };
        var provider = new StubBookMetadataProvider();
        var service = new OnlineMetadataLookupService(provider, new StubAnalysisStore());

        var result = await service.EnrichCandidatesAsync(
            candidate.Parts[0].MediaItem.LibrarySourceId,
            [candidate]);

        Assert.Equal(0, provider.CallCount);
        Assert.Null(Assert.Single(result).OnlineSuggestion);
    }

    [Theory]
    [InlineData("A Window into Time 08--20", "Peter F. Hamilton", "A Window into Time")]
    [InlineData("One Door Away From Heaven (11of17)", "Dean Koontz", "One Door Away From Heaven")]
    [InlineData("Miss Marple 10 At Bertram's Hotel 1965 001-End", "Agatha Christie", "At Bertram's Hotel")]
    [InlineData("Blue Gold (read by Michael Pritchard)", "Clive Cussler", "Blue Gold")]
    [InlineData("1989 - Hyperion (Foushee) 64k 17.03.25 {477mb}", "Dan Simmons", "Hyperion")]
    [InlineData("Dean Koontz - Darkfall (Unabridged)", "Dean Koontz", "Darkfall")]
    [InlineData(
        "Dean Koontz - Bliss To You - Trixie's Guide to a Happy Life - 01 of 01",
        "Dean Koontz",
        "Bliss To You - Trixie's Guide to a Happy Life")]
    public async Task EnrichCandidates_RemovesAudiobookFilenameNoiseBeforeSearching(
        string title,
        string author,
        string expected)
    {
        var candidate = CreateCandidate(title, author, 0.70m);
        var provider = new StubBookMetadataProvider();
        var service = new OnlineMetadataLookupService(provider, new StubAnalysisStore());

        await service.EnrichCandidatesAsync(candidate.Parts[0].MediaItem.LibrarySourceId, [candidate]);

        Assert.Equal(expected, Assert.Single(Assert.Single(provider.QueryBatches)).Title);
    }

    [Fact]
    public async Task OpenLibraryProvider_UsesSearchEndpointAndMapsProvenanceFields()
    {
        var handler = new StubHttpMessageHandler("""
            {
              "docs": [{
                "key": "/works/OL66554W",
                "title": "Pride and Prejudice",
                "author_name": ["Jane Austen"],
                "first_publish_year": 1813,
                "subject": ["Fiction", "Courtship"],
                "cover_i": 123
              }]
            }
            """);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://openlibrary.org/") };
        using var provider = new OpenLibraryMetadataProvider(httpClient);

        var result = await provider.SearchAsync(
            [new OnlineMetadataQuery("candidate", "Pride and Prejudice", "Jane Austen")]);

        var book = Assert.Single(result);
        Assert.Equal("Open Library", book.ProviderName);
        Assert.Equal(1813, book.FirstPublishedYear);
        Assert.Equal("https://openlibrary.org/works/OL66554W", book.SourceUrl);
        Assert.Contains("search.json", handler.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("fields=key", handler.RequestUri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenLibraryProvider_RetriesTemporaryServerFailures()
    {
        var handler = new TransientHttpMessageHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://openlibrary.org/") };
        using var provider = new OpenLibraryMetadataProvider(
            httpClient,
            TimeSpan.Zero,
            [TimeSpan.Zero, TimeSpan.Zero]);

        var result = await provider.SearchAsync(
            [new OnlineMetadataQuery("candidate", "Hyperion", "Dan Simmons")]);

        Assert.Equal(3, handler.CallCount);
        Assert.Equal("Hyperion", Assert.Single(result).Title);
    }

    private static AudiobookCandidateGroup CreateCandidate(
        string title,
        string? author,
        decimal confidence,
        Guid? sourceId = null,
        IReadOnlyList<string>? candidateWarnings = null)
    {
        var now = DateTime.UtcNow;
        var actualSourceId = sourceId ?? Guid.NewGuid();
        var relativePath = Path.Combine(author ?? "Unknown", title, $"{title}.m4b");
        var item = new MediaItem(
            actualSourceId,
            Path.Combine(Path.GetTempPath(), "Archivio.Tests", Guid.NewGuid().ToString("N"), relativePath),
            relativePath,
            100,
            now,
            now,
            now);
        var metadata = new LocalMediaMetadata(
            item.FullPath,
            new MetadataValue(title, MetadataValueSource.Inferred),
            new MetadataValue(author, author is null ? MetadataValueSource.None : MetadataValueSource.Inferred),
            new MetadataValue(null, MetadataValueSource.None),
            new MetadataValue(null, MetadataValueSource.None),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            [],
            []);
        return new AudiobookCandidateGroup(
            author is null ? title : $"{author} - {title}",
            author,
            title,
            author is null ? MetadataValueSource.None : MetadataValueSource.Inferred,
            MetadataValueSource.Inferred,
            true,
            [new AudiobookCandidatePart(item, 1, false, metadata)],
            confidence,
            candidateWarnings ?? []);
    }

    private static OnlineBookSearchResult CreateResult(string key, string title, string author) =>
        new(
            "Open Library",
            key,
            title,
            [author],
            null,
            [],
            null,
            $"https://openlibrary.org{key}");

    private static AudiobookOrganisationProposal CreateProposal(
        string planKey,
        string author,
        string title,
        bool isPrimary,
        string genre = "Uncategorised") =>
        new(
            planKey,
            author,
            title,
            null,
            genre,
            Path.Combine(author, title),
            $"001 - {title}{{original extension}}",
            AudiobookOrganisationAction.OrganiseMultipart,
            2,
            2,
            isPrimary,
            false,
            1m,
            false,
            true,
            [],
            [],
            DateTime.UtcNow);

    private sealed class StubBookMetadataProvider : IBookMetadataProvider
    {
        public string Name => "Open Library";
        public int CallCount { get; private set; }
        public IReadOnlyList<OnlineBookSearchResult> Results { get; init; } = [];
        public List<IReadOnlyList<OnlineMetadataQuery>> QueryBatches { get; } = [];

        public Task<IReadOnlyList<OnlineBookSearchResult>> SearchAsync(
            IReadOnlyList<OnlineMetadataQuery> queries,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            QueryBatches.Add(queries);
            return Task.FromResult(Results);
        }
    }

    private sealed class StubAnalysisStore : IAudiobookAnalysisStore
    {
        public Dictionary<string, OnlineMetadataCacheEntry> OnlineCache { get; } = [];
        public List<OnlineMetadataCacheEntry> SavedOnlineEntries { get; } = [];

        public Task<IReadOnlyDictionary<string, OnlineMetadataCacheEntry>> LoadOnlineMetadataCacheAsync(
            Guid librarySourceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, OnlineMetadataCacheEntry>>(OnlineCache);

        public Task SaveOnlineMetadataCacheAsync(
            Guid librarySourceId,
            IReadOnlyCollection<OnlineMetadataCacheEntry> entries,
            CancellationToken cancellationToken = default)
        {
            SavedOnlineEntries.AddRange(entries);
            foreach (var entry in entries)
            {
                OnlineCache[entry.CandidateKey] = entry;
            }

            return Task.CompletedTask;
        }

        public Task PruneOnlineMetadataCacheAsync(
            Guid librarySourceId,
            IReadOnlyCollection<string> currentCandidateKeys,
            CancellationToken cancellationToken = default)
        {
            var currentKeys = currentCandidateKeys.ToHashSet(StringComparer.Ordinal);
            foreach (var staleKey in OnlineCache.Keys.Where(key => !currentKeys.Contains(key)).ToList())
            {
                OnlineCache.Remove(staleKey);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<Guid, AudiobookMetadataCacheEntry>> LoadMetadataCacheAsync(
            Guid librarySourceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, AudiobookMetadataCacheEntry>>(
                new Dictionary<Guid, AudiobookMetadataCacheEntry>());

        public Task<SavedAudiobookAnalysis?> LoadCompletedAnalysisAsync(
            Guid librarySourceId,
            IReadOnlyList<MediaItem> currentMediaItems,
            CancellationToken cancellationToken = default) => Task.FromResult<SavedAudiobookAnalysis?>(null);

        public Task BeginAnalysisAsync(Guid librarySourceId, int totalCount, int reusedCount, int warningCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveCheckpointAsync(Guid librarySourceId, IReadOnlyCollection<AudiobookMetadataCacheEntry> metadataEntries, int processedCount, int totalCount, int warningCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CompleteAnalysisAsync(Guid librarySourceId, IReadOnlyList<AudiobookCandidateGroup> candidates, int processedCount, int totalCount, int warningCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MarkAnalysisInterruptedAsync(Guid librarySourceId, bool wasCancelled, string? errorMessage, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubHttpMessageHandler(string responseJson) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class TransientHttpMessageHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (CallCount < 3)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"docs":[{"key":"/works/OL1W","title":"Hyperion","author_name":["Dan Simmons"]}]}
                    """,
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
