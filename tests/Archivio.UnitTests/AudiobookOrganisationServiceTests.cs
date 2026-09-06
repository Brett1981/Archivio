using Archivio.Application.Abstractions;
using Archivio.Application.Services;
using Archivio.Domain;

namespace Archivio.UnitTests;

public sealed class AudiobookOrganisationServiceTests
{
    [Fact]
    public async Task GenreOverride_PersistsRevalidatesAndCanBeRestoredToAutomatic()
    {
        var sourceId = Guid.NewGuid();
        var candidate = CreateCandidate(
            sourceId,
            "A Reliable Book",
            "A Reliable Author",
            confidence: 1m);
        var overrideStore = new StubReviewOverrideStore();
        var service = new AudiobookOrganisationService(
            new StubOrganisationStore(),
            overrideStore);

        var initial = await service.PrepareProposalsAsync(sourceId, [candidate]);
        var initialProposal = Assert.IsType<AudiobookOrganisationProposal>(
            Assert.Single(initial).OrganisationProposal);

        Assert.Equal("Uncategorised", initialProposal.GenreCategory);
        Assert.False(initialProposal.ReadyForAutomaticHandling);

        var corrected = await service.SetGenreOverrideAsync(
            sourceId,
            initial,
            initialProposal.PlanKey,
            "Science Fiction");
        var correctedProposal = Assert.IsType<AudiobookOrganisationProposal>(
            Assert.Single(corrected).OrganisationProposal);

        Assert.Equal("Science Fiction", correctedProposal.GenreCategory);
        Assert.True(correctedProposal.UsesManualGenre);
        Assert.True(correctedProposal.ReadyForAutomaticHandling);
        Assert.StartsWith("Science Fiction", correctedProposal.SuggestedRelativeFolder, StringComparison.Ordinal);
        Assert.Contains(correctedProposal.Reasons, reason => reason.Contains("confirmed by you", StringComparison.Ordinal));

        var reloaded = await service.PrepareProposalsAsync(sourceId, [candidate]);
        Assert.True(Assert.Single(reloaded).OrganisationProposal?.UsesManualGenre);

        var restored = await service.SetGenreOverrideAsync(
            sourceId,
            reloaded,
            correctedProposal.PlanKey,
            null);
        var restoredProposal = Assert.IsType<AudiobookOrganisationProposal>(
            Assert.Single(restored).OrganisationProposal);

        Assert.Equal("Uncategorised", restoredProposal.GenreCategory);
        Assert.False(restoredProposal.UsesManualGenre);
        Assert.False(restoredProposal.ReadyForAutomaticHandling);
    }

    [Fact]
    public async Task IdentityOverride_ReplacesUnknownIdentityPersistsAndCanBeRestored()
    {
        var sourceId = Guid.NewGuid();
        var candidate = CreateCandidate(
            sourceId,
            "Zecharia Sitchin",
            string.Empty,
            confidence: 0.60m,
            source: MetadataValueSource.Inferred,
            genre: "Science Fiction");
        var overrideStore = new StubReviewOverrideStore();
        var service = new AudiobookOrganisationService(
            new StubOrganisationStore(),
            overrideStore);

        var initial = await service.PrepareProposalsAsync(sourceId, [candidate]);
        var initialProposal = Assert.IsType<AudiobookOrganisationProposal>(
            Assert.Single(initial).OrganisationProposal);
        Assert.Equal("Unknown Author", initialProposal.CanonicalAuthor);
        Assert.False(initialProposal.ReadyForAutomaticHandling);

        var corrected = await service.SetIdentityOverrideAsync(
            sourceId,
            initial,
            initialProposal.PlanKey,
            "Zecharia Sitchin",
            "The Cosmic Code");
        var correctedProposal = Assert.IsType<AudiobookOrganisationProposal>(
            Assert.Single(corrected).OrganisationProposal);

        Assert.Equal("Zecharia Sitchin", correctedProposal.CanonicalAuthor);
        Assert.Equal("The Cosmic Code", correctedProposal.CanonicalTitle);
        Assert.True(correctedProposal.UsesManualAuthor);
        Assert.True(correctedProposal.UsesManualTitle);
        Assert.True(correctedProposal.ReadyForAutomaticHandling);
        Assert.Equal(0.95m, correctedProposal.Confidence);
        Assert.Contains(
            Path.Combine("Science Fiction", "Zecharia Sitchin", "The Cosmic Code"),
            correctedProposal.SuggestedRelativeFolder,
            StringComparison.Ordinal);

        var reloaded = await service.PrepareProposalsAsync(sourceId, [candidate]);
        Assert.True(Assert.Single(reloaded).OrganisationProposal?.UsesManualAuthor);

        var restored = await service.SetIdentityOverrideAsync(
            sourceId,
            reloaded,
            correctedProposal.PlanKey,
            null,
            null);
        var restoredProposal = Assert.IsType<AudiobookOrganisationProposal>(
            Assert.Single(restored).OrganisationProposal);

        Assert.Equal("Unknown Author", restoredProposal.CanonicalAuthor);
        Assert.Equal("Zecharia Sitchin", restoredProposal.CanonicalTitle);
        Assert.False(restoredProposal.UsesManualAuthor);
        Assert.False(restoredProposal.ReadyForAutomaticHandling);
    }

