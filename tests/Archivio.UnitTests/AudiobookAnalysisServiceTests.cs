using Archivio.Application.Abstractions;
using Archivio.Application.Services;
using Archivio.Domain;

namespace Archivio.UnitTests;

public sealed class AudiobookAnalysisServiceTests
{
    [Fact]
    public void Analyse_FiltersNonAudioAndMissingItems()
    {
        var sourceId = Guid.NewGuid();
        var audio = CreateItem(sourceId, "Author/Book/Author - Book.mp3");
        var image = CreateItem(sourceId, "Author/Book/cover.jpg");
        var missing = CreateItem(sourceId, "Author/Other/Author - Other.m4b");
        missing.MarkMissing(DateTime.UtcNow);
        var metadataService = new StubLocalMediaMetadataService();
        var service = new AudiobookAnalysisService(metadataService);

        var result = service.Analyse([audio, image, missing]);

        var group = Assert.Single(result);
        Assert.Equal("Author", group.Author);
        Assert.Equal("Book", group.Title);
        Assert.Same(audio, Assert.Single(group.Parts).MediaItem);
        Assert.Equal([audio.FullPath], metadataService.ReadPaths);
    }

    [Fact]
    public void Analyse_GroupsAndOrdersMultipartFiles()
    {
        var sourceId = Guid.NewGuid();
        var partTwo = CreateItem(sourceId, "Author/Book/Author - Book Part 2.mp3");
        var partOne = CreateItem(sourceId, "Author/Book/Author - Book Part 1.mp3");
        var service = CreateService();

        var result = service.Analyse([partTwo, partOne]);

        var group = Assert.Single(result);
        Assert.True(group.IsMultipart);
        Assert.Equal([partOne.Id, partTwo.Id], group.Parts.Select(part => part.MediaItem.Id));
        Assert.All(group.Parts, part => Assert.True(part.SequenceWasInferred));
        Assert.Empty(group.Warnings);
    }

