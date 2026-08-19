namespace Archivio.Persistence;

internal sealed class AudiobookExecutionRunEntity
{
    public Guid Id { get; set; }
    public Guid LibrarySourceId { get; set; }
    public int Status { get; set; }
    public int PlannedOperationCount { get; set; }
    public int CompletedOperationCount { get; set; }
    public int RolledBackOperationCount { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? ErrorMessage { get; set; }
    public List<AudiobookExecutionOperationEntity> Operations { get; set; } = [];
}

internal sealed class AudiobookExecutionOperationEntity
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public int SortOrder { get; set; }
    public string PlanKey { get; set; } = string.Empty;
    public string InputSignature { get; set; } = string.Empty;
    public Guid MediaItemId { get; set; }
    public string SourceRelativePath { get; set; } = string.Empty;
    public string DestinationRelativePath { get; set; } = string.Empty;
    public int Kind { get; set; }
    public int Status { get; set; }
    public long SourceSizeBytes { get; set; }
    public DateTime SourceModifiedAtUtc { get; set; }
    public string? ErrorMessage { get; set; }
    public AudiobookExecutionRunEntity Run { get; set; } = null!;
}
