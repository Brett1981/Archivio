using System.Text.Json;
using Archivio.Application.Abstractions;
using Archivio.Domain;
using Microsoft.EntityFrameworkCore;

namespace Archivio.Persistence;

internal sealed class AudiobookAnalysisStore(
    IDbContextFactory<ArchivioDbContext> dbContextFactory) : IAudiobookAnalysisStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    public async Task<IReadOnlyDictionary<Guid, AudiobookMetadataCacheEntry>> LoadMetadataCacheAsync(
        Guid librarySourceId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entities = await context.AudiobookMetadataCache
            .AsNoTracking()
            .Where(entry => entry.LibrarySourceId == librarySourceId)
            .ToListAsync(cancellationToken);
        var result = new Dictionary<Guid, AudiobookMetadataCacheEntry>(entities.Count);

        foreach (var entity in entities)
        {
            try
            {
                var metadata = JsonSerializer.Deserialize<LocalMediaMetadata>(entity.MetadataJson, JsonOptions);
                if (metadata is not null)
                {
                    result[entity.MediaItemId] = new AudiobookMetadataCacheEntry(
                        entity.MediaItemId,
                        entity.SizeBytes,
                        entity.ModifiedAtUtc,
                        entity.AnalysedAtUtc,
                        metadata);
                }
            }
            catch (JsonException)
            {
                // A damaged cache row is ignored and will be replaced during the next analysis.
            }
        }

        return result;
    }

    public async Task<SavedAudiobookAnalysis?> LoadCompletedAnalysisAsync(
        Guid librarySourceId,
        IReadOnlyList<MediaItem> currentMediaItems,
        CancellationToken cancellationToken = default)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await context.AudiobookAnalysisRuns
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.LibrarySourceId == librarySourceId &&
                             candidate.Status == AudiobookAnalysisRunStatus.Completed,
                cancellationToken);
        if (run?.CompletedAtUtc is null)
        {
            return null;
        }

        var snapshots = await context.AudiobookCandidateSnapshots
            .AsNoTracking()
            .Where(snapshot => snapshot.AnalysisRunId == run.Id)
            .OrderBy(snapshot => snapshot.SortOrder)
            .ToListAsync(cancellationToken);
        var cache = await context.AudiobookMetadataCache
            .AsNoTracking()
            .Where(entry => entry.LibrarySourceId == librarySourceId)
            .ToDictionaryAsync(entry => entry.MediaItemId, cancellationToken);
        var onlineCache = await context.OnlineMetadataCache
            .AsNoTracking()
            .Where(entry => entry.LibrarySourceId == librarySourceId)
            .ToDictionaryAsync(entry => entry.CandidateKey, cancellationToken);
        var currentItems = currentMediaItems.ToDictionary(item => item.Id);
        var candidates = new List<AudiobookCandidateGroup>(snapshots.Count);

        try
        {
            foreach (var snapshot in snapshots)
            {
                var payload = JsonSerializer.Deserialize<CandidatePayload>(snapshot.CandidateJson, JsonOptions);
                if (payload is null)
                {
                    return null;
                }

                var parts = new List<AudiobookCandidatePart>(payload.Parts.Count);
                foreach (var partPayload in payload.Parts)
                {
                    if (!currentItems.TryGetValue(partPayload.MediaItemId, out var mediaItem) ||
                        !cache.TryGetValue(partPayload.MediaItemId, out var cacheEntity) ||
                        mediaItem.SizeBytes != cacheEntity.SizeBytes ||
                        mediaItem.ModifiedAtUtc != cacheEntity.ModifiedAtUtc)
                    {
                        return null;
                    }

                    var metadata = JsonSerializer.Deserialize<LocalMediaMetadata>(cacheEntity.MetadataJson, JsonOptions);
                    if (metadata is null)
                    {
                        return null;
                    }

                    parts.Add(new AudiobookCandidatePart(
                        mediaItem,
                        partPayload.Sequence,
                        partPayload.SequenceWasInferred,
                        metadata));
                }

                var candidate = new AudiobookCandidateGroup(
                    payload.DisplayName,
                    payload.Author,
                    payload.Title,
                    payload.AuthorSource,
                    payload.TitleSource,
                    HasLoadedLocalMetadata: true,
                    parts,
                    payload.Confidence,
                    payload.Warnings);
                if (onlineCache.TryGetValue(candidate.CandidateKey, out var onlineEntity) &&
                    onlineEntity.InputSignature == OnlineMetadataIdentity.CreateInputSignature(
                        candidate.Title,
                        candidate.Author) &&
                    onlineEntity.SuggestionJson is not null)
                {
                    candidate = candidate with
                    {
                        OnlineSuggestion = JsonSerializer.Deserialize<OnlineMetadataSuggestion>(
                            onlineEntity.SuggestionJson,
                            JsonOptions)
                    };
                }

                candidates.Add(candidate);
            }
        }
        catch (JsonException)
        {
            return null;
        }

        var completedAtUtc = DateTime.SpecifyKind(run.CompletedAtUtc.Value, DateTimeKind.Utc);
        return candidates.Sum(candidate => candidate.Parts.Count) == run.TotalCount
            ? new SavedAudiobookAnalysis(completedAtUtc, run.WarningCount, candidates)
            : null;
    }

    public async Task BeginAnalysisAsync(
        Guid librarySourceId,
        int totalCount,
        int reusedCount,
        int warningCount,
        CancellationToken cancellationToken = default)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await GetOrCreateRunAsync(context, librarySourceId, cancellationToken);
        var now = DateTime.UtcNow;
        run.Status = AudiobookAnalysisRunStatus.Running;
        run.StartedAtUtc = now;
        run.UpdatedAtUtc = now;
        run.CompletedAtUtc = null;
        run.ProcessedCount = reusedCount;
        run.TotalCount = totalCount;
        run.WarningCount = warningCount;
        run.ErrorMessage = null;
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveCheckpointAsync(
        Guid librarySourceId,
        IReadOnlyCollection<AudiobookMetadataCacheEntry> metadataEntries,
        int processedCount,
        int totalCount,
        int warningCount,
        CancellationToken cancellationToken = default)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var ids = metadataEntries.Select(entry => entry.MediaItemId).ToList();
        var existing = await context.AudiobookMetadataCache
            .Where(entry => ids.Contains(entry.MediaItemId))
            .ToDictionaryAsync(entry => entry.MediaItemId, cancellationToken);

        foreach (var entry in metadataEntries)
        {
            if (!existing.TryGetValue(entry.MediaItemId, out var entity))
            {
                entity = new AudiobookMetadataCacheEntity { MediaItemId = entry.MediaItemId };
                context.AudiobookMetadataCache.Add(entity);
            }

            entity.LibrarySourceId = librarySourceId;
            entity.SizeBytes = entry.SizeBytes;
            entity.ModifiedAtUtc = entry.ModifiedAtUtc;
            entity.AnalysedAtUtc = entry.AnalysedAtUtc;
            entity.MetadataJson = JsonSerializer.Serialize(entry.Metadata, JsonOptions);
        }

        var run = await GetOrCreateRunAsync(context, librarySourceId, cancellationToken);
        run.Status = AudiobookAnalysisRunStatus.Running;
        run.UpdatedAtUtc = DateTime.UtcNow;
        run.ProcessedCount = processedCount;
        run.TotalCount = totalCount;
        run.WarningCount = warningCount;
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task CompleteAnalysisAsync(
        Guid librarySourceId,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        int processedCount,
        int totalCount,
        int warningCount,
        CancellationToken cancellationToken = default)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var run = await GetOrCreateRunAsync(context, librarySourceId, cancellationToken);
        var previousSnapshots = await context.AudiobookCandidateSnapshots
            .Where(snapshot => snapshot.AnalysisRunId == run.Id)
            .ToListAsync(cancellationToken);
        context.AudiobookCandidateSnapshots.RemoveRange(previousSnapshots);

        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var payload = new CandidatePayload(
                candidate.DisplayName,
                candidate.Author,
                candidate.Title,
                candidate.AuthorSource,
                candidate.TitleSource,
                candidate.Confidence,
                candidate.Warnings,
                candidate.Parts
                    .Select(part => new CandidatePartPayload(
                        part.MediaItem.Id,
                        part.Sequence,
                        part.SequenceWasInferred))
                    .ToList());
            context.AudiobookCandidateSnapshots.Add(new AudiobookCandidateSnapshotEntity
            {
                Id = Guid.NewGuid(),
                AnalysisRunId = run.Id,
                SortOrder = index,
                CandidateJson = JsonSerializer.Serialize(payload, JsonOptions)
            });
        }

        var now = DateTime.UtcNow;
        run.Status = AudiobookAnalysisRunStatus.Completed;
        run.UpdatedAtUtc = now;
        run.CompletedAtUtc = now;
        run.ProcessedCount = processedCount;
        run.TotalCount = totalCount;
        run.WarningCount = warningCount;
        run.CandidateCount = candidates.Count;
        run.ErrorMessage = null;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkAnalysisInterruptedAsync(
        Guid librarySourceId,
        bool wasCancelled,
        string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await GetOrCreateRunAsync(context, librarySourceId, cancellationToken);
        run.Status = wasCancelled ? AudiobookAnalysisRunStatus.Cancelled : AudiobookAnalysisRunStatus.Failed;
        run.UpdatedAtUtc = DateTime.UtcNow;
        run.ErrorMessage = string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage[..Math.Min(2048, errorMessage.Length)];
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, OnlineMetadataCacheEntry>> LoadOnlineMetadataCacheAsync(
        Guid librarySourceId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entities = await context.OnlineMetadataCache
            .AsNoTracking()
            .Where(entry => entry.LibrarySourceId == librarySourceId)
            .ToListAsync(cancellationToken);
        var result = new Dictionary<string, OnlineMetadataCacheEntry>(StringComparer.Ordinal);

        foreach (var entity in entities)
        {
            try
            {
                var suggestion = entity.SuggestionJson is null
                    ? null
                    : JsonSerializer.Deserialize<OnlineMetadataSuggestion>(entity.SuggestionJson, JsonOptions);
                result[entity.CandidateKey] = new OnlineMetadataCacheEntry(
                    entity.CandidateKey,
                    entity.InputSignature,
                    DateTime.SpecifyKind(entity.RetrievedAtUtc, DateTimeKind.Utc),
                    suggestion);
            }
            catch (JsonException)
            {
                // A damaged online cache row is ignored and can be refreshed later.
            }
        }

        return result;
    }

    public async Task SaveOnlineMetadataCacheAsync(
        Guid librarySourceId,
        IReadOnlyCollection<OnlineMetadataCacheEntry> entries,
        CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0)
        {
            return;
        }

        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var keys = entries.Select(entry => entry.CandidateKey).ToList();
        var existing = await context.OnlineMetadataCache
            .Where(entry => keys.Contains(entry.CandidateKey))
            .ToDictionaryAsync(entry => entry.CandidateKey, cancellationToken);

        foreach (var entry in entries)
        {
            if (!existing.TryGetValue(entry.CandidateKey, out var entity))
            {
                entity = new OnlineMetadataCacheEntity { CandidateKey = entry.CandidateKey };
                context.OnlineMetadataCache.Add(entity);
            }

            entity.LibrarySourceId = librarySourceId;
            entity.InputSignature = entry.InputSignature;
            entity.RetrievedAtUtc = entry.RetrievedAtUtc;
            entity.SuggestionJson = entry.Suggestion is null
                ? null
                : JsonSerializer.Serialize(entry.Suggestion, JsonOptions);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private static async Task<AudiobookAnalysisRunEntity> GetOrCreateRunAsync(
        ArchivioDbContext context,
        Guid librarySourceId,
        CancellationToken cancellationToken)
    {
        var run = await context.AudiobookAnalysisRuns
            .SingleOrDefaultAsync(candidate => candidate.LibrarySourceId == librarySourceId, cancellationToken);
        if (run is not null)
        {
            return run;
        }

        run = new AudiobookAnalysisRunEntity
        {
            Id = Guid.NewGuid(),
            LibrarySourceId = librarySourceId,
            StartedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        context.AudiobookAnalysisRuns.Add(run);
        return run;
    }

    private sealed record CandidatePayload(
        string DisplayName,
        string? Author,
        string Title,
        MetadataValueSource AuthorSource,
        MetadataValueSource TitleSource,
        decimal Confidence,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<CandidatePartPayload> Parts);

    private sealed record CandidatePartPayload(
        Guid MediaItemId,
        int Sequence,
        bool SequenceWasInferred);
}
