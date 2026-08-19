using Archivio.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Archivio.Persistence;

internal sealed class AudiobookBatchDecisionStore(
    IDbContextFactory<ArchivioDbContext> dbContextFactory) : IAudiobookBatchDecisionStore
{
    public async Task<IReadOnlyDictionary<string, AudiobookBatchDecisionEntry>> LoadAsync(
        Guid librarySourceId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entities = await context.AudiobookBatchDecisions
            .AsNoTracking()
            .Where(entity => entity.LibrarySourceId == librarySourceId)
            .ToListAsync(cancellationToken);
        return entities.ToDictionary(
            entity => entity.PlanKey,
            entity => new AudiobookBatchDecisionEntry(
                entity.PlanKey,
                entity.InputSignature,
                Enum.IsDefined(typeof(AudiobookBatchDecision), entity.Decision)
                    ? (AudiobookBatchDecision)entity.Decision
                    : AudiobookBatchDecision.Pending,
                DateTime.SpecifyKind(entity.UpdatedAtUtc, DateTimeKind.Utc)),
            StringComparer.Ordinal);
    }

    public async Task SaveAsync(
        Guid librarySourceId,
        IReadOnlyCollection<AudiobookBatchDecisionEntry> entries,
        CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0)
        {
            return;
        }

        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await context.AudiobookBatchDecisions
            .Where(entity => entity.LibrarySourceId == librarySourceId)
            .ToDictionaryAsync(entity => entity.PlanKey, StringComparer.Ordinal, cancellationToken);

        foreach (var entry in entries)
        {
            if (!existing.TryGetValue(entry.PlanKey, out var entity))
            {
                entity = new AudiobookBatchDecisionEntity
                {
                    LibrarySourceId = librarySourceId,
                    PlanKey = entry.PlanKey
                };
                context.AudiobookBatchDecisions.Add(entity);
            }

            entity.InputSignature = entry.InputSignature;
            entity.Decision = (int)entry.Decision;
            entity.UpdatedAtUtc = entry.UpdatedAtUtc;
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}
