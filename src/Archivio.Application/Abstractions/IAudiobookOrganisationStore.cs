namespace Archivio.Application.Abstractions;

public interface IAudiobookOrganisationStore
{
    Task<IReadOnlyDictionary<string, AudiobookOrganisationCacheEntry>> LoadAsync(
        Guid librarySourceId,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        Guid librarySourceId,
        IReadOnlyCollection<AudiobookOrganisationCacheEntry> entries,
        CancellationToken cancellationToken = default);
}
