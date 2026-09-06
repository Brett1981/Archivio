namespace Archivio.Application.Abstractions;

public interface IAudiobookOrganisationService
{
    Task<IReadOnlyList<AudiobookCandidateGroup>> PrepareProposalsAsync(
        Guid librarySourceId,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AudiobookCandidateGroup>> SetGenreOverrideAsync(
        Guid librarySourceId,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        string planKey,
        string? genreCategory,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AudiobookCandidateGroup>> SetIdentityOverrideAsync(
        Guid librarySourceId,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        string planKey,
        string? canonicalAuthor,
        string? canonicalTitle,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AudiobookCandidateGroup>> SetCollectionOverrideAsync(
        Guid librarySourceId,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        string planKey,
        AudiobookCollectionHandling collectionHandling,
        string? canonicalAuthor,
        string? seriesName,
        CancellationToken cancellationToken = default) =>
        PrepareProposalsAsync(librarySourceId, candidates, cancellationToken);

    Task<IReadOnlyList<AudiobookCandidateGroup>> SetSeriesOverrideAsync(
        Guid librarySourceId,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        string planKey,
        string? seriesName,
        int? seriesPosition,
        CancellationToken cancellationToken = default) =>
        PrepareProposalsAsync(librarySourceId, candidates, cancellationToken);
}
