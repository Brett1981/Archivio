using Archivio.Application.Abstractions;

namespace Archivio.Infrastructure;

internal sealed class LocalAudiobookFileOperator : IAudiobookFileOperator
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public AudiobookFileSnapshot GetSnapshot(string path)
    {
        var info = new FileInfo(path);
        return new AudiobookFileSnapshot(info.Length, info.LastWriteTimeUtc);
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void Move(string sourcePath, string destinationPath) =>
        File.Move(sourcePath, destinationPath, overwrite: false);
}
