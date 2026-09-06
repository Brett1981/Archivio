namespace Archivio.Persistence;

internal enum AudiobookAnalysisRunStatus
{
    Running = 0,
    Completed = 1,
    Cancelled = 2,
    Failed = 3
}

internal sealed class AudiobookAnalysisRunEntity
{
    public Guid Id { get; set; }
    public Guid LibrarySourceId { get; set; }
    public AudiobookAnalysisRunStatus Status { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public int ProcessedCount { get; set; }
    public int TotalCount { get; set; }
    public int WarningCount { get; set; }
    public int CandidateCount { get; set; }
    public string? ErrorMessage { get; set; }
}

internal sealed class AudiobookMetadataCacheEntity
{
    public Guid MediaItemId { get; set; }
    public Guid LibrarySourceId { get; set; }
    public long SizeBytes { get; set; }
    public DateTime ModifiedAtUtc { get; set; }
    public DateTime AnalysedAtUtc { get; set; }
    public string MetadataJson { get; set; } = string.Empty;
}

internal sealed class AudiobookCandidateSnapshotEntity
{
    public Guid Id { get; set; }
    public Guid AnalysisRunId { get; set; }
    public int SortOrder { get; set; }
    public string CandidateJson { get; set; } = string.Empty;
}

internal sealed class OnlineMetadataCacheEntity
{
    public string CandidateKey { get; set; } = string.Empty;
    public Guid LibrarySourceId { get; set; }
    public string InputSignature { get; set; } = string.Empty;
    public DateTime RetrievedAtUtc { get; set; }
    public string? SuggestionJson { get; set; }
}

internal sealed class AudiobookOrganisationProposalEntity
{
    public string CandidateKey { get; set; } = string.Empty;
    public Guid LibrarySourceId { get; set; }
    public string InputSignature { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; }
    public string ProposalJson { get; set; } = string.Empty;
}

internal sealed class AudiobookBatchDecisionEntity
{
    public Guid LibrarySourceId { get; set; }
    public string PlanKey { get; set; } = string.Empty;
    public string InputSignature { get; set; } = string.Empty;
    public int Decision { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

internal sealed class AudiobookReviewOverrideEntity
{
    public Guid LibrarySourceId { get; set; }
    public string PlanKey { get; set; } = string.Empty;
    public string GenreCategory { get; set; } = string.Empty;
    public string? CanonicalAuthor { get; set; }
    public string? CanonicalTitle { get; set; }
    public int CollectionHandling { get; set; }
    public string? SeriesName { get; set; }
    public int? SeriesPosition { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
