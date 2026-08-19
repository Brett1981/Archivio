namespace Archivio.Application.Abstractions;

public interface IAudiobookFileOperator
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    AudiobookFileSnapshot GetSnapshot(string path);
    void CreateDirectory(string path);
    void Move(string sourcePath, string destinationPath);
}
