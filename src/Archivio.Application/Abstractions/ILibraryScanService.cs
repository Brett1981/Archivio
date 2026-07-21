namespace Archivio.Application.Abstractions;

public interface ILibraryScanService
{
    Task<LibraryScanResult> ScanAsync(
        Guid librarySourceId,
        CancellationToken cancellationToken = default);
}
