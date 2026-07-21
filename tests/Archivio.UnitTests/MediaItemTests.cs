using Archivio.Domain;

namespace Archivio.UnitTests;

public sealed class MediaItemTests
{
    [Fact]
    public void Constructor_NormalizesMetadataAndInitializesPresentState()
    {
        var sourceId = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "Archivio.Tests", Guid.NewGuid().ToString("N"));
        var fullPath = Path.Combine(root, "Movies", "Feature.MKV");
        var created = DateTime.UtcNow.AddDays(-2);
        var modified = DateTime.UtcNow.AddDays(-1);
        var scanned = DateTime.UtcNow;

        var item = new MediaItem(sourceId, fullPath, Path.Combine("Movies", "Feature.MKV"), 1234, created, modified, scanned);

        Assert.NotEqual(Guid.Empty, item.Id);
        Assert.Equal(sourceId, item.LibrarySourceId);
        Assert.Equal(Path.GetFullPath(fullPath), item.FullPath);
        Assert.Equal("Feature.MKV", item.FileName);
        Assert.Equal(".mkv", item.Extension);
        Assert.Equal(1234, item.SizeBytes);
        Assert.Equal(created, item.CreatedAtUtc);
        Assert.Equal(modified, item.ModifiedAtUtc);
        Assert.Equal(scanned, item.LastScannedAtUtc);
        Assert.False(item.IsMissing);
        Assert.Null(item.ContentHash);
    }

    [Fact]
    public void Constructor_RejectsInvalidSourceAndSize()
    {
        var now = DateTime.UtcNow;
        var path = Path.Combine(Path.GetTempPath(), "file.bin");

        Assert.Throws<ArgumentException>(() => new MediaItem(Guid.Empty, path, "file.bin", 1, now, now, now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaItem(Guid.NewGuid(), path, "file.bin", -1, now, now, now));
    }

    [Fact]
    public void Constructor_RejectsRootedRelativePath()
    {
        var now = DateTime.UtcNow;
        var path = Path.Combine(Path.GetTempPath(), "file.bin");

        Assert.Throws<ArgumentException>(() =>
            new MediaItem(Guid.NewGuid(), path, path, 1, now, now, now));
    }

    [Fact]
    public void Constructor_RejectsNonUtcTimestamps()
    {
        var utc = DateTime.UtcNow;
        var local = DateTime.SpecifyKind(utc, DateTimeKind.Local);
        var path = Path.Combine(Path.GetTempPath(), "file.bin");

        Assert.Throws<ArgumentException>(() =>
            new MediaItem(Guid.NewGuid(), path, "file.bin", 1, local, utc, utc));
    }

    [Fact]
    public void MarkMissing_UpdatesScanTimestampAndState()
    {
        var now = DateTime.UtcNow;
        var item = CreateItem(now);
        var later = now.AddMinutes(5);

        item.MarkMissing(later);

        Assert.True(item.IsMissing);
        Assert.Equal(later, item.LastScannedAtUtc);
    }

    [Fact]
    public void Refresh_UpdatesMetadataAndRestoresPresentState()
    {
        var now = DateTime.UtcNow;
        var item = CreateItem(now);
        item.MarkMissing(now.AddMinutes(1));
        var refreshedAt = now.AddMinutes(2);
        var newPath = Path.Combine(Path.GetTempPath(), "Archivio.Tests", Guid.NewGuid().ToString("N"), "renamed.MP4");

        item.Refresh(newPath, "renamed.MP4", 900, now.AddDays(-3), now.AddMinutes(1), refreshedAt);

        Assert.Equal(Path.GetFullPath(newPath), item.FullPath);
        Assert.Equal("renamed.MP4", item.RelativePath);
        Assert.Equal("renamed.MP4", item.FileName);
        Assert.Equal(".mp4", item.Extension);
        Assert.Equal(900, item.SizeBytes);
        Assert.False(item.IsMissing);
        Assert.Equal(refreshedAt, item.LastScannedAtUtc);
    }

    private static MediaItem CreateItem(DateTime now)
    {
        var path = Path.Combine(Path.GetTempPath(), "Archivio.Tests", Guid.NewGuid().ToString("N"), "file.bin");
        return new MediaItem(Guid.NewGuid(), path, "file.bin", 10, now.AddDays(-1), now, now);
    }
}
