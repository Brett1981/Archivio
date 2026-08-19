using Archivio.Domain;

namespace Archivio.Application.Abstractions;

public interface ILibrarySourceService
{
    Task<IReadOnlyList<LibrarySource>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<LibrarySource> CreateAsync(
        string name,
        string path,
        LibrarySourceType type,
        CancellationToken cancellationToken = default);
    Task<LibrarySource> UpdateAsync(
        Guid id,
        string name,
        string path,
        LibrarySourceType type,
        bool isEnabled,
        CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
