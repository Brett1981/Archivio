namespace Archivio.Application.Abstractions;

public interface IAudiobookExecutionJournalStore
{
    Task CreateAsync(
        AudiobookExecutionRunEntry run,
        CancellationToken cancellationToken = default);

    Task UpdateRunAsync(
        Guid runId,
        AudiobookExecutionRunStatus status,
        int completedOperationCount,
        int rolledBackOperationCount,
        string? errorMessage,
        DateTime updatedAtUtc,
        DateTime? completedAtUtc,
        CancellationToken cancellationToken = default);

    Task UpdateOperationAsync(
        Guid operationId,
        AudiobookExecutionOperationStatus status,
        string? errorMessage,
        CancellationToken cancellationToken = default);

    Task<AudiobookExecutionRunEntry?> LoadLatestAsync(
        Guid librarySourceId,
        CancellationToken cancellationToken = default);
}
