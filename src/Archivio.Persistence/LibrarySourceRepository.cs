using Archivio.Application.Abstractions;
using Archivio.Domain;
using Microsoft.EntityFrameworkCore;

namespace Archivio.Persistence;

internal sealed class LibrarySourceRepository(ArchivioDbContext dbContext) : ILibrarySourceRepository
{
    public async Task<IReadOnlyList<LibrarySource>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await dbContext.LibrarySources
            .AsNoTracking()
            .OrderBy(source => source.Name)
            .ThenBy(source => source.Path)
            .ToListAsync(cancellationToken);

    public Task<LibrarySource?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.LibrarySources.SingleOrDefaultAsync(source => source.Id == id, cancellationToken);

    public Task<bool> PathExistsAsync(
        string path,
        Guid? excludingId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalizedPath = NormalizePath(path);

        return dbContext.LibrarySources.AnyAsync(
            source => source.Path == normalizedPath && (!excludingId.HasValue || source.Id != excludingId.Value),
            cancellationToken);
    }

    public Task AddAsync(LibrarySource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return dbContext.LibrarySources.AddAsync(source, cancellationToken).AsTask();
    }

    public void Remove(LibrarySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        dbContext.LibrarySources.Remove(source);
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        dbContext.SaveChangesAsync(cancellationToken);

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path.Trim());
        var root = Path.GetPathRoot(fullPath);

        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
