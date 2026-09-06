namespace Archivio.Application.Abstractions;

public interface IDatabaseBackupService
{
    Task<string?> CreateBackupAsync(
        string reason,
        CancellationToken cancellationToken = default);
}
