namespace Archivio.Application.Abstractions;

public interface IAudiobookBatchExecutionService
{
    Task<AudiobookExecutionResult> ExecuteApprovedAsync(
        Guid librarySourceId,
        string sourceRoot,
        string destinationRoot,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        IProgress<AudiobookExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<AudiobookExecutionResult> RecoverInterruptedAsync(
        Guid librarySourceId,
        string sourceRoot,
        string destinationRoot,
        IProgress<AudiobookExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<AudiobookExecutionRunEntry?> LoadLatestAsync(
        Guid librarySourceId,
        CancellationToken cancellationToken = default);
}
