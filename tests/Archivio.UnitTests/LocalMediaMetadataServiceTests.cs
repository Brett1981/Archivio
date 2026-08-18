using Archivio.Application.Abstractions;
using Archivio.Metadata;

namespace Archivio.UnitTests;

public sealed class LocalMediaMetadataServiceTests
{
    [Fact]
    public void Parser_RemovesTechnicalTokensAndPreservesSourceHints()
    {
        var path = Path.Combine(Path.GetTempPath(), "Audio", "DivineComedy_dante_32kb_librivox.m4b");

        var result = MetadataFilenameParser.Parse(path);

        Assert.Null(result.Author);
        Assert.Equal("Divine Comedy Dante", result.Title);
        Assert.Contains("LibriVox", result.SourceHints);
    }

    [Fact]
    public void Parser_UsesAuthorAndTitleFoldersWhenAvailable()
    {
        var path = Path.Combine(Path.GetTempPath(), "Audiobooks", "Jane Austen", "Pride and Prejudice", "track01.m4b");

        var result = MetadataFilenameParser.Parse(path);

        Assert.Equal("Jane Austen", result.Author);
        Assert.Equal("Pride and Prejudice", result.Title);
        Assert.Equal(MetadataValueSource.FolderStructure, result.AuthorSource);
        Assert.Equal(MetadataValueSource.FolderStructure, result.TitleSource);
    }

    [Fact]
    public void Parser_UsesExplicitAuthorTitleSeparator()
    {
        var path = Path.Combine(Path.GetTempPath(), "Audio", "Stephen King - It.mp3");

        var result = MetadataFilenameParser.Parse(path);

        Assert.Equal("Stephen King", result.Author);
        Assert.Equal("It", result.Title);
        Assert.Equal(MetadataValueSource.FileName, result.AuthorSource);
        Assert.Equal(MetadataValueSource.FileName, result.TitleSource);
    }

    [Fact]
    public void Read_MissingFileReturnsFilenameFallbackAndWarning()
    {
        var path = Path.Combine(Path.GetTempPath(), "Archivio.Tests", "The_Hobbit_64kb_librivox.m4b");
        var service = new LocalMediaMetadataService();

        var result = service.Read(path);

        Assert.Equal("The Hobbit", result.Title.Value);
        Assert.Equal(MetadataValueSource.FileName, result.Title.Source);
        Assert.False(result.Author.HasValue);
        Assert.Contains("LibriVox", result.SourceHints);
        Assert.Single(result.Warnings);
        Assert.False(result.HasEmbeddedArtwork);
    }

    [Fact]
    public void MetadataPresentation_FormatsValuesAndProvenanceForReview()
    {
        var metadata = new LocalMediaMetadata(
            "book.m4b",
            new MetadataValue("Book", MetadataValueSource.EmbeddedTag),
            new MetadataValue("Author", MetadataValueSource.FolderStructure),
            new MetadataValue("Collection", MetadataValueSource.EmbeddedTag),
            new MetadataValue("Audiobook", MetadataValueSource.EmbeddedTag),
            2026,
            3,
            TimeSpan.FromMinutes(62) + TimeSpan.FromSeconds(5),
            128,
            44_100,
            2,
            "AAC",
            true,
            ["LibriVox"],
            []);

        Assert.Equal("Embedded tag", metadata.Title.SourceDisplay);
        Assert.Equal("Folder structure", metadata.Author.SourceDisplay);
        Assert.Equal("1:02:05", metadata.DurationDisplay);
        Assert.Equal("Year 2026 · Track 3", metadata.ReleaseDetailsDisplay);
        Assert.Equal("AAC · 128 kbps · 44,100 Hz · 2 channels", metadata.TechnicalSummary);
        Assert.Equal("Embedded artwork", metadata.ArtworkDisplay);
        Assert.Equal("LibriVox", metadata.SourceHintsDisplay);
    }
}
