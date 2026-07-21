using System.Threading.Channels;
using Archivio.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Archivio.Workers;

public sealed class BackgroundScanWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<BackgroundScanWorker> logger) : BackgroundService, IBackgroundScanService
{
    private readonly Channel<Guid> _requests = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    private readonly object _sync = new();
    private CancellationTokenSource? _activeScanCancellation;
    private bool _isRunning;

    public event Action<LibraryScanProgress>? ProgressChanged;
    public event Action<LibraryScanResult>? ScanCompleted;
    public event Action<Exception>? ScanFailed;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _isRunning;
            }
        }
    }

    public async Task<bool> QueueScanAsync(Guid librarySourceId, CancellationToken cancellationToken = default)
    {
        if (librarySourceId == Guid.Empty)
        {
            throw new ArgumentException("Library source id is required.", nameof(librarySourceId));
        }

        lock (_sync)
        {
            if (_isRunning)
            {
                return false;
            }

            _isRunning = true;
        }

        try
        {
            await _requests.Writer.WriteAsync(librarySourceId, cancellationToken);
            return true;
        }
        catch
        {
            SetIdle();
            throw;
        }
    }

    public void Cancel()
    {
        lock (_sync)
        {
            _activeScanCancellation?.Cancel();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var sourceId in _requests.Reader.ReadAllAsync(stoppingToken))
        {
            using var scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            lock (_sync)
            {
                _activeScanCancellation = scanCancellation;
            }

            try
            {
                using var scope = scopeFactory.CreateScope();
                var scanService = scope.ServiceProvider.GetRequiredService<ILibraryScanService>();
                var progress = new Progress<LibraryScanProgress>(value => ProgressChanged?.Invoke(value));
                var result = await scanService.ScanAsync(sourceId, progress, scanCancellation.Token);
                ScanCompleted?.Invoke(result);
            }
            catch (OperationCanceledException) when (scanCancellation.IsCancellationRequested)
            {
                ProgressChanged?.Invoke(new LibraryScanProgress(
                    sourceId,
                    LibraryScanStage.Cancelled,
                    "Library scan cancelled",
                    null,
                    0,
                    0,
                    0,
                    0,
                    0,
                    DateTime.UtcNow));
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Background scan failed for library source {LibrarySourceId}", sourceId);
                ScanFailed?.Invoke(exception);
            }
            finally
            {
                lock (_sync)
                {
                    _activeScanCancellation = null;
                }

                SetIdle();
            }
        }
    }

    private void SetIdle()
    {
        lock (_sync)
        {
            _isRunning = false;
        }
    }
}
