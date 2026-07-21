namespace Archivio.Application.Abstractions;

public sealed record LibraryScanProgress(
    Guid LibrarySourceId,
    LibraryScanStage Stage,
    string Status,
    string? CurrentPath,
    int DiscoveredCount,
    int ProcessedCount,
    int AddedCount,
    int RefreshedCount,
    int MissingCount,
    DateTime StartedAtUtc)
{
    public TimeSpan Elapsed => DateTime.UtcNow - StartedAtUtc;
}
