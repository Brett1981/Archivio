namespace Archivio.Application.Abstractions;

public interface IAudiobookBatchExecutionService
{
    Task<AudiobookExecutionResult> ExecuteApprovedAsync(
        Guid librarySourceId,
        string libraryRoot,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        IProgress<AudiobookExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<AudiobookExecutionResult> RecoverInterruptedAsync(
        Guid librarySourceId,
        string libraryRoot,
        IProgress<AudiobookExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<AudiobookExecutionRunEntry?> LoadLatestAsync(
        Guid librarySourceId,
        CancellationToken cancellationToken = default);
}
