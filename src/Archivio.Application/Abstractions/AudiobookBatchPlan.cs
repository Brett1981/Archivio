namespace Archivio.Application.Abstractions;

public enum AudiobookBatchDecision
{
    Pending = 0,
    Approved = 1,
    Deferred = 2
}

public enum AudiobookBatchValidationStatus
{
    Ready = 0,
    ReviewRequired = 1,
    Conflict = 2,
    NoChange = 3
}

public enum AudiobookFileOperationKind
{
    NoChange = 0,
    Rename = 1,
    MoveAndRename = 2
}

public sealed record AudiobookFileOperation(
    Guid MediaItemId,
    string SourceRelativePath,
    string DestinationRelativePath,
    AudiobookFileOperationKind Kind)
{
    public string KindLabel => Kind switch
    {
        AudiobookFileOperationKind.NoChange => "No change",
        AudiobookFileOperationKind.Rename => "Rename",
        AudiobookFileOperationKind.MoveAndRename => "Move and rename",
        _ => "Review operation"
    };

    public string Display => Kind == AudiobookFileOperationKind.NoChange
        ? $"Keep  {SourceRelativePath}"
        : $"{SourceRelativePath}  →  {DestinationRelativePath}";
}

public sealed record AudiobookBatchPlan(
    string PlanKey,
    string InputSignature,
    string CanonicalDisplay,
    AudiobookBatchValidationStatus ValidationStatus,
    AudiobookBatchDecision Decision,
    IReadOnlyList<AudiobookFileOperation> Operations,
    IReadOnlyList<string> Warnings,
    DateTime PreparedAtUtc)
{
    public bool CanApprove => ValidationStatus == AudiobookBatchValidationStatus.Ready;
    public bool IsBlocked => ValidationStatus is
        AudiobookBatchValidationStatus.ReviewRequired or AudiobookBatchValidationStatus.Conflict;

    public string ValidationLabel => ValidationStatus switch
    {
        AudiobookBatchValidationStatus.Ready => "Ready for batch approval",
        AudiobookBatchValidationStatus.ReviewRequired => "Review required",
        AudiobookBatchValidationStatus.Conflict => "Destination conflict",
        AudiobookBatchValidationStatus.NoChange => "No change required",
        _ => "Validation unavailable"
    };

    public string DecisionLabel => Decision switch
    {
        AudiobookBatchDecision.Approved => "Approved for a future execution stage",
        AudiobookBatchDecision.Deferred => "Deferred",
        _ => "Decision pending"
    };

    public string Summary => $"{ValidationLabel} · {Operations.Count:N0} file operation{(Operations.Count == 1 ? string.Empty : "s")}";
}

public sealed record AudiobookBatchDecisionEntry(
    string PlanKey,
    string InputSignature,
    AudiobookBatchDecision Decision,
    DateTime UpdatedAtUtc);
