namespace Archivio.Application.Abstractions;

public interface ILocalMediaMetadataService
{
    LocalMediaMetadata Read(string filePath);
}