    [Fact]
    public async Task IdentityOverride_RemainsAuthoritativeWhenOnlineEnrichmentArrivesLater()
    {
        var sourceId = Guid.NewGuid();
        var candidate = CreateCandidate(
            sourceId,
            "Zecharia Sitchin",
            string.Empty,
            confidence: 0.60m,
            source: MetadataValueSource.Inferred,
            genre: "Science Fiction");
        var service = new AudiobookOrganisationService(
            new StubOrganisationStore(),
            new StubReviewOverrideStore());
        var initial = await service.PrepareProposalsAsync(sourceId, [candidate]);
        var initialProposal = Assert.IsType<AudiobookOrganisationProposal>(
            Assert.Single(initial).OrganisationProposal);
        var corrected = await service.SetIdentityOverrideAsync(
            sourceId,
            initial,
            initialProposal.PlanKey,
            "Zecharia Sitchin",
            "The Cosmic Code");
        var enriched = Assert.Single(corrected) with
        {
            OnlineSuggestion = CreateSuggestion(
                "/works/OL1W",
                "The Cosmic Code",
                "Zecharia Sitchin",
                1998,
                ["Science fiction"])
        };

        var regenerated = await service.PrepareProposalsAsync(sourceId, [enriched]);
        var regeneratedProposal = Assert.IsType<AudiobookOrganisationProposal>(
            Assert.Single(regenerated).OrganisationProposal);

        Assert.Equal(initialProposal.PlanKey, regeneratedProposal.PlanKey);
        Assert.True(regeneratedProposal.UsesManualAuthor);
        Assert.True(regeneratedProposal.UsesManualTitle);
        Assert.Equal("Zecharia Sitchin", regeneratedProposal.CanonicalAuthor);
        Assert.Equal("The Cosmic Code", regeneratedProposal.CanonicalTitle);
    }

    [Fact]
    public async Task SeparateBooksOverride_CreatesIndependentSeriesPlansForEachSourceFile()
    {
        var sourceId = Guid.NewGuid();
        var titles = new[]
        {
            "The 12th Planet Earth Chronicles Series, Book 1",
            "The Stairway to Heaven Earth Chronicles Series, Book 2",
            "The Cosmic Code Earth Chronicles Series, Book 6"
        };
        var collection = CreateCompleteBookCollection(sourceId, titles);
        var service = new AudiobookOrganisationService(
            new StubOrganisationStore(),
            new StubReviewOverrideStore());
        var initial = await service.PrepareProposalsAsync(sourceId, [collection]);
        var initialProposal = Assert.IsType<AudiobookOrganisationProposal>(
            Assert.Single(initial).OrganisationProposal);

        var separated = await service.SetCollectionOverrideAsync(
            sourceId,
            initial,
            initialProposal.PlanKey,
            AudiobookCollectionHandling.SeparateBooks,
            "Zecharia Sitchin",
            "Earth Chronicles");

        Assert.Equal(3, separated.Count);
        Assert.All(separated, candidate => Assert.Single(candidate.Parts));
        Assert.All(separated, candidate => Assert.True(candidate.IsPrimaryOrganisationPlan));
        Assert.Equal(3, separated.Select(candidate => candidate.OrganisationProposal!.PlanKey).Distinct().Count());
        Assert.Contains(separated, candidate => candidate.OrganisationProposal is
        {
            CanonicalTitle: "The 12th Planet",
            SeriesName: "Earth Chronicles",
            SeriesPosition: 1,
            IsSeparateBookPlan: true
        });
        Assert.Contains(separated, candidate => candidate.OrganisationProposal is
        {
            CanonicalTitle: "The Cosmic Code",
            SeriesPosition: 6
        });
        Assert.All(separated, candidate => Assert.Contains(
            Path.Combine("Science Fiction", "Zecharia Sitchin", "Earth Chronicles"),
            candidate.OrganisationProposal!.SuggestedRelativeFolder,
            StringComparison.Ordinal));
    }

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

