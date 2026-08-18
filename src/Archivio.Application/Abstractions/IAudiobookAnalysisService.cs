using Archivio.Domain;

namespace Archivio.Application.Abstractions;

public interface IAudiobookAnalysisService
{
    IReadOnlyList<AudiobookCandidateGroup> Analyse(IEnumerable<MediaItem> mediaItems);

    Task<IReadOnlyList<AudiobookCandidateGroup>> AnalyseAsync(
        IEnumerable<MediaItem> mediaItems,
        IProgress<AudiobookAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<AudiobookCandidateGroup> EnrichMetadataAsync(
        AudiobookCandidateGroup candidate,
        IProgress<AudiobookAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
