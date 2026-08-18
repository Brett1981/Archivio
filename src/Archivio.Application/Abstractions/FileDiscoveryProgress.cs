namespace Archivio.Application.Abstractions;

public sealed record FileDiscoveryProgress(
    string CurrentPath,
    int DiscoveredFileCount,
    int ScannedDirectoryCount,
    int IssueCount);