    [Fact]
    public void Analyse_UsesEmbeddedTrackNumbersForMultipartOrdering()
    {
        var sourceId = Guid.NewGuid();
        var trackTwo = CreateItem(sourceId, "Author/Book/a.mp3");
        var trackOne = CreateItem(sourceId, "Author/Book/z.mp3");
        var metadataService = new StubLocalMediaMetadataService(path => CreateMetadata(
            path,
            album: new MetadataValue("Book", MetadataValueSource.EmbeddedTag),
            trackNumber: path == trackOne.FullPath ? 1u : 2u));
        var service = new AudiobookAnalysisService(metadataService);

        var group = Assert.Single(service.Analyse([trackTwo, trackOne]));

        Assert.Equal([trackOne.Id, trackTwo.Id], group.Parts.Select(part => part.MediaItem.Id));
        Assert.All(group.Parts, part => Assert.True(part.SequenceWasInferred));
        Assert.DoesNotContain(group.Warnings, warning => warning.Contains("part numbers", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyse_UsesUnanimousEmbeddedAlbumAsMultipartBookTitle()
    {
        var sourceId = Guid.NewGuid();
        var opening = CreateItem(sourceId, "Author/Book/opening.mp3");
        var conclusion = CreateItem(sourceId, "Author/Book/conclusion.mp3");
        var metadataService = new StubLocalMediaMetadataService(path => CreateMetadata(
            path,
            new MetadataValue(
                path == opening.FullPath ? "Opening" : "Conclusion",
                MetadataValueSource.EmbeddedTag),
            new MetadataValue("Author", MetadataValueSource.EmbeddedTag),
            new MetadataValue("Book", MetadataValueSource.EmbeddedTag),
            path == opening.FullPath ? 1u : 2u));
        var service = new AudiobookAnalysisService(metadataService);

        var group = Assert.Single(service.Analyse([conclusion, opening]));

        Assert.Equal("Book", group.Title);
        Assert.Equal(MetadataValueSource.EmbeddedTag, group.TitleSource);
        Assert.DoesNotContain(group.Warnings, warning => warning.Contains("Title metadata differs", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("001 - Book.mp3", "002 - Book.mp3")]
    [InlineData("Chapter 01.mp3", "Chapter 02.mp3")]
    [InlineData("(Author) Book - 01.mp3", "(Author) Book - 02.mp3")]
    public void Analyse_RecognizesCommonNumberedMultipartFilenames(string firstName, string secondName)
    {
        var sourceId = Guid.NewGuid();
        var first = CreateItem(sourceId, $"Author/Book/{firstName}");
        var second = CreateItem(sourceId, $"Author/Book/{secondName}");
        var service = CreateService();

        var group = Assert.Single(service.Analyse([second, first]));

        Assert.Equal([first.Id, second.Id], group.Parts.Select(part => part.MediaItem.Id));
        Assert.All(group.Parts, part => Assert.True(part.SequenceWasInferred));
        Assert.Empty(group.Warnings);
    }

    [Fact]
    public void Analyse_PrefersCompleteFilenameSequenceOverDiscLocalTrackNumbers()
    {
        var sourceId = Guid.NewGuid();
        var items = Enumerable.Range(1, 4)
            .Select(index => CreateItem(
                sourceId,
                $"Roald Dahl/Roald Dahl - The BFG/(Roald Dahl) The BFG - {index:00}.mp3"))
            .ToList();
        var metadataService = new StubLocalMediaMetadataService(path =>
        {
            var fileIndex = items.FindIndex(item => item.FullPath == path) + 1;
            return CreateMetadata(
                path,
                new MetadataValue($"Chapter {fileIndex}", MetadataValueSource.EmbeddedTag),
                new MetadataValue("Roald Dahl", MetadataValueSource.EmbeddedTag),
                new MetadataValue("The BFG", MetadataValueSource.EmbeddedTag),
                (uint)(((fileIndex - 1) % 2) + 1));
        });

        var group = Assert.Single(new AudiobookAnalysisService(metadataService).Analyse(items.AsEnumerable().Reverse()));

        Assert.Equal([1, 2, 3, 4], group.Parts.Select(part => part.Sequence));
        Assert.DoesNotContain(group.Warnings, warning =>
            warning.Contains("Duplicate part number", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyse_NamesFilesWhenARealDuplicatePartNumberRemains()
    {
        var sourceId = Guid.NewGuid();
        var first = CreateItem(sourceId, "Author/Book/opening.mp3");
        var second = CreateItem(sourceId, "Author/Book/conclusion.mp3");
        var metadataService = new StubLocalMediaMetadataService(path => CreateMetadata(
            path,
            album: new MetadataValue("Book", MetadataValueSource.EmbeddedTag),
            trackNumber: 1));

        var group = Assert.Single(new AudiobookAnalysisService(metadataService).Analyse([first, second]));

        var warning = Assert.Single(group.Warnings, warning =>
            warning.Contains("Duplicate part number", StringComparison.Ordinal));
        Assert.Contains(first.FileName, warning, StringComparison.Ordinal);
        Assert.Contains(second.FileName, warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyse_TreatsLeadingPublicationYearAsContextAndUsesAuthorFolder()
    {
        var sourceId = Guid.NewGuid();
        var item = CreateItem(
            sourceId,
            "Stephen King/Stephen King - Rage/1977 - Rage.mp3");

        var group = Assert.Single(CreateService().Analyse([item]));

        Assert.Equal("Stephen King", group.Author);
        Assert.Equal("Rage", group.Title);
    }

    [Fact]
    public void Analyse_UsesFolderStructureWhenFilenameHasNoAuthorTitleSeparator()
    {
        var sourceId = Guid.NewGuid();
        var item = CreateItem(sourceId, "Jane Austen/Pride and Prejudice/track01.m4b");
        var service = CreateService();

        var group = Assert.Single(service.Analyse([item]));

        Assert.Equal("Jane Austen", group.Author);
        Assert.Equal("Pride and Prejudice", group.Title);
        Assert.Equal("Jane Austen - Pride and Prejudice", group.DisplayName);
    }

    [Fact]
    public void Analyse_WarnsWhenMultipartSequenceCannotBeInferred()
    {
        var sourceId = Guid.NewGuid();
        var first = CreateItem(sourceId, "Author/Book/a.mp3");
        var second = CreateItem(sourceId, "Author/Book/b.mp3");
        var service = CreateService();

        var group = Assert.Single(service.Analyse([second, first]));

        Assert.True(group.IsMultipart);
        Assert.Single(group.Warnings);
        Assert.Equal([first.Id, second.Id], group.Parts.Select(part => part.MediaItem.Id));
    }

    [Fact]
    public void Analyse_ExposesReviewPresentationForHighConfidenceCandidate()
    {
        var sourceId = Guid.NewGuid();
        var item = CreateItem(sourceId, "Jane Austen/Pride and Prejudice/Jane Austen - Pride and Prejudice.m4b");
        var service = CreateService();

        var group = Assert.Single(service.Analyse([item]));

        Assert.False(group.NeedsReview);
        Assert.Equal("High confidence", group.ReviewLabel);
        Assert.Equal("Single file", group.TypeLabel);
        Assert.Equal("Jane Austen", group.AuthorDisplay);
        Assert.Equal(4, group.ConfidenceReasons.Count);
    }

    [Fact]
    public void Analyse_ExposesReviewPresentationForAmbiguousMultipartCandidate()
    {
        var sourceId = Guid.NewGuid();
        var first = CreateItem(sourceId, "Book/a.mp3");
        var second = CreateItem(sourceId, "Book/b.mp3");
        var service = CreateService();

        var group = Assert.Single(service.Analyse([first, second]));

        Assert.True(group.NeedsReview);
        Assert.Equal("Needs review", group.ReviewLabel);
        Assert.Equal("Multipart", group.TypeLabel);
        Assert.Equal("Unknown author", group.AuthorDisplay);
        Assert.All(group.Parts, part => Assert.Equal("Filename order", part.OrderingStatus));
    }

    [Fact]
    public void Analyse_UsesLocalMetadataAndPreservesFieldProvenance()
    {
        var sourceId = Guid.NewGuid();
        var item = CreateItem(sourceId, "Unknown/Folder/track01.m4b");
        var metadataService = new StubLocalMediaMetadataService(path => CreateMetadata(
            path,
            new MetadataValue("Pride and Prejudice", MetadataValueSource.EmbeddedTag),
            new MetadataValue("Jane Austen", MetadataValueSource.EmbeddedTag)));

        var group = Assert.Single(new AudiobookAnalysisService(metadataService).Analyse([item]));

        Assert.Equal("Jane Austen", group.Author);
        Assert.Equal("Pride and Prejudice", group.Title);
        Assert.Equal(MetadataValueSource.EmbeddedTag, group.AuthorSource);
        Assert.Equal(MetadataValueSource.EmbeddedTag, group.TitleSource);
        Assert.Equal("Title: Embedded tag · Author: Embedded tag", group.MetadataProvenanceSummary);
        var part = Assert.Single(group.Parts);
        Assert.Equal("Pride and Prejudice", part.Metadata.TitleDisplay);
        Assert.Equal(item.FullPath, Assert.Single(metadataService.ReadPaths));
    }

    [Fact]
    public void Analyse_SurfacesLocalMetadataWarningsWithoutWritingMedia()
    {
        var sourceId = Guid.NewGuid();
        var item = CreateItem(sourceId, "Author/Book/Author - Book.m4b");
        var metadataService = new StubLocalMediaMetadataService(path => CreateMetadata(
            path,
            warnings: ["Metadata could not be read."]));

        var group = Assert.Single(new AudiobookAnalysisService(metadataService).Analyse([item]));

        Assert.True(group.NeedsReview);
        Assert.Contains($"{item.FileName}: Metadata could not be read.", group.Warnings);
        Assert.Equal([item.FullPath], metadataService.ReadPaths);
    }

    [Fact]
    public void Analyse_IgnoresExpectedPerPartFilenameTitleDifferences()
    {
        var sourceId = Guid.NewGuid();
        var partOne = CreateItem(sourceId, "Author/Book/Author - Book Part 1.mp3");
        var partTwo = CreateItem(sourceId, "Author/Book/Author - Book Part 2.mp3");
        var metadataService = new StubLocalMediaMetadataService(path => CreateMetadata(
            path,
            new MetadataValue(Path.GetFileNameWithoutExtension(path), MetadataValueSource.FileName),
            new MetadataValue("Author", MetadataValueSource.FileName)));

        var group = Assert.Single(new AudiobookAnalysisService(metadataService).Analyse([partOne, partTwo]));

        Assert.Equal("Book", group.Title);
        Assert.Equal(MetadataValueSource.Inferred, group.TitleSource);
        Assert.Empty(group.Warnings);
    }

    [Fact]
    public void Analyse_TreatsDifferingEmbeddedTitlesAsChapterNamesWhenFilenameSequenceIsComplete()
    {
        var sourceId = Guid.NewGuid();
        var partOne = CreateItem(sourceId, "Author/Book/Author - Book Part 1.mp3");
        var partTwo = CreateItem(sourceId, "Author/Book/Author - Book Part 2.mp3");
        var metadataService = new StubLocalMediaMetadataService(path => CreateMetadata(
            path,
            new MetadataValue(
                path == partOne.FullPath ? "Opening" : "Conclusion",
                MetadataValueSource.EmbeddedTag),
            new MetadataValue("Author", MetadataValueSource.EmbeddedTag)));

        var group = Assert.Single(new AudiobookAnalysisService(metadataService).Analyse([partOne, partTwo]));

        Assert.Equal("Book", group.Title);
        Assert.DoesNotContain("Title metadata differs across files; the inferred candidate value is shown.", group.Warnings);
        Assert.False(group.NeedsReview);
    }

    [Fact]
    public async Task AnalyseAsync_AutomaticallyReadsAllMetadataAndReportsBothStages()
    {
        var sourceId = Guid.NewGuid();
        var first = CreateItem(sourceId, "Author/First/Author - First.m4b");
        var second = CreateItem(sourceId, "Author/Second/Author - Second.mp3");
        var metadataService = new StubLocalMediaMetadataService();
        var progress = new RecordingProgress<AudiobookAnalysisProgress>();
        var service = new AudiobookAnalysisService(metadataService);

        var groups = await service.AnalyseAsync([first, second], progress);

        Assert.Equal(2, groups.Count);
        Assert.Equal([first.FullPath, second.FullPath], metadataService.ReadPaths);
        Assert.All(groups, group => Assert.True(group.HasLoadedLocalMetadata));
        Assert.Contains(progress.Values, value => value.Stage == AudiobookAnalysisStage.Grouping);
        var finalMetadataProgress = progress.Values
            .Last(value => value.Stage == AudiobookAnalysisStage.ReadingMetadata);
        Assert.Equal(2, finalMetadataProgress.ProcessedCount);
        Assert.Equal(2, finalMetadataProgress.TotalCount);
    }

    [Fact]
    public async Task AnalyseAsync_CanBeCancelledDuringMetadataReading()
    {
        var sourceId = Guid.NewGuid();
        var first = CreateItem(sourceId, "Author/Book/Author - Book Part 1.mp3");
        var second = CreateItem(sourceId, "Author/Book/Author - Book Part 2.mp3");
        using var cancellation = new CancellationTokenSource();
        var metadataService = new StubLocalMediaMetadataService(path =>
        {
            cancellation.Cancel();
            return CreateMetadata(path);
        });
        var service = new AudiobookAnalysisService(metadataService);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.AnalyseAsync([first, second], cancellationToken: cancellation.Token));

        Assert.Single(metadataService.ReadPaths);
    }

    [Fact]
    public async Task AnalyseAsync_ContinuesWhenOneMetadataReaderCallThrowsUnexpectedException()
    {
        var sourceId = Guid.NewGuid();
        var first = CreateItem(sourceId, "Author/First/Author - First.m4b");
        var second = CreateItem(sourceId, "Author/Second/Author - Second.m4b");
        var metadataService = new StubLocalMediaMetadataService(path =>
            path == first.FullPath
                ? throw new InvalidOperationException("Invalid atom layout.")
                : CreateMetadata(path));
        var progress = new RecordingProgress<AudiobookAnalysisProgress>();
        var service = new AudiobookAnalysisService(metadataService);

        var groups = await service.AnalyseAsync([first, second], progress);

        Assert.Equal(2, groups.Count);
        Assert.Equal([first.FullPath, second.FullPath], metadataService.ReadPaths);
        var failedPart = Assert.Single(groups.Single(group => group.Title == "First").Parts);
        Assert.Contains(failedPart.Metadata.Warnings, warning =>
            warning.Contains("InvalidOperationException", StringComparison.Ordinal));
        var finalProgress = progress.Values
            .Last(value => value.Stage == AudiobookAnalysisStage.ReadingMetadata);
        Assert.Equal(2, finalProgress.ProcessedCount);
        Assert.Equal(1, finalProgress.WarningCount);
    }

    [Fact]
    public async Task AnalyseAsync_SeparatesDistinctM4bSeriesBooksBeforeOnlineLookup()
    {
        var sourceId = Guid.NewGuid();
        var files = new[]
        {
            CreateItem(sourceId, "Zecharia Sitchin/The 12th Planet Earth Chronicles Series, Book 1.m4b"),
            CreateItem(sourceId, "Zecharia Sitchin/The Stairway to Heaven Earth Chronicles Series, Book 2.m4b"),
            CreateItem(sourceId, "Zecharia Sitchin/The Cosmic Code Earth Chronicles Series, Book 6.m4b")
        };
        var metadataService = new StubLocalMediaMetadataService(path =>
        {
            var title = Path.GetFileNameWithoutExtension(path);
            return CreateMetadata(
                path,
                new MetadataValue(title, MetadataValueSource.EmbeddedTag),
                new MetadataValue("Zecharia Sitchin", MetadataValueSource.EmbeddedTag)) with
            {
                Duration = TimeSpan.FromHours(8)
            };
        });
        var service = new AudiobookAnalysisService(metadataService);

        var result = await service.AnalyseAsync(files);

        Assert.Equal(3, result.Count);
        Assert.All(result, candidate => Assert.Single(candidate.Parts));
        Assert.Contains(result, candidate => candidate.Title.Contains("12th Planet", StringComparison.Ordinal));
        Assert.Contains(result, candidate => candidate.Title.Contains("Cosmic Code", StringComparison.Ordinal));
        Assert.All(result, candidate => Assert.Equal("Zecharia Sitchin", candidate.Author));
    }

    [Fact]
    public async Task EnrichMetadataAsync_DoesNotReopenFilesForLoadedCandidate()
    {
        var sourceId = Guid.NewGuid();
        var item = CreateItem(sourceId, "Author/Book/Author - Book.m4b");
        var metadataService = new StubLocalMediaMetadataService();
        var service = new AudiobookAnalysisService(metadataService);
        var candidate = Assert.Single(service.Analyse([item]));

        var enrichedAgain = await service.EnrichMetadataAsync(candidate);

        Assert.Same(candidate, enrichedAgain);
        Assert.Equal([item.FullPath], metadataService.ReadPaths);
    }

    [Fact]
    public async Task AnalyseAsync_ReusesUnchangedPersistentMetadataAndSavesCompletedCandidates()
    {
        var sourceId = Guid.NewGuid();
        var item = CreateItem(sourceId, "Author/Book/Author - Book.m4b");
        var cachedMetadata = CreateMetadata(
            item.FullPath,
            new MetadataValue("Saved Book", MetadataValueSource.EmbeddedTag),
            new MetadataValue("Saved Author", MetadataValueSource.EmbeddedTag));
        var store = new RecordingAnalysisStore();
        store.Cache[item.Id] = new AudiobookMetadataCacheEntry(
            item.Id,
            item.SizeBytes,
            item.ModifiedAtUtc,
            DateTime.UtcNow,
            cachedMetadata);
        var metadataService = new StubLocalMediaMetadataService();
        var service = new AudiobookAnalysisService(metadataService, store);

        var result = await service.AnalyseAsync([item]);

        Assert.Empty(metadataService.ReadPaths);
        Assert.Equal(1, store.BeginReusedCount);
        Assert.Equal("Saved Book", Assert.Single(result).Title);
        Assert.Same(result, store.CompletedCandidates);
    }

    [Fact]
    public async Task AnalyseAsync_CheckpointsNewMetadataAndIgnoresStaleCacheEntry()
    {
        var sourceId = Guid.NewGuid();
        var item = CreateItem(sourceId, "Author/Book/Author - Book.m4b");
        var store = new RecordingAnalysisStore();
        store.Cache[item.Id] = new AudiobookMetadataCacheEntry(
            item.Id,
            item.SizeBytes + 1,
            item.ModifiedAtUtc,
            DateTime.UtcNow,
            CreateMetadata(item.FullPath));
        var metadataService = new StubLocalMediaMetadataService();
        var service = new AudiobookAnalysisService(metadataService, store);

        await service.AnalyseAsync([item]);

        Assert.Equal([item.FullPath], metadataService.ReadPaths);
        var checkpoint = Assert.Single(store.CheckpointEntries);
        Assert.Equal(item.Id, checkpoint.MediaItemId);
        Assert.Equal(item.SizeBytes, checkpoint.SizeBytes);
    }

    [Fact]
    public async Task LoadSavedAnalysis_ReturnsNullWhenCurrentAudioSetHasChanged()
    {
        var sourceId = Guid.NewGuid();
        var savedItem = CreateItem(sourceId, "Author/Book/Author - Book.m4b");
        var newItem = CreateItem(sourceId, "Author/New/Author - New.m4b");
        var savedCandidate = Assert.Single(CreateService().Analyse([savedItem]));
        var store = new RecordingAnalysisStore
        {
            SavedAnalysis = new SavedAudiobookAnalysis(DateTime.UtcNow, 0, [savedCandidate])
        };
        var service = new AudiobookAnalysisService(new StubLocalMediaMetadataService(), store);

        var result = await service.LoadSavedAnalysisAsync([savedItem, newItem]);

        Assert.Null(result);
    }

    [Fact]
    public async Task AnalyseAsync_ResumesFromLastCompletedCheckpointAfterCancellation()
    {
        var sourceId = Guid.NewGuid();
        var items = Enumerable.Range(1, 51)
            .Select(index => CreateItem(
                sourceId,
                $"Author/Book {index:D2}/Author - Book {index:D2}.m4b"))
            .ToList();
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        var firstReader = new StubLocalMediaMetadataService(path =>
        {
            reads++;
            if (reads == 51)
            {
                cancellation.Cancel();
            }

            return CreateMetadata(path);
        });
        var store = new RecordingAnalysisStore();
        var firstService = new AudiobookAnalysisService(firstReader, store);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            firstService.AnalyseAsync(items, cancellationToken: cancellation.Token));

        Assert.Equal(50, store.Cache.Count);

        var resumedReader = new StubLocalMediaMetadataService();
        var resumedService = new AudiobookAnalysisService(resumedReader, store);
        var result = await resumedService.AnalyseAsync(items);

        Assert.Equal(50, store.BeginReusedCount);
        Assert.Single(resumedReader.ReadPaths);
        Assert.Equal(51, result.Count);
    }

    private static AudiobookAnalysisService CreateService() => new(new StubLocalMediaMetadataService());

    private static LocalMediaMetadata CreateMetadata(
        string path,
        MetadataValue? title = null,
        MetadataValue? author = null,
        MetadataValue? album = null,
        uint? trackNumber = null,
        IReadOnlyList<string>? warnings = null) =>
        new(
            path,
            title ?? new MetadataValue(null, MetadataValueSource.None),
            author ?? new MetadataValue(null, MetadataValueSource.None),
            album ?? new MetadataValue(null, MetadataValueSource.None),
            new MetadataValue(null, MetadataValueSource.None),
            null,
            trackNumber,
            null,
            null,
            null,
            null,
            null,
            false,
            [],
            warnings ?? []);

    private static MediaItem CreateItem(Guid sourceId, string relativePath)
    {
        var root = Path.Combine(Path.GetTempPath(), "Archivio.Tests", Guid.NewGuid().ToString("N"));
        var now = DateTime.UtcNow;
        return new MediaItem(sourceId, Path.Combine(root, relativePath), relativePath, 1024, now, now, now);
    }

    private sealed class StubLocalMediaMetadataService(
        Func<string, LocalMediaMetadata>? reader = null) : ILocalMediaMetadataService
    {
        public List<string> ReadPaths { get; } = [];

        public LocalMediaMetadata Read(string filePath)
        {
            ReadPaths.Add(filePath);
            return reader?.Invoke(filePath) ?? CreateMetadata(filePath);
        }
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }

    private sealed class RecordingAnalysisStore : IAudiobookAnalysisStore
    {
        public Dictionary<Guid, AudiobookMetadataCacheEntry> Cache { get; } = [];
        public List<AudiobookMetadataCacheEntry> CheckpointEntries { get; } = [];
        public int BeginReusedCount { get; private set; }
        public IReadOnlyList<AudiobookCandidateGroup>? CompletedCandidates { get; private set; }
        public SavedAudiobookAnalysis? SavedAnalysis { get; set; }

        public Task<IReadOnlyDictionary<Guid, AudiobookMetadataCacheEntry>> LoadMetadataCacheAsync(
            Guid librarySourceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, AudiobookMetadataCacheEntry>>(Cache);

        public Task<SavedAudiobookAnalysis?> LoadCompletedAnalysisAsync(
            Guid librarySourceId,
            IReadOnlyList<MediaItem> currentMediaItems,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(SavedAnalysis);

        public Task BeginAnalysisAsync(
            Guid librarySourceId,
            int totalCount,
            int reusedCount,
            int warningCount,
            CancellationToken cancellationToken = default)
        {
            BeginReusedCount = reusedCount;
            return Task.CompletedTask;
        }

        public Task SaveCheckpointAsync(
            Guid librarySourceId,
            IReadOnlyCollection<AudiobookMetadataCacheEntry> metadataEntries,
            int processedCount,
            int totalCount,
            int warningCount,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckpointEntries.AddRange(metadataEntries);
            foreach (var entry in metadataEntries)
            {
                Cache[entry.MediaItemId] = entry;
            }
            return Task.CompletedTask;
        }

        public Task CompleteAnalysisAsync(
            Guid librarySourceId,
            IReadOnlyList<AudiobookCandidateGroup> candidates,
            int processedCount,
            int totalCount,
            int warningCount,
            CancellationToken cancellationToken = default)
        {
            CompletedCandidates = candidates;
            return Task.CompletedTask;
        }

        public Task MarkAnalysisInterruptedAsync(
            Guid librarySourceId,
            bool wasCancelled,
            string? errorMessage,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyDictionary<string, OnlineMetadataCacheEntry>> LoadOnlineMetadataCacheAsync(
            Guid librarySourceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, OnlineMetadataCacheEntry>>(
                new Dictionary<string, OnlineMetadataCacheEntry>());

        public Task SaveOnlineMetadataCacheAsync(
            Guid librarySourceId,
            IReadOnlyCollection<OnlineMetadataCacheEntry> entries,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PruneOnlineMetadataCacheAsync(
            Guid librarySourceId,
            IReadOnlyCollection<string> currentCandidateKeys,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
