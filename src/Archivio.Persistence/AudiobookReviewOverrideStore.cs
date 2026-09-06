using Archivio.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Archivio.Persistence;

internal sealed class AudiobookReviewOverrideStore(
    IDbContextFactory<ArchivioDbContext> dbContextFactory) : IAudiobookReviewOverrideStore
{
    public async Task<IReadOnlyDictionary<string, AudiobookReviewOverrideEntry>> LoadAsync(
        Guid librarySourceId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.AudiobookReviewOverrides
            .AsNoTracking()
            .Where(entity => entity.LibrarySourceId == librarySourceId)
            .ToDictionaryAsync(
                entity => entity.PlanKey,
                entity => new AudiobookReviewOverrideEntry(
                    entity.PlanKey,
                    entity.CanonicalAuthor,
                    entity.CanonicalTitle,
                    string.IsNullOrWhiteSpace(entity.GenreCategory) ? null : entity.GenreCategory,
                    DateTime.SpecifyKind(entity.UpdatedAtUtc, DateTimeKind.Utc),
                    (AudiobookCollectionHandling)entity.CollectionHandling,
                    entity.SeriesName,
                    entity.SeriesPosition),
                StringComparer.Ordinal,
                cancellationToken);
    }

    public async Task SetGenreAsync(
        Guid librarySourceId,
        string planKey,
        string? genreCategory,
        CancellationToken cancellationToken = default)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.AudiobookReviewOverrides.SingleOrDefaultAsync(
            candidate => candidate.LibrarySourceId == librarySourceId &&
                         candidate.PlanKey == planKey,
            cancellationToken);
        if (entity is null && genreCategory is null)
        {
            return;
        }

        if (entity is null)
        {
            entity = new AudiobookReviewOverrideEntity
            {
                LibrarySourceId = librarySourceId,
                PlanKey = planKey
            };
            context.AudiobookReviewOverrides.Add(entity);
        }

        entity.GenreCategory = genreCategory ?? string.Empty;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        if (IsEmpty(entity))
        {
            context.AudiobookReviewOverrides.Remove(entity);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task SetIdentityAsync(
        Guid librarySourceId,
        string planKey,
        string? canonicalAuthor,
        string? canonicalTitle,
        CancellationToken cancellationToken = default)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.AudiobookReviewOverrides.SingleOrDefaultAsync(
            candidate => candidate.LibrarySourceId == librarySourceId &&
                         candidate.PlanKey == planKey,
            cancellationToken);
        if (entity is null && canonicalAuthor is null && canonicalTitle is null)
        {
            return;
        }

        if (entity is null)
        {
            entity = new AudiobookReviewOverrideEntity
            {
                LibrarySourceId = librarySourceId,
                PlanKey = planKey
            };
            context.AudiobookReviewOverrides.Add(entity);
        }

        entity.CanonicalAuthor = canonicalAuthor;
        entity.CanonicalTitle = canonicalTitle;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        if (IsEmpty(entity))
        {
            context.AudiobookReviewOverrides.Remove(entity);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task SetCollectionAsync(
        Guid librarySourceId,
        string planKey,
        AudiobookCollectionHandling collectionHandling,
        string? canonicalAuthor,
        string? seriesName,
        CancellationToken cancellationToken = default)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await GetOrCreateAsync(context, librarySourceId, planKey, cancellationToken);
        entity.CollectionHandling = (int)collectionHandling;
        entity.CanonicalAuthor = string.IsNullOrWhiteSpace(canonicalAuthor) ? null : canonicalAuthor.Trim();
        entity.CanonicalTitle = null;
        entity.SeriesName = string.IsNullOrWhiteSpace(seriesName) ? null : seriesName.Trim();
        entity.SeriesPosition = null;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        if (IsEmpty(entity))
        {
            context.AudiobookReviewOverrides.Remove(entity);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task SetSeriesAsync(
        Guid librarySourceId,
        string planKey,
        string? seriesName,
        int? seriesPosition,
        CancellationToken cancellationToken = default)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await GetOrCreateAsync(context, librarySourceId, planKey, cancellationToken);
        entity.SeriesName = string.IsNullOrWhiteSpace(seriesName) ? null : seriesName.Trim();
        entity.SeriesPosition = seriesPosition;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        if (IsEmpty(entity))
        {
            context.AudiobookReviewOverrides.Remove(entity);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private static async Task<AudiobookReviewOverrideEntity> GetOrCreateAsync(
        ArchivioDbContext context,
        Guid librarySourceId,
        string planKey,
        CancellationToken cancellationToken)
    {
        var entity = await context.AudiobookReviewOverrides.SingleOrDefaultAsync(
            candidate => candidate.LibrarySourceId == librarySourceId && candidate.PlanKey == planKey,
            cancellationToken);
        if (entity is not null)
        {
            return entity;
        }

        entity = new AudiobookReviewOverrideEntity
        {
            LibrarySourceId = librarySourceId,
            PlanKey = planKey
        };
        context.AudiobookReviewOverrides.Add(entity);
        return entity;
    }

    private static bool IsEmpty(AudiobookReviewOverrideEntity entity) =>
        string.IsNullOrWhiteSpace(entity.CanonicalAuthor) &&
        string.IsNullOrWhiteSpace(entity.CanonicalTitle) &&
        string.IsNullOrWhiteSpace(entity.GenreCategory) &&
        entity.CollectionHandling == (int)AudiobookCollectionHandling.Automatic &&
        string.IsNullOrWhiteSpace(entity.SeriesName) &&
        entity.SeriesPosition is null;
}
