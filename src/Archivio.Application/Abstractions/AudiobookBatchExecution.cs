namespace Archivio.Application.Abstractions;

public enum AudiobookExecutionRunStatus
{
    Prepared = 0,
    Running = 1,
    Completed = 2,
    FailedRolledBack = 3,
    FailedNeedsRecovery = 4,
    CancelledRolledBack = 5
}

public enum AudiobookExecutionOperationStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    RolledBack = 3,
    Failed = 4,
    RollbackFailed = 5
}

public sealed record AudiobookFileSnapshot(long SizeBytes, DateTime ModifiedAtUtc);

public sealed record AudiobookExecutionOperationEntry(
    Guid Id,
    int SortOrder,
    string PlanKey,
    string InputSignature,
    Guid MediaItemId,
    string SourceRelativePath,
    string DestinationRelativePath,
    AudiobookFileOperationKind Kind,
    AudiobookExecutionOperationStatus Status,
    long SourceSizeBytes,
    DateTime SourceModifiedAtUtc,
    string? ErrorMessage = null);

public sealed record AudiobookExecutionRunEntry(
    Guid Id,
    Guid LibrarySourceId,
    AudiobookExecutionRunStatus Status,
    int PlannedOperationCount,
    int CompletedOperationCount,
    int RolledBackOperationCount,
    DateTime StartedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? CompletedAtUtc,
    string? ErrorMessage,
    IReadOnlyList<AudiobookExecutionOperationEntry> Operations);

public sealed record AudiobookExecutionProgress(
    int ProcessedCount,
    int TotalCount,
    string CurrentPath,
    string Status);

public sealed record AudiobookExecutionResult(
    Guid? RunId,
    AudiobookExecutionRunStatus? Status,
    int PlannedOperationCount,
    int CompletedOperationCount,
    int RolledBackOperationCount,
    string Message)
{
    public bool Succeeded => Status == AudiobookExecutionRunStatus.Completed;
    public bool NeedsRecovery => Status == AudiobookExecutionRunStatus.FailedNeedsRecovery;
}
