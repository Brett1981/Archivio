namespace Archivio.Application.Abstractions;

public interface IBackgroundScanService
{
    event Action<LibraryScanProgress>? ProgressChanged;
    event Action<LibraryScanResult>? ScanCompleted;
    event Action<Exception>? ScanFailed;

    bool IsRunning { get; }

    Task<bool> QueueScanAsync(Guid librarySourceId, CancellationToken cancellationToken = default);
    void Cancel();
}
