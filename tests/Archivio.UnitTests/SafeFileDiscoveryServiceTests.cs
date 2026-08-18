using Archivio.Application.Abstractions;
using Archivio.Media;

namespace Archivio.UnitTests;

public sealed class SafeFileDiscoveryServiceTests
{
    [Fact]
    public async Task DiscoverAsync_FindsNestedFilesWithStableRelativePaths()
    {
        using var fixture = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(fixture.Path, "Movies"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Path, "readme.TXT"), "notes");
        await File.WriteAllBytesAsync(Path.Combine(fixture.Path, "Movies", "Film.MKV"), [1, 2, 3, 4]);

        var service = new SafeFileDiscoveryService();

        var result = await service.DiscoverAsync(fixture.Path);

        Assert.Equal(Path.GetFullPath(fixture.Path), result.RootPath);
        Assert.Empty(result.Issues);
        Assert.Equal(2, result.Files.Count);
        Assert.Equal("Movies", Path.GetDirectoryName(result.Files[0].RelativePath));
        Assert.Equal(".mkv", result.Files[0].Extension);
        Assert.Equal(4, result.Files[0].SizeBytes);
        Assert.Equal("readme.TXT", result.Files[1].RelativePath);
        Assert.Equal(".txt", result.Files[1].Extension);
    }

    [Fact]
    public async Task DiscoverAsync_DoesNotModifyDiscoveredFiles()
    {
        using var fixture = new TemporaryDirectory();
        var filePath = Path.Combine(fixture.Path, "archive.bin");
        await File.WriteAllBytesAsync(filePath, [10, 20, 30]);
        var before = new FileInfo(filePath);
        var originalLength = before.Length;
        var originalWriteTime = before.LastWriteTimeUtc;

        var service = new SafeFileDiscoveryService();

        await service.DiscoverAsync(fixture.Path);

        var after = new FileInfo(filePath);
        Assert.True(after.Exists);
        Assert.Equal(originalLength, after.Length);
        Assert.Equal(originalWriteTime, after.LastWriteTimeUtc);
        Assert.Equal([10, 20, 30], await File.ReadAllBytesAsync(filePath));
    }

    [Fact]
    public async Task DiscoverAsync_RejectsMissingRootDirectory()
    {
        var service = new SafeFileDiscoveryService();
        var missingPath = Path.Combine(Path.GetTempPath(), $"Archivio-Missing-{Guid.NewGuid():N}");

        var exception = await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => service.DiscoverAsync(missingPath));

        Assert.Contains(Path.GetFullPath(missingPath), exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiscoverAsync_HonoursCancellation()
    {
        using var fixture = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new SafeFileDiscoveryService();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.DiscoverAsync(fixture.Path, cancellation.Token));
    }

    [Fact]
    public async Task DiscoverAsync_FiltersExtensionsSkipsSystemFoldersAndReportsProgress()
    {
        using var fixture = new TemporaryDirectory();
        var mediaDirectory = Directory.CreateDirectory(Path.Combine(fixture.Path, "Media"));
        var excludedDirectory = Directory.CreateDirectory(Path.Combine(fixture.Path, "System Volume Information"));
        await File.WriteAllBytesAsync(Path.Combine(mediaDirectory.FullName, "book.MP3"), [1, 2, 3]);
        await File.WriteAllTextAsync(Path.Combine(mediaDirectory.FullName, "notes.tmp"), "ignore");
        await File.WriteAllBytesAsync(Path.Combine(excludedDirectory.FullName, "hidden.mp3"), [4, 5, 6]);
        var progressValues = new List<FileDiscoveryProgress>();
        var progress = new InlineProgress<FileDiscoveryProgress>(progressValues.Add);
        var options = new FileDiscoveryOptions(
            new HashSet<string>([".mp3"], StringComparer.OrdinalIgnoreCase),
            FileDiscoveryOptions.Default.ExcludedDirectoryNames);

        var result = await new SafeFileDiscoveryService().DiscoverAsync(
            fixture.Path,
            options,
            progress);

        var file = Assert.Single(result.Files);
        Assert.Equal("book.MP3", Path.GetFileName(file.FullPath));
        Assert.DoesNotContain(result.Files, value => value.FullPath.Contains("System Volume Information"));
        Assert.NotEmpty(progressValues);
        Assert.Equal(1, progressValues[^1].DiscoveredFileCount);
        Assert.Equal(2, progressValues[^1].ScannedDirectoryCount);
        Assert.Equal(0, progressValues[^1].IssueCount);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"Archivio-Discovery-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
