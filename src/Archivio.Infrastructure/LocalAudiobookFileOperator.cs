using Archivio.Application.Abstractions;

namespace Archivio.Infrastructure;

internal sealed class LocalAudiobookFileOperator : IAudiobookFileOperator
{
    public bool FileExists(string path)
    {
        var attributes = TryGetAttributes(path);
        return attributes is not null &&
               !attributes.Value.HasFlag(FileAttributes.Directory);
    }

    public bool DirectoryExists(string path)
    {
        var attributes = TryGetAttributes(path);
        return attributes is not null &&
               attributes.Value.HasFlag(FileAttributes.Directory);
    }

    public AudiobookFileSnapshot GetSnapshot(string path)
    {
        var info = new FileInfo(path);
        return new AudiobookFileSnapshot(info.Length, info.LastWriteTimeUtc);
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void Move(string sourcePath, string destinationPath) =>
        File.Move(sourcePath, destinationPath, overwrite: false);

    public void WriteAllBytesNew(string path, ReadOnlySpan<byte> data)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(data);
    }

    private static FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }
}
