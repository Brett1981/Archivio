namespace Archivio.Application.Abstractions;

public interface ILibraryScanService
{
    Task<LibraryScanResult> ScanAsync(
        Guid librarySourceId,
        CancellationToken cancellationToken = default);

    Task<LibraryScanResult> ScanAsync(
        Guid librarySourceId,
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken = default);
}
