using Archivio.Domain;

namespace Archivio.Application.Abstractions;

public interface ILibrarySourceRepository
{
    Task<IReadOnlyList<LibrarySource>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<LibrarySource?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> PathExistsAsync(string path, Guid? excludingId = null, CancellationToken cancellationToken = default);
    Task AddAsync(LibrarySource source, CancellationToken cancellationToken = default);
    void Remove(LibrarySource source);
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
