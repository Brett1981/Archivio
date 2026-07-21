using Archivio.Application.Abstractions;
using Archivio.Domain;

namespace Archivio.Application.Services;

public sealed class LibrarySourceService(
    ILibrarySourceRepository repository,
    IDirectoryService directoryService) : ILibrarySourceService
{
    public Task<IReadOnlyList<LibrarySource>> GetAllAsync(CancellationToken cancellationToken = default) =>
        repository.GetAllAsync(cancellationToken);

    public async Task<LibrarySource> CreateAsync(
        string name,
        string path,
        LibrarySourceType type,
        CancellationToken cancellationToken = default)
    {
        var source = new LibrarySource(name, path, type);
        ValidateDirectoryExists(source.Path);

        if (await repository.PathExistsAsync(source.Path, cancellationToken: cancellationToken))
        {
            throw new InvalidOperationException($"A library source already exists for '{source.Path}'.");
        }

        await repository.AddAsync(source, cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        return source;
    }

    public async Task<LibrarySource> UpdateAsync(
        Guid id,
        string name,
        string path,
        LibrarySourceType type,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        var source = await repository.GetByIdAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException($"Library source '{id}' was not found.");

        var normalizedCandidate = new LibrarySource(name, path, type);
        ValidateDirectoryExists(normalizedCandidate.Path);

        if (await repository.PathExistsAsync(normalizedCandidate.Path, id, cancellationToken))
        {
            throw new InvalidOperationException($"A library source already exists for '{normalizedCandidate.Path}'.");
        }

        source.Rename(name);
        source.ChangePath(path);
        source.ChangeType(type);
        source.SetEnabled(isEnabled);

        await repository.SaveChangesAsync(cancellationToken);
        return source;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var source = await repository.GetByIdAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException($"Library source '{id}' was not found.");

        repository.Remove(source);
        await repository.SaveChangesAsync(cancellationToken);
    }

    private void ValidateDirectoryExists(string path)
    {
        if (!directoryService.Exists(path))
        {
            throw new DirectoryNotFoundException($"The library source directory '{path}' does not exist.");
        }
    }
}
