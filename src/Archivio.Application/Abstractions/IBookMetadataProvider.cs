namespace Archivio.Application.Abstractions;

public interface IBookMetadataProvider
{
    string Name { get; }

    Task<IReadOnlyList<OnlineBookSearchResult>> SearchAsync(
        IReadOnlyList<OnlineMetadataQuery> queries,
        CancellationToken cancellationToken = default);
}
