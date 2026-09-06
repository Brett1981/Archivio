using Archivio.Domain;

namespace Archivio.UnitTests;

public sealed class LibrarySourceTests
{
    [Fact]
    public void Constructor_CreatesEnabledSourceWithNormalizedValues()
    {
        var path = Path.Combine(Path.GetTempPath(), "Archivio", "Audiobooks") + Path.DirectorySeparatorChar;

        var source = new LibrarySource("  Audiobooks  ", path, LibrarySourceType.Audiobooks);

        Assert.NotEqual(Guid.Empty, source.Id);
        Assert.Equal("Audiobooks", source.Name);
        Assert.Equal(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), source.Path);
        Assert.Equal(LibrarySourceType.Audiobooks, source.Type);
        Assert.True(source.IsEnabled);
        Assert.Equal(source.CreatedAtUtc, source.UpdatedAtUtc);
        Assert.Null(source.DestinationPath);
        Assert.Equal(source.Path, source.EffectiveDestinationPath);
    }

    [Fact]
    public void DestinationPath_OverridesSourceAndCanBeResetToDefault()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "Incoming");
        var destinationPath = Path.Combine(Path.GetTempPath(), "Organised");
        var source = new LibrarySource(
            "Audiobooks",
            sourcePath,
            LibrarySourceType.Audiobooks,
            destinationPath);

        Assert.Equal(Path.GetFullPath(destinationPath), source.DestinationPath);
        Assert.Equal(source.DestinationPath, source.EffectiveDestinationPath);

        source.ChangeDestinationPath(null);

        Assert.Null(source.DestinationPath);
        Assert.Equal(source.Path, source.EffectiveDestinationPath);
    }

    [Fact]
    public void Constructor_RejectsNullName()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new LibrarySource(null!, Path.GetTempPath(), LibrarySourceType.Documents));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsEmptyOrWhitespaceName(string name)
    {
        Assert.Throws<ArgumentException>(() =>
            new LibrarySource(name, Path.GetTempPath(), LibrarySourceType.Documents));
    }

    [Fact]
    public void Constructor_RejectsNullPath()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new LibrarySource("Documents", null!, LibrarySourceType.Documents));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsEmptyOrWhitespacePath(string path)
    {
        Assert.Throws<ArgumentException>(() =>
            new LibrarySource("Documents", path, LibrarySourceType.Documents));
    }

    [Fact]
    public void Constructor_RejectsUnsupportedType()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LibrarySource("Unknown", Path.GetTempPath(), (LibrarySourceType)int.MaxValue));
    }

    [Fact]
    public void Mutations_UpdateValuesAndTimestamp()
    {
        var source = new LibrarySource("Music", Path.Combine(Path.GetTempPath(), "Music"), LibrarySourceType.Music);
        var originalTimestamp = source.UpdatedAtUtc;

        source.Rename("Soundtracks");
        source.ChangePath(Path.Combine(Path.GetTempPath(), "Soundtracks"));
        source.ChangeType(LibrarySourceType.Mixed);
        source.SetEnabled(false);

        Assert.Equal("Soundtracks", source.Name);
        Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Soundtracks")), source.Path);
        Assert.Equal(LibrarySourceType.Mixed, source.Type);
        Assert.False(source.IsEnabled);
        Assert.True(source.UpdatedAtUtc >= originalTimestamp);
    }

    [Fact]
    public void SetEnabled_WithCurrentValue_DoesNotChangeTimestamp()
    {
        var source = new LibrarySource("Photos", Path.Combine(Path.GetTempPath(), "Photos"), LibrarySourceType.Photos);
        var timestamp = source.UpdatedAtUtc;

        source.SetEnabled(true);

        Assert.Equal(timestamp, source.UpdatedAtUtc);
    }
}
