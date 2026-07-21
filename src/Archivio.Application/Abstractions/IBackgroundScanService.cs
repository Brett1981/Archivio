namespace Archivio.Application.Abstractions;

public interface IBackgroundScanService
{
    event EventHandler<LibraryScanProgress>? ProgressChanged;
    event EventHandler<LibraryScanResult>? ScanCompleted;
    event EventHandler<Exception>? ScanFailed;

    bool IsRunning { get; }

    Task<bool> QueueScanAsync(Guid librarySourceId, CancellationToken cancellationToken = default);
    void Cancel();
}
