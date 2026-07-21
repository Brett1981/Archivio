using Archivio.Application.Abstractions;
using Archivio.Domain;

namespace Archivio.Application.Services;

public sealed class LibraryScanService(
    ILibrarySourceRepository librarySourceRepository,
    IMediaItemRepository mediaItemRepository,
    IFileDiscoveryService fileDiscoveryService) : ILibraryScanService
{
    public async Task<LibraryScanResult> ScanAsync(
        Guid librarySourceId,
        CancellationToken cancellationToken = default)
    {
        if (librarySourceId == Guid.Empty)
        {
            throw new ArgumentException("Library source id is required.", nameof(librarySourceId));
        }

        var source = await librarySourceRepository.GetByIdAsync(librarySourceId, cancellationToken)
            ?? throw new KeyNotFoundException($"Library source '{librarySourceId}' was not found.");

        if (!source.IsEnabled)
        {
            throw new InvalidOperationException($"Library source '{source.Name}' is disabled.");
        }

        var discovery = await fileDiscoveryService.DiscoverAsync(source.Path, cancellationToken);
        var existingItems = await mediaItemRepository.GetByLibrarySourceIdAsync(source.Id, cancellationToken);
        var existingByPath = existingItems.ToDictionary(
            item => item.FullPath,
            StringComparer.OrdinalIgnoreCase);
        var discoveredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newItems = new List<MediaItem>();
        var addedCount = 0;
        var refreshedCount = 0;
        var missingCount = 0;

        foreach (var file in discovery.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedPath = Path.GetFullPath(file.FullPath);
            discoveredPaths.Add(normalizedPath);

            if (existingByPath.TryGetValue(normalizedPath, out var existing))
            {
                existing.Refresh(
                    normalizedPath,
                    file.RelativePath,
                    file.SizeBytes,
                    file.LastWriteTimeUtc,
                    file.LastWriteTimeUtc,
                    discovery.CompletedAtUtc);
                refreshedCount++;
                continue;
            }

            newItems.Add(new MediaItem(
                source.Id,
                normalizedPath,
                file.RelativePath,
                file.SizeBytes,
                file.LastWriteTimeUtc,
                file.LastWriteTimeUtc,
                discovery.CompletedAtUtc));
            addedCount++;
        }

        foreach (var existing in existingItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!discoveredPaths.Contains(existing.FullPath))
            {
                existing.MarkMissing(discovery.CompletedAtUtc);
                missingCount++;
            }
        }

        if (newItems.Count > 0)
        {
            await mediaItemRepository.AddRangeAsync(newItems, cancellationToken);
        }

        await mediaItemRepository.SaveChangesAsync(cancellationToken);

        return new LibraryScanResult(
            source.Id,
            discovery.RootPath,
            discovery.Files.Count,
            addedCount,
            refreshedCount,
            missingCount,
            discovery.Issues,
            discovery.StartedAtUtc,
            discovery.CompletedAtUtc);
    }
}
