namespace Archivio.Application.Abstractions;

public interface IAudiobookBatchDecisionStore
{
    Task<IReadOnlyDictionary<string, AudiobookBatchDecisionEntry>> LoadAsync(
        Guid librarySourceId,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        Guid librarySourceId,
        IReadOnlyCollection<AudiobookBatchDecisionEntry> entries,
        CancellationToken cancellationToken = default);
}
