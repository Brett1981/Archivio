using Archivio.Domain;

namespace Archivio.Application.Abstractions;

public interface IAudiobookBatchPlanningService
{
    Task<IReadOnlyList<AudiobookCandidateGroup>> PrepareBatchAsync(
        Guid librarySourceId,
        string libraryRoot,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        IReadOnlyList<MediaItem> indexedMedia,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AudiobookCandidateGroup>> SetDecisionAsync(
        Guid librarySourceId,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        IReadOnlyCollection<string> planKeys,
        AudiobookBatchDecision decision,
        CancellationToken cancellationToken = default);
}
