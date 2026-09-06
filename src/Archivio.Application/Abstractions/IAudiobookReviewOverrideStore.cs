namespace Archivio.Application.Abstractions;

public interface IAudiobookReviewOverrideStore
{
    Task<IReadOnlyDictionary<string, AudiobookReviewOverrideEntry>> LoadAsync(
        Guid librarySourceId,
        CancellationToken cancellationToken = default);

    Task SetGenreAsync(
        Guid librarySourceId,
        string planKey,
        string? genreCategory,
        CancellationToken cancellationToken = default);

    Task SetIdentityAsync(
        Guid librarySourceId,
        string planKey,
        string? canonicalAuthor,
        string? canonicalTitle,
        CancellationToken cancellationToken = default);

    Task SetCollectionAsync(
        Guid librarySourceId,
        string planKey,
        AudiobookCollectionHandling collectionHandling,
        string? canonicalAuthor,
        string? seriesName,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    Task SetSeriesAsync(
        Guid librarySourceId,
        string planKey,
        string? seriesName,
        int? seriesPosition,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
