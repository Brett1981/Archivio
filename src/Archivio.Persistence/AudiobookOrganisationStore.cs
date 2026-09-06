using System.Text.Json;
using Archivio.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Archivio.Persistence;

internal sealed class AudiobookOrganisationStore(
    IDbContextFactory<ArchivioDbContext> dbContextFactory) : IAudiobookOrganisationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    public async Task<IReadOnlyDictionary<string, AudiobookOrganisationCacheEntry>> LoadAsync(
        Guid librarySourceId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entities = await context.AudiobookOrganisationProposals
            .AsNoTracking()
            .Where(entity => entity.LibrarySourceId == librarySourceId)
            .ToListAsync(cancellationToken);
        var result = new Dictionary<string, AudiobookOrganisationCacheEntry>(StringComparer.Ordinal);

        foreach (var entity in entities)
        {
            try
            {
                var proposal = JsonSerializer.Deserialize<AudiobookOrganisationProposal>(
                    entity.ProposalJson,
                    JsonOptions);
                if (proposal is not null)
                {
                    result[entity.CandidateKey] = new AudiobookOrganisationCacheEntry(
                        entity.CandidateKey,
                        entity.InputSignature,
                        DateTime.SpecifyKind(entity.GeneratedAtUtc, DateTimeKind.Utc),
                        proposal);
                }
            }
            catch (JsonException)
            {
                // A damaged proposal is ignored and regenerated from the saved analysis.
            }
        }

        return result;
    }

    public async Task SaveAsync(
        Guid librarySourceId,
        IReadOnlyCollection<AudiobookOrganisationCacheEntry> entries,
        IReadOnlyCollection<string> currentCandidateKeys,
        CancellationToken cancellationToken = default)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var existing = await context.AudiobookOrganisationProposals
            .Where(entity => entity.LibrarySourceId == librarySourceId)
            .ToDictionaryAsync(entity => entity.CandidateKey, cancellationToken);
        var currentKeys = currentCandidateKeys.ToHashSet(StringComparer.Ordinal);
        context.AudiobookOrganisationProposals.RemoveRange(
            existing.Values.Where(entity => !currentKeys.Contains(entity.CandidateKey)));

        foreach (var entry in entries)
        {
            if (!existing.TryGetValue(entry.CandidateKey, out var entity))
            {
                entity = new AudiobookOrganisationProposalEntity { CandidateKey = entry.CandidateKey };
                context.AudiobookOrganisationProposals.Add(entity);
            }

            entity.LibrarySourceId = librarySourceId;
            entity.InputSignature = entry.InputSignature;
            entity.GeneratedAtUtc = entry.GeneratedAtUtc;
            entity.ProposalJson = JsonSerializer.Serialize(entry.Proposal, JsonOptions);
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
