namespace Archivio.Application.Abstractions;

public interface IAudioPreviewService
{
    Task PlayAsync(
        string filePath,
        CancellationToken cancellationToken = default);
}
