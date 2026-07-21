using Archivio.Application.Abstractions;
using Archivio.Domain;

namespace Archivio.Application.Services;

public sealed class LibraryScanService(
    ILibrarySourceRepository librarySourceRepository,
    IMediaItemRepository mediaItemRepository,
    IFileDiscoveryService fileDiscoveryService) : ILibraryScanService
{
    public Task<LibraryScanResult> ScanAsync(
        Guid librarySourceId,
        CancellationToken cancellationToken = default) =>
        ScanAsync(librarySourceId, null, cancellationToken);

    public async Task<LibraryScanResult> ScanAsync(
        Guid librarySourceId,
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (librarySourceId == Guid.Empty)
        {
            throw new ArgumentException("Library source id is required.", nameof(librarySourceId));
        }

        var startedAtUtc = DateTime.UtcNow;
        Report(progress, librarySourceId, LibraryScanStage.Starting, "Preparing library scan", null, 0, 0, 0, 0, 0, startedAtUtc);

        var source = await librarySourceRepository.GetByIdAsync(librarySourceId, cancellationToken)
            ?? throw new KeyNotFoundException($"Library source '{librarySourceId}' was not found.");

        if (!source.IsEnabled)
        {
            throw new InvalidOperationException($"Library source '{source.Name}' is disabled.");
        }

        Report(progress, source.Id, LibraryScanStage.Discovering, $"Discovering files in {source.Name}", source.Path, 0, 0, 0, 0, 0, startedAtUtc);
        var discovery = await fileDiscoveryService.DiscoverAsync(source.Path, cancellationToken);
        var existingItems = await mediaItemRepository.GetByLibrarySourceIdAsync(source.Id, cancellationToken);
        var existingByPath = existingItems.ToDictionary(item => item.FullPath, StringComparer.OrdinalIgnoreCase);
        var discoveredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newItems = new List<MediaItem>();
        var addedCount = 0;
        var refreshedCount = 0;
        var missingCount = 0;
        var processedCount = 0;

        foreach (var file in discovery.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedPath = Path.GetFullPath(file.FullPath);
            discoveredPaths.Add(normalizedPath);

            if (existingByPath.TryGetValue(normalizedPath, out var existing))
            {
                existing.Refresh(normalizedPath, file.RelativePath, file.SizeBytes, file.LastWriteTimeUtc,
                    file.LastWriteTimeUtc, discovery.CompletedAtUtc);
                refreshedCount++;
            }
            else
            {
                newItems.Add(new MediaItem(source.Id, normalizedPath, file.RelativePath, file.SizeBytes,
                    file.LastWriteTimeUtc, file.LastWriteTimeUtc, discovery.CompletedAtUtc));
                addedCount++;
            }

            processedCount++;
            Report(progress, source.Id, LibraryScanStage.Reconciling, "Reconciling discovered files", normalizedPath,
                discovery.Files.Count, processedCount, addedCount, refreshedCount, missingCount, startedAtUtc);
        }

        foreach (var existing in existingItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!discoveredPaths.Contains(existing.FullPath))
            {
                existing.MarkMissing(discovery.CompletedAtUtc);
                missingCount++;
                Report(progress, source.Id, LibraryScanStage.Reconciling, "Marking missing files", existing.FullPath,
                    discovery.Files.Count, processedCount, addedCount, refreshedCount, missingCount, startedAtUtc);
            }
        }

        Report(progress, source.Id, LibraryScanStage.Saving, "Saving catalogue changes", null,
            discovery.Files.Count, processedCount, addedCount, refreshedCount, missingCount, startedAtUtc);

        if (newItems.Count > 0)
        {
            await mediaItemRepository.AddRangeAsync(newItems, cancellationToken);
        }

        await mediaItemRepository.SaveChangesAsync(cancellationToken);

        var result = new LibraryScanResult(source.Id, discovery.RootPath, discovery.Files.Count, addedCount,
            refreshedCount, missingCount, discovery.Issues, discovery.StartedAtUtc, discovery.CompletedAtUtc);

        Report(progress, source.Id, LibraryScanStage.Completed, "Library scan completed", null,
            discovery.Files.Count, processedCount, addedCount, refreshedCount, missingCount, startedAtUtc);
        return result;
    }

    private static void Report(IProgress<LibraryScanProgress>? progress, Guid sourceId, LibraryScanStage stage,
        string status, string? currentPath, int discovered, int processed, int added, int refreshed, int missing,
        DateTime startedAtUtc) =>
        progress?.Report(new LibraryScanProgress(sourceId, stage, status, currentPath, discovered, processed,
            added, refreshed, missing, startedAtUtc));
}
