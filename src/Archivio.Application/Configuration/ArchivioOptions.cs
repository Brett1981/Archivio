namespace Archivio.Application.Configuration;

public sealed class ArchivioOptions
{
    public const string SectionName = "Archivio";
    public string ProductName { get; init; } = "Archivio";
    public string Version { get; init; } = "0.1.0";
}