    [Theory]
    [InlineData("Sci-Fi", "Science Fiction")]
    [InlineData("Mystery", "Mystery & Thriller")]
    [InlineData("Audio Book: Children & Young Adult", "Children & Young Adult")]
    public async Task PrepareProposals_UsesExplicitEmbeddedGenreWhenOnlineMetadataIsUnavailable(
        string embeddedGenre,
        string expectedCategory)
    {
        var sourceId = Guid.NewGuid();
        var candidate = CreateCandidate(
            sourceId,
            "A Reliable Book",
            "A Reliable Author",
            confidence: 1m,
            genre: embeddedGenre);
        var service = new AudiobookOrganisationService(new StubOrganisationStore());

        var result = await service.PrepareProposalsAsync(sourceId, [candidate]);

        var proposal = Assert.IsType<AudiobookOrganisationProposal>(Assert.Single(result).OrganisationProposal);
        Assert.Equal(expectedCategory, proposal.GenreCategory);
        Assert.StartsWith(expectedCategory, proposal.SuggestedRelativeFolder, StringComparison.Ordinal);
        Assert.True(proposal.ReadyForAutomaticHandling);
        Assert.Contains(proposal.Reasons, reason => reason.Contains("embedded tag", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrepareProposals_DoesNotTreatGenericAudiobookTagAsAGenre()
    {
        var sourceId = Guid.NewGuid();
        var candidate = CreateCandidate(
            sourceId,
            "A Reliable Book",
            "A Reliable Author",
            confidence: 1m,
            genre: "Audio Book");
        var service = new AudiobookOrganisationService(new StubOrganisationStore());

        var result = await service.PrepareProposalsAsync(sourceId, [candidate]);

        var proposal = Assert.IsType<AudiobookOrganisationProposal>(Assert.Single(result).OrganisationProposal);
        Assert.Equal("Uncategorised", proposal.GenreCategory);
        Assert.False(proposal.ReadyForAutomaticHandling);
    }

    [Fact]
    public async Task PrepareProposals_PrunesCacheWhenTheCurrentAnalysisIsEmpty()
    {
        var sourceId = Guid.NewGuid();
        var staleCandidate = CreateCandidate(sourceId, "Old Book", "Old Author");
        var store = new StubOrganisationStore();
        var service = new AudiobookOrganisationService(store);
        await service.PrepareProposalsAsync(sourceId, [staleCandidate]);
        Assert.NotEmpty(store.Cache);

        var result = await service.PrepareProposalsAsync(
            sourceId,
            Array.Empty<AudiobookCandidateGroup>());

        Assert.Empty(result);
        Assert.Empty(store.Cache);
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
        Assert.Contains(proposal.Warnings, warning => warning.Contains("track, disc", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrepareProposals_ConsolidatesNumberedPartsUsingEmbeddedAlbumIdentity()
    {
        var sourceId = Guid.NewGuid();
        var candidates = new[]
        {
            CreateCandidate(
                sourceId,
                "The Key to Midnight - 01 of 02",
                "Dean Koontz",
                confidence: 1m,
                genre: "Mystery",
                album: "The Key to Midnight",
                trackNumber: 1),
            CreateCandidate(
                sourceId,
                "The Key to Midnight - 02 of 02",
                "Dean Koontz",
                confidence: 1m,
                genre: "Mystery",
                album: "The Key to Midnight",
                trackNumber: 2)
        };
        var service = new AudiobookOrganisationService(new StubOrganisationStore());

        var result = await service.PrepareProposalsAsync(sourceId, candidates);

        var primary = Assert.Single(result, candidate => candidate.IsPrimaryOrganisationPlan);
        var proposal = Assert.IsType<AudiobookOrganisationProposal>(primary.OrganisationProposal);
        Assert.Equal("The Key to Midnight", proposal.CanonicalTitle);
        Assert.Equal(2, proposal.RelatedCandidateCount);
        Assert.Equal(AudiobookOrganisationAction.ConsolidateCandidates, proposal.RecommendedAction);
        Assert.True(proposal.ReadyForAutomaticHandling);
        Assert.Contains(proposal.Reasons, reason => reason.Contains("album metadata", StringComparison.Ordinal));
        Assert.All(result, candidate =>
            Assert.Equal(proposal.PlanKey, candidate.OrganisationProposal?.PlanKey));
    }

    [Fact]
    public async Task PrepareProposals_UsesAuthorBookFolderWhenAlbumIsOnlyADiscNumber()
    {
        var sourceId = Guid.NewGuid();
        var bookFolder = Path.Combine("Roald Dahl", "Roald Dahl - Esio Trot");
        var candidates = new[]
        {
            CreateCandidate(
                sourceId,
                "Chapter One",
                "Roald Dahl",
                confidence: 1m,
                relativeDirectory: bookFolder,
                genre: "Children",
                album: "6",
                trackNumber: 1),
            CreateCandidate(
                sourceId,
                "Chapter Two",
                "Roald Dahl",
                confidence: 1m,
                relativeDirectory: bookFolder,
                genre: "Children",
                album: "6",
                trackNumber: 2)
        };
        var service = new AudiobookOrganisationService(new StubOrganisationStore());

        var result = await service.PrepareProposalsAsync(sourceId, candidates);

        var primary = Assert.Single(result, candidate => candidate.IsPrimaryOrganisationPlan);
        var proposal = Assert.IsType<AudiobookOrganisationProposal>(primary.OrganisationProposal);
        Assert.Equal("Roald Dahl", proposal.CanonicalAuthor);
        Assert.Equal("Esio Trot", proposal.CanonicalTitle);
        Assert.Equal(
            Path.Combine("Children & Young Adult", "Roald Dahl", "Esio Trot"),
            proposal.SuggestedRelativeFolder);
        Assert.True(proposal.ReadyForAutomaticHandling);
        Assert.Contains(proposal.Reasons, reason => reason.Contains("author/book folder", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrepareProposals_BlocksAnIsolatedChapterEvenWhenAlbumIdentifiesTheBook()
    {
        var sourceId = Guid.NewGuid();
        var candidate = CreateCandidate(
            sourceId,
            "Chapter Eleven",
            "Roald Dahl",
            confidence: 1m,
            genre: "Children",
            album: "Charlie and the Chocolate Factory",
            trackNumber: 11);
        var service = new AudiobookOrganisationService(new StubOrganisationStore());

        var result = await service.PrepareProposalsAsync(sourceId, [candidate]);

        var proposal = Assert.IsType<AudiobookOrganisationProposal>(Assert.Single(result).OrganisationProposal);
        Assert.Equal("Charlie and the Chocolate Factory", proposal.CanonicalTitle);
        Assert.False(proposal.ReadyForAutomaticHandling);
        Assert.Contains(
            proposal.Warnings,
            warning => warning.Contains("complete, unique track sequence", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IdentityOverride_DoesNotClearAnIncompleteTrackSequence()
    {
        var sourceId = Guid.NewGuid();
        var candidate = CreateCandidate(
            sourceId,
            "Chapter Eleven",
            "Roald Dahl",
            confidence: 1m,
            genre: "Children",
            album: "Charlie and the Chocolate Factory",
            trackNumber: 11);
        var service = new AudiobookOrganisationService(
            new StubOrganisationStore(),
            new StubReviewOverrideStore());
        var initial = await service.PrepareProposalsAsync(sourceId, [candidate]);
        var initialProposal = Assert.IsType<AudiobookOrganisationProposal>(
            Assert.Single(initial).OrganisationProposal);

        var corrected = await service.SetIdentityOverrideAsync(
            sourceId,
            initial,
            initialProposal.PlanKey,
            "Roald Dahl",
            "Charlie and the Chocolate Factory");
        var correctedProposal = Assert.IsType<AudiobookOrganisationProposal>(
            Assert.Single(corrected).OrganisationProposal);

        Assert.False(correctedProposal.ReadyForAutomaticHandling);
        Assert.Contains(
            correctedProposal.Warnings,
            warning => warning.Contains("complete, unique track sequence", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrepareProposals_BlocksAlbumConsolidationWithANonContiguousTrackSequence()
    {
        var sourceId = Guid.NewGuid();
        var candidates = new[]
        {
            CreateCandidate(
                sourceId,
                "The Cow",
                "Roald Dahl",
                confidence: 1m,
                genre: "Children",
                album: "Revolting Rhymes And Dirty Beasts",
                trackNumber: 7),
            CreateCandidate(
                sourceId,
                "The Pig",
                "Roald Dahl",
                confidence: 1m,
                genre: "Children",
                album: "Revolting Rhymes And Dirty Beasts",
                trackNumber: 8)
        };
        var service = new AudiobookOrganisationService(new StubOrganisationStore());

        var result = await service.PrepareProposalsAsync(sourceId, candidates);

        var primary = Assert.Single(result, candidate => candidate.IsPrimaryOrganisationPlan);
        var proposal = Assert.IsType<AudiobookOrganisationProposal>(primary.OrganisationProposal);
        Assert.False(proposal.ReadyForAutomaticHandling);
        Assert.Contains(
            proposal.Warnings,
            warning => warning.Contains("complete, unique track sequence", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrepareProposals_AllowsAnExplicitOneOfOneAlbumIdentityWithoutATrackTag()
    {
        var sourceId = Guid.NewGuid();
        var candidate = CreateCandidate(
            sourceId,
            "Dean Koontz - Bliss To You - Trixie's Guide to a Happy Life - 01 of 01",
            "Dean Koontz",
            confidence: 1m,
            genre: "Fiction",
            album: "Bliss To You");
        var service = new AudiobookOrganisationService(new StubOrganisationStore());

        var result = await service.PrepareProposalsAsync(sourceId, [candidate]);

        var proposal = Assert.IsType<AudiobookOrganisationProposal>(Assert.Single(result).OrganisationProposal);
        Assert.Equal("Bliss To You - Trixie's Guide to a Happy Life", proposal.CanonicalTitle);
        Assert.True(proposal.ReadyForAutomaticHandling);
    }

    [Theory]
    [InlineData("Chapter Eleven")]
    [InlineData("A Book - 01 of 10")]
    [InlineData("Part 3")]
    public async Task PrepareProposals_BlocksUnresolvedSegmentTitles(string title)
    {
        var sourceId = Guid.NewGuid();
        var candidate = CreateCandidate(
            sourceId,
            title,
            "An Author",
            confidence: 1m,
            genre: "Fiction");
        var service = new AudiobookOrganisationService(new StubOrganisationStore());

        var result = await service.PrepareProposalsAsync(sourceId, [candidate]);

        var proposal = Assert.IsType<AudiobookOrganisationProposal>(Assert.Single(result).OrganisationProposal);
        Assert.False(proposal.ReadyForAutomaticHandling);
        Assert.Contains(
            proposal.Warnings,
            warning => warning.Contains("chapter, track, disc, or part", StringComparison.Ordinal));
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
    public async Task PrepareProposals_RecognisesItsCanonicalMultipartDestinationAfterRescan()
    {
        var sourceId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var relativeDirectory = Path.Combine(
            "Children & Young Adult",
            "Roald Dahl",
            "The BFG");
        var parts = Enumerable.Range(1, 35)
            .Select(index =>
            {
                var relativePath = Path.Combine(relativeDirectory, $"{index:000} - The BFG.mp3");
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
                    new MetadataValue("Chapter title", MetadataValueSource.EmbeddedTag),
                    new MetadataValue("12 The BFG", MetadataValueSource.EmbeddedTag),
                    new MetadataValue(null, MetadataValueSource.None),
                    new MetadataValue(null, MetadataValueSource.None),
                    null,
                    (uint)((index - 1) % 12 + 1),
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
        var candidate = new AudiobookCandidateGroup(
            "12 The BFG - The BFG",
            "12 The BFG",
            "The BFG",
            MetadataValueSource.EmbeddedTag,
            MetadataValueSource.FileName,
            true,
            parts,
            1m,
            ["Embedded track numbers do not form a complete sequence."]);
        var service = new AudiobookOrganisationService(new StubOrganisationStore());

        var result = await service.PrepareProposalsAsync(sourceId, [candidate]);

        var proposal = Assert.IsType<AudiobookOrganisationProposal>(Assert.Single(result).OrganisationProposal);
        Assert.Equal("Roald Dahl", proposal.CanonicalAuthor);
        Assert.Equal("The BFG", proposal.CanonicalTitle);
        Assert.Equal("Children & Young Adult", proposal.GenreCategory);
        Assert.Equal(relativeDirectory, proposal.SuggestedRelativeFolder);
        Assert.Equal(AudiobookOrganisationAction.Keep, proposal.RecommendedAction);
        Assert.True(proposal.ReadyForAutomaticHandling);
    }

    [Fact]
    public async Task PrepareProposals_RecognisesItsCanonicalSingleFileDestinationBeforeOnlineMetadata()
    {
        var sourceId = Guid.NewGuid();
        var canonicalDirectory = Path.Combine(
            "Mystery & Thriller",
            "Stephen King",
            "Rage (1977)");
        var candidate = CreateCandidate(
            sourceId,
            "Rage",
            "1977",
            confidence: 1m,
            relativeDirectory: canonicalDirectory) with
        {
            Parts = CreateCandidate(
                    sourceId,
                    "Rage",
                    "1977",
                    confidence: 1m,
                    relativeDirectory: canonicalDirectory)
                .Parts
                .Select(part =>
                {
                    var relativePath = Path.Combine(canonicalDirectory, "Stephen King - Rage.mp3");
                    var item = new MediaItem(
                        sourceId,
                        Path.Combine(Path.GetTempPath(), "Archivio.Tests", relativePath),
                        relativePath,
                        part.MediaItem.SizeBytes,
                        part.MediaItem.CreatedAtUtc,
                        part.MediaItem.ModifiedAtUtc,
                        part.MediaItem.LastScannedAtUtc);
                    return part with { MediaItem = item };
                })
                .ToList(),
            OnlineSuggestion = CreateSuggestion(
                "/works/wrong",
                "A Different Book",
                "A Different Author",
                1999,
                ["Fantasy"])
        };
        var service = new AudiobookOrganisationService(new StubOrganisationStore());

        var result = await service.PrepareProposalsAsync(sourceId, [candidate]);

        var proposal = Assert.IsType<AudiobookOrganisationProposal>(Assert.Single(result).OrganisationProposal);
        Assert.Equal("Stephen King", proposal.CanonicalAuthor);
        Assert.Equal("Rage", proposal.CanonicalTitle);
        Assert.Equal(1977, proposal.FirstPublishedYear);
        Assert.Equal("Mystery & Thriller", proposal.GenreCategory);
        Assert.Equal(canonicalDirectory, proposal.SuggestedRelativeFolder);
        Assert.Equal(AudiobookOrganisationAction.Keep, proposal.RecommendedAction);
        Assert.False(proposal.UsesOnlineMetadata);
        Assert.True(proposal.ReadyForAutomaticHandling);
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
        string? relativeDirectory = null,
        string? genre = null,
        string? album = null,
        uint? trackNumber = null)
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
                    new MetadataValue(
                        album,
                        album is null ? MetadataValueSource.None : MetadataValueSource.EmbeddedTag),
                    new MetadataValue(
                        genre,
                        genre is null ? MetadataValueSource.None : MetadataValueSource.EmbeddedTag),
                    null,
                    trackNumber is null ? null : trackNumber + (uint)(index - 1),
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

    private static AudiobookCandidateGroup CreateCompleteBookCollection(
        Guid sourceId,
        IReadOnlyList<string> titles)
    {
        var now = DateTime.UtcNow;
        var parts = titles.Select((title, index) =>
        {
            var relativePath = Path.Combine("Zecharia Sitchin", $"{title}.m4b");
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
                new MetadataValue(title, MetadataValueSource.EmbeddedTag),
                new MetadataValue("Zecharia Sitchin", MetadataValueSource.EmbeddedTag),
                new MetadataValue(null, MetadataValueSource.None),
                new MetadataValue("Science Fiction", MetadataValueSource.EmbeddedTag),
                null,
                null,
                TimeSpan.FromHours(8),
                64,
                22050,
                2,
                "MPEG-4 Audio",
                true,
                [],
                []);
            return new AudiobookCandidatePart(item, index + 1, false, metadata);
        }).ToList();
        return new AudiobookCandidateGroup(
            "Zecharia Sitchin",
            null,
            "Zecharia Sitchin",
            MetadataValueSource.None,
            MetadataValueSource.FolderStructure,
            true,
            parts,
            0.60m,
            ["Title metadata differs across files; the inferred candidate value is shown."]);
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
            IReadOnlyCollection<string> currentCandidateKeys,
            CancellationToken cancellationToken = default)
        {
            SavedEntries.AddRange(entries);
            foreach (var entry in entries)
            {
                Cache[entry.CandidateKey] = entry;
            }

            var currentKeys = currentCandidateKeys.ToHashSet(StringComparer.Ordinal);
            foreach (var staleKey in Cache.Keys.Where(key => !currentKeys.Contains(key)).ToList())
            {
                Cache.Remove(staleKey);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class StubReviewOverrideStore : IAudiobookReviewOverrideStore
    {
        private readonly Dictionary<string, AudiobookReviewOverrideEntry> _entries =
            new(StringComparer.Ordinal);

        public Task<IReadOnlyDictionary<string, AudiobookReviewOverrideEntry>> LoadAsync(
            Guid librarySourceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, AudiobookReviewOverrideEntry>>(_entries);

        public Task SetGenreAsync(
            Guid librarySourceId,
            string planKey,
            string? genreCategory,
            CancellationToken cancellationToken = default)
        {
            _entries.TryGetValue(planKey, out var current);
            if (genreCategory is null &&
                current?.CanonicalAuthor is null &&
                current?.CanonicalTitle is null)
            {
                _entries.Remove(planKey);
            }
            else
            {
                _entries[planKey] = new AudiobookReviewOverrideEntry(
                    planKey,
                    current?.CanonicalAuthor,
                    current?.CanonicalTitle,
                    genreCategory,
                    DateTime.UtcNow,
                    current?.CollectionHandling ?? AudiobookCollectionHandling.Automatic,
                    current?.SeriesName,
                    current?.SeriesPosition);
            }

            return Task.CompletedTask;
        }

        public Task SetIdentityAsync(
            Guid librarySourceId,
            string planKey,
            string? canonicalAuthor,
            string? canonicalTitle,
            CancellationToken cancellationToken = default)
        {
            _entries.TryGetValue(planKey, out var current);
            if (canonicalAuthor is null && canonicalTitle is null && current?.GenreCategory is null)
            {
                _entries.Remove(planKey);
            }
            else
            {
                _entries[planKey] = new AudiobookReviewOverrideEntry(
                    planKey,
                    canonicalAuthor,
                    canonicalTitle,
                    current?.GenreCategory,
                    DateTime.UtcNow,
                    current?.CollectionHandling ?? AudiobookCollectionHandling.Automatic,
                    current?.SeriesName,
                    current?.SeriesPosition);
            }

            return Task.CompletedTask;
        }

        public Task SetCollectionAsync(
            Guid librarySourceId,
            string planKey,
            AudiobookCollectionHandling collectionHandling,
            string? canonicalAuthor,
            string? seriesName,
            CancellationToken cancellationToken = default)
        {
            _entries.TryGetValue(planKey, out var current);
            _entries[planKey] = new AudiobookReviewOverrideEntry(
                planKey,
                canonicalAuthor,
                null,
                current?.GenreCategory,
                DateTime.UtcNow,
                collectionHandling,
                seriesName,
                null);
            return Task.CompletedTask;
        }

        public Task SetSeriesAsync(
            Guid librarySourceId,
            string planKey,
            string? seriesName,
            int? seriesPosition,
            CancellationToken cancellationToken = default)
        {
            _entries.TryGetValue(planKey, out var current);
            _entries[planKey] = new AudiobookReviewOverrideEntry(
                planKey,
                current?.CanonicalAuthor,
                current?.CanonicalTitle,
                current?.GenreCategory,
                DateTime.UtcNow,
                current?.CollectionHandling ?? AudiobookCollectionHandling.Automatic,
                seriesName,
                seriesPosition);
            return Task.CompletedTask;
        }
    }
}
