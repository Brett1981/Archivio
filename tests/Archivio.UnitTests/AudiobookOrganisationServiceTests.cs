using Archivio.Application.Abstractions;
using Archivio.Application.Services;
using Archivio.Domain;

namespace Archivio.UnitTests;

public sealed class AudiobookOrganisationServiceTests
{
    [Fact]
    public async Task PrepareProposals_GroupsCandidatesWithTheSameOnlineIdentity()
    {
        var sourceId = Guid.NewGuid();
        var suggestion = CreateSuggestion(
            "/works/OL265415W",
            "At Bertram's Hotel",
            "Agatha Christie",
            1965,
            ["Mystery", "Detective fiction"]);
        var candidates = new[]
        {
            CreateCandidate(sourceId, "Miss Marple 10 At Bertram's Hotel 001-End", "Agatha Christie") with
            {
                OnlineSuggestion = suggestion
            },
            CreateCandidate(sourceId, "At Bertram's Hotel Disc 2", "Agatha Christie") with
            {
                OnlineSuggestion = suggestion
            }
        };
        var store = new StubOrganisationStore();
        var service = new AudiobookOrganisationService(store);

        var result = await service.PrepareProposalsAsync(sourceId, candidates);

        var primary = Assert.Single(result, candidate => candidate.IsPrimaryOrganisationPlan);
        var proposal = Assert.IsType<AudiobookOrganisationProposal>(primary.OrganisationProposal);
        Assert.Equal(AudiobookOrganisationAction.ConsolidateCandidates, proposal.RecommendedAction);
        Assert.Equal(2, proposal.RelatedCandidateCount);
        Assert.Equal(2, proposal.SourceFileCount);
        Assert.Equal("Mystery & Thriller", proposal.GenreCategory);
        Assert.Equal(
            Path.Combine("Mystery & Thriller", "Agatha Christie", "At Bertram's Hotel (1965)"),
            proposal.SuggestedRelativeFolder);
        Assert.True(proposal.ReadyForAutomaticHandling);
        Assert.All(result, candidate => Assert.Equal(proposal.PlanKey, candidate.OrganisationProposal?.PlanKey));
        Assert.Equal(2, store.SavedEntries.Count);
    }

    [Fact]
    public async Task PrepareProposals_UsesASafeMultipartFolderPlanWithoutChangingFiles()
    {
        var sourceId = Guid.NewGuid();
        var candidate = CreateCandidate(
            sourceId,
            "The Long Book",
            "An Author",
            partCount: 3,
            confidence: 1m);
        var originalPaths = candidate.Parts.Select(part => part.MediaItem.RelativePath).ToList();
        var service = new AudiobookOrganisationService(new StubOrganisationStore());

        var result = await service.PrepareProposalsAsync(sourceId, [candidate]);

        var proposal = Assert.IsType<AudiobookOrganisationProposal>(Assert.Single(result).OrganisationProposal);
        Assert.Equal(AudiobookOrganisationAction.OrganiseMultipart, proposal.RecommendedAction);
        Assert.Equal(Path.Combine("An Author", "The Long Book"), proposal.SuggestedRelativeFolder);
        Assert.Equal("001 - The Long Book{original extension}", proposal.SuggestedFileNamePattern);
        Assert.True(proposal.FutureCombineCandidate);
        Assert.Equal(originalPaths, candidate.Parts.Select(part => part.MediaItem.RelativePath));
    }

    [Fact]
    public async Task PrepareProposals_SanitizesWindowsReservedNamesAndInvalidCharacters()
    {
        var sourceId = Guid.NewGuid();
        var candidate = CreateCandidate(sourceId, "Unsafe", "Author") with
        {
            OnlineSuggestion = CreateSuggestion(
                "/works/OL1W",
                "CON: A/B?",
                "AUX",
                null,
                ["Science fiction"])
        };
        var service = new AudiobookOrganisationService(new StubOrganisationStore());

        var result = await service.PrepareProposalsAsync(sourceId, [candidate]);

        var proposal = Assert.IsType<AudiobookOrganisationProposal>(Assert.Single(result).OrganisationProposal);
        Assert.Contains("_AUX", proposal.SuggestedRelativeFolder, StringComparison.Ordinal);
        Assert.DoesNotContain(':', proposal.SuggestedRelativeFolder);
        Assert.DoesNotContain('?', proposal.SuggestedRelativeFolder);
        Assert.DoesNotContain("A/B", proposal.SuggestedRelativeFolder, StringComparison.Ordinal);
        Assert.DoesNotContain(':', proposal.SuggestedFileNamePattern);
        Assert.DoesNotContain('?', proposal.SuggestedFileNamePattern);
    }

