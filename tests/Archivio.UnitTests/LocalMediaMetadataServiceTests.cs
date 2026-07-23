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
    }

    [Fact]
    public void Parser_UsesExplicitAuthorTitleSeparator()
    {
        var path = Path.Combine(Path.GetTempPath(), "Audio", "Stephen King - It.mp3");

        var result = MetadataFilenameParser.Parse(path);

        Assert.Equal("Stephen King", result.Author);
        Assert.Equal("It", result.Title);
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
}
