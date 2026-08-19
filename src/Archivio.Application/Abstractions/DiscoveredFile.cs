namespace Archivio.Application.Abstractions;

public sealed record DiscoveredFile(
    string FullPath,
    string RelativePath,
    string Extension,
    long SizeBytes,
    DateTime LastWriteTimeUtc);
