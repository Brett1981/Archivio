namespace Archivio.Application.Abstractions;

public interface IAppDataPaths
{
    string RootDirectory { get; }
    string DataDirectory { get; }
    string LogsDirectory { get; }
    string DatabasePath { get; }
    void EnsureCreated();
}
