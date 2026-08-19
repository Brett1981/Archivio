namespace Archivio.Application.Abstractions;

public enum AudiobookAnalysisStage
{
    Grouping = 0,
    ReadingMetadata = 1
}

public sealed record AudiobookAnalysisProgress(
    AudiobookAnalysisStage Stage,
    string? CurrentPath,
    int ProcessedCount,
    int TotalCount,
    int WarningCount = 0)
{
    public string Status => Stage switch
    {
        AudiobookAnalysisStage.Grouping =>
            $"Grouping audiobook files: {ProcessedCount:N0} / {TotalCount:N0}",
        _ => WarningCount == 0
            ? $"Reading local metadata: {ProcessedCount:N0} / {TotalCount:N0}"
            : $"Reading local metadata: {ProcessedCount:N0} / {TotalCount:N0} · {WarningCount:N0} warning{(WarningCount == 1 ? string.Empty : "s")}"
    };
}
