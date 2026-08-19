using Archivio.Application.Services;
using Archivio.Domain;

namespace Archivio.UnitTests;

public sealed class MediaCatalogueFilterTests
{
    [Fact]
    public void Apply_FiltersByFileNamePathAndExtensionIgnoringCase()
    {
        var sourceId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var ebook = CreateItem(sourceId, "Books/Reference.Guide.EPUB", now);
        var video = CreateItem(sourceId, "Video/movie.m4b", now);

        var byName = MediaCatalogueFilter.Apply([ebook, video], "reference", false);
        var byPath = MediaCatalogueFilter.Apply([ebook, video], "books", false);
        var byExtension = MediaCatalogueFilter.Apply([ebook, video], ".M4B", false);

        Assert.Same(ebook, Assert.Single(byName));
        Assert.Same(ebook, Assert.Single(byPath));
        Assert.Same(video, Assert.Single(byExtension));
    }

    [Fact]
    public void Apply_WhenMissingOnly_ReturnsOnlyMissingItems()
    {
        var sourceId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var available = CreateItem(sourceId, "available.epub", now);
        var missing = CreateItem(sourceId, "missing.epub", now);
        missing.MarkMissing(now);

        var result = MediaCatalogueFilter.Apply([available, missing], null, true);

        Assert.Same(missing, Assert.Single(result));
    }

    [Fact]
    public void Apply_OrdersAvailableBeforeMissingByRelativePath()
    {
        var sourceId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var availableB = CreateItem(sourceId, "B.epub", now);
        var availableA = CreateItem(sourceId, "A.epub", now);
        var missing = CreateItem(sourceId, "0.epub", now);
        missing.MarkMissing(now);

        var result = MediaCatalogueFilter.Apply([availableB, missing, availableA], string.Empty, false);

        Assert.Collection(
            result,
            item => Assert.Same(availableA, item),
            item => Assert.Same(availableB, item),
            item => Assert.Same(missing, item));
    }

    private static MediaItem CreateItem(Guid sourceId, string relativePath, DateTime now)
    {
        var root = Path.Combine(Path.GetTempPath(), "Archivio.Tests", Guid.NewGuid().ToString("N"));
        return new MediaItem(sourceId, Path.Combine(root, relativePath), relativePath, 1024, now, now, now);
    }
}
