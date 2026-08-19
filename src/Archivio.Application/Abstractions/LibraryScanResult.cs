namespace Archivio.Application.Abstractions;

public sealed record LibraryScanResult(
    Guid LibrarySourceId,
    string RootPath,
    int DiscoveredCount,
    int AddedCount,
    int RefreshedCount,
    int MissingCount,
    IReadOnlyList<FileDiscoveryIssue> Issues,
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc)
{
    public TimeSpan Duration => CompletedAtUtc - StartedAtUtc;
}
