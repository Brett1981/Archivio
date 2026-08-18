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
    public void Analyse_WarnsWhenEmbeddedTitlesConflictAcrossMultipartFiles()
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
        Assert.Contains("Title metadata differs across files; the inferred candidate value is shown.", group.Warnings);
        Assert.True(group.NeedsReview);
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

    private static AudiobookAnalysisService CreateService() => new(new StubLocalMediaMetadataService());

    private static LocalMediaMetadata CreateMetadata(
        string path,
        MetadataValue? title = null,
        MetadataValue? author = null,
        IReadOnlyList<string>? warnings = null) =>
        new(
            path,
            title ?? new MetadataValue(null, MetadataValueSource.None),
            author ?? new MetadataValue(null, MetadataValueSource.None),
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
}
