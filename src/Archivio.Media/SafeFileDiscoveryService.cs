using System.Security;
using Archivio.Application.Abstractions;

namespace Archivio.Media;

public sealed class SafeFileDiscoveryService : IFileDiscoveryService
{
    public Task<FileDiscoveryResult> DiscoverAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var normalizedRoot = Path.GetFullPath(rootPath.Trim());
        if (!Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException($"The discovery root '{normalizedRoot}' does not exist.");
        }

        return Task.Run(() => Discover(normalizedRoot, cancellationToken), cancellationToken);
    }

    private static FileDiscoveryResult Discover(string rootPath, CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTime.UtcNow;
        var files = new List<DiscoveredFile>();
        var issues = new List<FileDiscoveryIssue>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(rootPath);

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directoryPath = pendingDirectories.Pop();

            foreach (var filePath in EnumerateEntries(
                         directoryPath,
                         static path => Directory.EnumerateFiles(path),
                         issues))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var info = new FileInfo(filePath);
                    files.Add(new DiscoveredFile(
                        info.FullName,
                        Path.GetRelativePath(rootPath, info.FullName),
                        info.Extension.ToLowerInvariant(),
                        info.Length,
                        info.LastWriteTimeUtc));
                }
                catch (Exception exception) when (IsRecoverableFileSystemException(exception))
                {
                    issues.Add(new FileDiscoveryIssue(filePath, exception.Message));
                }
            }

            foreach (var childDirectory in EnumerateEntries(
                         directoryPath,
                         static path => Directory.EnumerateDirectories(path),
                         issues))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var attributes = File.GetAttributes(childDirectory);
                    if ((attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        pendingDirectories.Push(childDirectory);
                    }
                }
                catch (Exception exception) when (IsRecoverableFileSystemException(exception))
                {
                    issues.Add(new FileDiscoveryIssue(childDirectory, exception.Message));
                }
            }
        }

        files.Sort(static (left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));

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
        ICollection<FileDiscoveryIssue> issues)
    {
        try
        {
            return enumerate(directoryPath).ToArray();
        }
        catch (Exception exception) when (IsRecoverableFileSystemException(exception))
        {
            issues.Add(new FileDiscoveryIssue(directoryPath, exception.Message));
            return [];
        }
    }

    private static bool IsRecoverableFileSystemException(Exception exception) =>
        exception is UnauthorizedAccessException or IOException or SecurityException;
}
