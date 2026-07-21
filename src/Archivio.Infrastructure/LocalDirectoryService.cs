using Archivio.Application.Abstractions;

namespace Archivio.Infrastructure;

public sealed class LocalDirectoryService : IDirectoryService
{
    public bool Exists(string path) => Directory.Exists(path);
}
