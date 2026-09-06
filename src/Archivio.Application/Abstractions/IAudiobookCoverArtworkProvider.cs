namespace Archivio.Application.Abstractions;

public interface IAudiobookCoverArtworkProvider
{
    Task<AudiobookArtwork?> FetchAsync(
        string coverUrl,
        CancellationToken cancellationToken = default);
}
