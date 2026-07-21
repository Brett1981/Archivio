namespace Archivio.Application.Abstractions;

public interface IFileDiscoveryService
{
    Task<FileDiscoveryResult> DiscoverAsync(
        string rootPath,
        CancellationToken cancellationToken = default);
}
