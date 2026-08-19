namespace Archivio.Application.Abstractions;

public interface IFileDiscoveryService
{
    Task<FileDiscoveryResult> DiscoverAsync(
        string rootPath,
        CancellationToken cancellationToken = default);

    Task<FileDiscoveryResult> DiscoverAsync(
        string rootPath,
        FileDiscoveryOptions options,
        IProgress<FileDiscoveryProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
