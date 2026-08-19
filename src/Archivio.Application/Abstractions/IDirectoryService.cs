namespace Archivio.Application.Abstractions;

public interface IDirectoryService
{
    bool Exists(string path);
}