    [Fact]
    public async Task PrepareProposals_ReusesPersistedProposalWhenInputsAreUnchanged()
    {
        var sourceId = Guid.NewGuid();
        var candidate = CreateCandidate(sourceId, "Dune", "Frank Herbert") with
        {
            OnlineSuggestion = CreateSuggestion(
                "/works/OL893415W",
                "Dune",
                "Frank Herbert",
                1965,
                ["Science fiction"])
        };
        var store = new StubOrganisationStore();
        var service = new AudiobookOrganisationService(store);

        var first = await service.PrepareProposalsAsync(sourceId, [candidate]);
        store.SavedEntries.Clear();
        var second = await service.PrepareProposalsAsync(sourceId, [candidate]);

        Assert.Same(
            Assert.Single(first).OrganisationProposal,
            Assert.Single(second).OrganisationProposal);
        Assert.Empty(store.SavedEntries);
    }

    [Fact]
    public async Task PrepareProposals_MarksGenericTrackTitlesForReview()
    {
        var sourceId = Guid.NewGuid();
        var candidate = CreateCandidate(sourceId, "AudioTrack 01", "Dan Simmons", confidence: 0.95m);
        var service = new AudiobookOrganisationService(new StubOrganisationStore());

        var result = await service.PrepareProposalsAsync(sourceId, [candidate]);

        var proposal = Assert.IsType<AudiobookOrganisationProposal>(Assert.Single(result).OrganisationProposal);
        Assert.False(proposal.ReadyForAutomaticHandling);
        Assert.Contains(proposal.Warnings, warning => warning.Contains("track or disc", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrepareProposals_DoesNotAutomaticallyHandleFilenameOnlyIdentity()
    {
        var sourceId = Guid.NewGuid();
        var candidate = CreateCandidate(
            sourceId,
            "A Plausible Book",
            "A Plausible Author",
            confidence: 0.95m,
            source: MetadataValueSource.FileName);
        var service = new AudiobookOrganisationService(new StubOrganisationStore());

        var result = await service.PrepareProposalsAsync(sourceId, [candidate]);

        var proposal = Assert.IsType<AudiobookOrganisationProposal>(Assert.Single(result).OrganisationProposal);
        Assert.False(proposal.ReadyForAutomaticHandling);
        Assert.Contains(
            proposal.Warnings,
            warning => warning.Contains("filename-derived", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrepareProposals_UsesMatchingFolderIdentityForNumberedBookLabels()
    {
        var sourceId = Guid.NewGuid();
        var bookFolder = Path.Combine("Roald Dahl", "Roald Dahl - The BFG");
        var candidates = new[]
        {
            CreateCandidate(
                sourceId,
                "A Trogglehumper For The Fleshbumpeater – Dreams",
                "12 The BFG",
                confidence: 1m,
                relativeDirectory: bookFolder),
            CreateCandidate(
                sourceId,
                "The Bloodbottler",
                "13 The BFG",
                confidence: 1m,
                relativeDirectory: bookFolder)
        };
        var service = new AudiobookOrganisationService(new StubOrganisationStore());

        var result = await service.PrepareProposalsAsync(sourceId, candidates);

        var primary = Assert.Single(result, candidate => candidate.IsPrimaryOrganisationPlan);
        var proposal = Assert.IsType<AudiobookOrganisationProposal>(primary.OrganisationProposal);
        Assert.Equal("Roald Dahl", proposal.CanonicalAuthor);
        Assert.Equal("The BFG", proposal.CanonicalTitle);
        Assert.Equal(Path.Combine("Roald Dahl", "The BFG"), proposal.SuggestedRelativeFolder);
        Assert.Equal(AudiobookOrganisationAction.ConsolidateCandidates, proposal.RecommendedAction);
        Assert.Equal(2, proposal.RelatedCandidateCount);
        Assert.False(proposal.ReadyForAutomaticHandling);
        Assert.Contains(
            proposal.Warnings,
            warning => warning.Contains("numbered book label", StringComparison.Ordinal));
        Assert.All(result, candidate =>
            Assert.Equal(proposal.PlanKey, candidate.OrganisationProposal?.PlanKey));
    }

    [Fact]
    public async Task PrepareProposals_DoesNotMarkAnOnlinePlanReadyWhileAnalysisStillNeedsReview()
    {
        var sourceId = Guid.NewGuid();
        var candidate = CreateCandidate(sourceId, "Dune", "Frank Herbert") with
        {
            OnlineSuggestion = CreateSuggestion(
                "/works/OL893415W",
                "Dune",
                "Frank Herbert",
                1965,
                ["Science fiction"]),
            Warnings = ["Conflicting embedded title metadata."]
        };
        var service = new AudiobookOrganisationService(new StubOrganisationStore());

        var result = await service.PrepareProposalsAsync(sourceId, [candidate]);

        var prepared = Assert.Single(result);
        var proposal = Assert.IsType<AudiobookOrganisationProposal>(prepared.OrganisationProposal);
        Assert.True(prepared.NeedsReview);
        Assert.False(prepared.IsReviewClearedOrganisationPlan);
        Assert.False(proposal.ReadyForAutomaticHandling);
    }

    private static AudiobookCandidateGroup CreateCandidate(
        Guid sourceId,
        string title,
        string author,
        int partCount = 1,
        decimal confidence = 0.95m,
        MetadataValueSource source = MetadataValueSource.EmbeddedTag,
        string? relativeDirectory = null)
    {
        var now = DateTime.UtcNow;
        var parts = Enumerable.Range(1, partCount)
            .Select(index =>
            {
                var relativePath = Path.Combine(
                    relativeDirectory ?? Path.Combine(author, title),
                    $"Part {index:000}.mp3");
                var item = new MediaItem(
                    sourceId,
                    Path.Combine(Path.GetTempPath(), "Archivio.Tests", relativePath),
                    relativePath,
                    100,
                    now,
                    now,
                    now);
                var metadata = new LocalMediaMetadata(
                    item.FullPath,
                    new MetadataValue(title, source),
                    new MetadataValue(author, source),
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
                return new AudiobookCandidatePart(item, index, true, metadata);
            })
            .ToList();
        return new AudiobookCandidateGroup(
            $"{author} - {title}",
            author,
            title,
            source,
            source,
            true,
            parts,
            confidence,
            []);
    }

    private static OnlineMetadataSuggestion CreateSuggestion(
        string providerItemId,
        string title,
        string author,
        int? year,
        IReadOnlyList<string> subjects) =>
        new(
            "Open Library",
            providerItemId,
            title,
            [author],
            year,
            subjects,
            null,
            $"https://openlibrary.org{providerItemId}",
            1m,
            "Exact title and author match",
            DateTime.UtcNow);

    private sealed class StubOrganisationStore : IAudiobookOrganisationStore
    {
        public Dictionary<string, AudiobookOrganisationCacheEntry> Cache { get; } = [];
        public List<AudiobookOrganisationCacheEntry> SavedEntries { get; } = [];

        public Task<IReadOnlyDictionary<string, AudiobookOrganisationCacheEntry>> LoadAsync(
            Guid librarySourceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, AudiobookOrganisationCacheEntry>>(Cache);

        public Task SaveAsync(
            Guid librarySourceId,
            IReadOnlyCollection<AudiobookOrganisationCacheEntry> entries,
            CancellationToken cancellationToken = default)
        {
            SavedEntries.AddRange(entries);
            foreach (var entry in entries)
            {
                Cache[entry.CandidateKey] = entry;
            }

            return Task.CompletedTask;
        }
    }
}
