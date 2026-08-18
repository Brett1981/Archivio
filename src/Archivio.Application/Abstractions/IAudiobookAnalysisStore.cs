using Archivio.Domain;

namespace Archivio.Application.Abstractions;

public interface IAudiobookAnalysisStore
{
    Task<IReadOnlyDictionary<Guid, AudiobookMetadataCacheEntry>> LoadMetadataCacheAsync(
        Guid librarySourceId,
        CancellationToken cancellationToken = default);

    Task<SavedAudiobookAnalysis?> LoadCompletedAnalysisAsync(
        Guid librarySourceId,
        IReadOnlyList<MediaItem> currentMediaItems,
        CancellationToken cancellationToken = default);

    Task BeginAnalysisAsync(
        Guid librarySourceId,
        int totalCount,
        int reusedCount,
        int warningCount,
        CancellationToken cancellationToken = default);

    Task SaveCheckpointAsync(
        Guid librarySourceId,
        IReadOnlyCollection<AudiobookMetadataCacheEntry> metadataEntries,
        int processedCount,
        int totalCount,
        int warningCount,
        CancellationToken cancellationToken = default);

    Task CompleteAnalysisAsync(
        Guid librarySourceId,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        int processedCount,
        int totalCount,
        int warningCount,
        CancellationToken cancellationToken = default);

    Task MarkAnalysisInterruptedAsync(
        Guid librarySourceId,
        bool wasCancelled,
        string? errorMessage,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, OnlineMetadataCacheEntry>> LoadOnlineMetadataCacheAsync(
        Guid librarySourceId,
        CancellationToken cancellationToken = default);

    Task SaveOnlineMetadataCacheAsync(
        Guid librarySourceId,
        IReadOnlyCollection<OnlineMetadataCacheEntry> entries,
        CancellationToken cancellationToken = default);
}

public sealed record AudiobookMetadataCacheEntry(
    Guid MediaItemId,
    long SizeBytes,
    DateTime ModifiedAtUtc,
    DateTime AnalysedAtUtc,
    LocalMediaMetadata Metadata);

public sealed record SavedAudiobookAnalysis(
    DateTime CompletedAtUtc,
    int WarningCount,
    IReadOnlyList<AudiobookCandidateGroup> Candidates);
