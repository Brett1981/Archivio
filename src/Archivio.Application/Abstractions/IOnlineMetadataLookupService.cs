namespace Archivio.Application.Abstractions;

public interface IOnlineMetadataLookupService
{
    Task<IReadOnlyList<AudiobookCandidateGroup>> EnrichCandidatesAsync(
        Guid librarySourceId,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        IProgress<OnlineMetadataLookupProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record OnlineMetadataLookupProgress(
    int ProcessedCount,
    int TotalCount,
    int MatchCount,
    string? CurrentTitle)
{
    public string Status => TotalCount == 0
        ? "No candidates need online lookup"
        : $"Checking online metadata: {ProcessedCount:N0} / {TotalCount:N0} · {MatchCount:N0} match{(MatchCount == 1 ? string.Empty : "es")}";
}
