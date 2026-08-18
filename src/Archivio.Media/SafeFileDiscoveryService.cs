using System.Security;
using Archivio.Application.Abstractions;

namespace Archivio.Media;

public sealed class SafeFileDiscoveryService : IFileDiscoveryService
{
    public Task<FileDiscoveryResult> DiscoverAsync(
        string rootPath,
        CancellationToken cancellationToken = default) =>
        DiscoverAsync(rootPath, FileDiscoveryOptions.Default, null, cancellationToken);

    public Task<FileDiscoveryResult> DiscoverAsync(
        string rootPath,
        FileDiscoveryOptions options,
        IProgress<FileDiscoveryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(options);

        var normalizedRoot = Path.GetFullPath(rootPath.Trim());
        if (!Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException($"The discovery root '{normalizedRoot}' does not exist.");
        }

        return Task.Run(() => Discover(normalizedRoot, options, progress, cancellationToken), cancellationToken);
    }

    private static FileDiscoveryResult Discover(
        string rootPath,
        FileDiscoveryOptions options,
        IProgress<FileDiscoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTime.UtcNow;
        var files = new List<DiscoveredFile>();
        var issues = new List<FileDiscoveryIssue>();
        var pendingDirectories = new Stack<string>();
        var scannedDirectoryCount = 0;
        pendingDirectories.Push(rootPath);

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directoryPath = pendingDirectories.Pop();
            scannedDirectoryCount++;
            Report(progress, directoryPath, files.Count, scannedDirectoryCount, issues.Count);

            foreach (var filePath in EnumerateEntries(
                         directoryPath,
                         static path => Directory.EnumerateFiles(path),
                         issues,
                         cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var extension = Path.GetExtension(filePath).ToLowerInvariant();
                if (!options.IncludesExtension(extension))
                {
                    continue;
                }

                try
                {
                    var info = new FileInfo(filePath);
                    files.Add(new DiscoveredFile(
                        info.FullName,
                        Path.GetRelativePath(rootPath, info.FullName),
                        extension,
                        info.Length,
                        info.LastWriteTimeUtc));

                    if (files.Count == 1 || files.Count % 100 == 0)
                    {
                        Report(progress, filePath, files.Count, scannedDirectoryCount, issues.Count);
                    }
                }
                catch (Exception exception) when (IsRecoverableFileSystemException(exception))
                {
                    issues.Add(new FileDiscoveryIssue(filePath, exception.Message));
                }
            }

            foreach (var childDirectory in EnumerateEntries(
                         directoryPath,
                         static path => Directory.EnumerateDirectories(path),
                         issues,
                         cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (options.ExcludesDirectory(Path.GetFileName(childDirectory)))
                {
                    continue;
                }

                try
                {
                    var attributes = File.GetAttributes(childDirectory);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) == 0)
                    {
                        pendingDirectories.Push(childDirectory);
                    }
                }
                catch (Exception exception) when (IsRecoverableFileSystemException(exception))
                {
                    issues.Add(new FileDiscoveryIssue(childDirectory, exception.Message));
                }
            }

            Report(progress, directoryPath, files.Count, scannedDirectoryCount, issues.Count);
        }

        files.Sort(static (left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));

        Report(progress, rootPath, files.Count, scannedDirectoryCount, issues.Count);

        return new FileDiscoveryResult(
            rootPath,
            files,
            issues,
            startedAtUtc,
            DateTime.UtcNow);
    }

    private static IEnumerable<string> EnumerateEntries(
        string directoryPath,
        Func<string, IEnumerable<string>> enumerate,
        ICollection<FileDiscoveryIssue> issues,
        CancellationToken cancellationToken)
    {
        IEnumerator<string> enumerator;
        try
        {
            enumerator = enumerate(directoryPath).GetEnumerator();
        }
        catch (Exception exception) when (IsRecoverableFileSystemException(exception))
        {
            issues.Add(new FileDiscoveryIssue(directoryPath, exception.Message));
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool hasNext;
                try
                {
                    hasNext = enumerator.MoveNext();
                }
                catch (Exception exception) when (IsRecoverableFileSystemException(exception))
                {
                    issues.Add(new FileDiscoveryIssue(directoryPath, exception.Message));
                    yield break;
                }

                if (!hasNext)
                {
                    yield break;
                }

                yield return enumerator.Current;
            }
        }
    }

    private static void Report(
        IProgress<FileDiscoveryProgress>? progress,
        string currentPath,
        int discoveredFileCount,
        int scannedDirectoryCount,
        int issueCount) =>
        progress?.Report(new FileDiscoveryProgress(
            currentPath,
            discoveredFileCount,
            scannedDirectoryCount,
            issueCount));

    private static bool IsRecoverableFileSystemException(Exception exception) =>
        exception is UnauthorizedAccessException or IOException or SecurityException;
}
