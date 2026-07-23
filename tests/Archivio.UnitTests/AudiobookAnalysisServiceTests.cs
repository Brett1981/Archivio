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
        var service = new AudiobookAnalysisService();

        var result = service.Analyse([audio, image, missing]);

        var group = Assert.Single(result);
        Assert.Equal("Author", group.Author);
        Assert.Equal("Book", group.Title);
        Assert.Same(audio, Assert.Single(group.Parts).MediaItem);
    }

    [Fact]
    public void Analyse_GroupsAndOrdersMultipartFiles()
    {
        var sourceId = Guid.NewGuid();
        var partTwo = CreateItem(sourceId, "Author/Book/Author - Book Part 2.mp3");
        var partOne = CreateItem(sourceId, "Author/Book/Author - Book Part 1.mp3");
        var service = new AudiobookAnalysisService();

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
        var service = new AudiobookAnalysisService();

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
        var service = new AudiobookAnalysisService();

        var group = Assert.Single(service.Analyse([second, first]));

        Assert.True(group.IsMultipart);
        Assert.Single(group.Warnings);
        Assert.Equal([first.Id, second.Id], group.Parts.Select(part => part.MediaItem.Id));
    }

    private static MediaItem CreateItem(Guid sourceId, string relativePath)
    {
        var root = Path.Combine(Path.GetTempPath(), "Archivio.Tests", Guid.NewGuid().ToString("N"));
        var now = DateTime.UtcNow;
        return new MediaItem(sourceId, Path.Combine(root, relativePath), relativePath, 1024, now, now, now);
    }
}
