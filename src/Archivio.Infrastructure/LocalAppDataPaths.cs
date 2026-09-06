using Archivio.Application.Abstractions;

namespace Archivio.Infrastructure;

public sealed class LocalAppDataPaths : IAppDataPaths
{
    public LocalAppDataPaths()
    {
        RootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Archivio");
        DataDirectory = Path.Combine(RootDirectory, "data");
        LogsDirectory = Path.Combine(RootDirectory, "logs");
        BackupsDirectory = Path.Combine(RootDirectory, "backups");
        DatabasePath = Path.Combine(DataDirectory, "archivio.db");
    }

    public string RootDirectory { get; }
    public string DataDirectory { get; }
    public string LogsDirectory { get; }
    public string BackupsDirectory { get; }
    public string DatabasePath { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(BackupsDirectory);
    }
}
