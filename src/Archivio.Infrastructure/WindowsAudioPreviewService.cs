using System.Diagnostics;
using Archivio.Application.Abstractions;

namespace Archivio.Infrastructure;

internal sealed class WindowsAudioPreviewService : IAudioPreviewService
{
    public Task PlayAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("The selected source file is no longer available.", filePath);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = filePath,
            UseShellExecute = true
        });
        return Task.CompletedTask;
    }
}
