namespace Archivio.Domain;

public sealed class MediaItem
{
    private MediaItem() { }

    public MediaItem(Guid librarySourceId, string fullPath, string relativePath, long sizeBytes, DateTime createdAtUtc, DateTime modifiedAtUtc, DateTime scannedAtUtc)
    {
        if (librarySourceId == Guid.Empty) throw new ArgumentException("Library source id is required.", nameof(librarySourceId));
        if (sizeBytes < 0) throw new ArgumentOutOfRangeException(nameof(sizeBytes));

        Id = Guid.NewGuid();
        LibrarySourceId = librarySourceId;
        FullPath = NormalizeFullPath(fullPath);
        RelativePath = ValidateRelativePath(relativePath);
        FileName = Path.GetFileName(FullPath);
        Extension = Path.GetExtension(FullPath).ToLowerInvariant();
        SizeBytes = sizeBytes;
        CreatedAtUtc = EnsureUtc(createdAtUtc, nameof(createdAtUtc));
        ModifiedAtUtc = EnsureUtc(modifiedAtUtc, nameof(modifiedAtUtc));
        LastScannedAtUtc = EnsureUtc(scannedAtUtc, nameof(scannedAtUtc));
        IsMissing = false;
    }

    public Guid Id { get; private set; }
    public Guid LibrarySourceId { get; private set; }
    public LibrarySource? LibrarySource { get; private set; }
    public string FullPath { get; private set; } = string.Empty;
    public string RelativePath { get; private set; } = string.Empty;
    public string FileName { get; private set; } = string.Empty;
    public string Extension { get; private set; } = string.Empty;
    public long SizeBytes { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime ModifiedAtUtc { get; private set; }
    public DateTime LastScannedAtUtc { get; private set; }
    public string? ContentHash { get; private set; }
    public bool IsMissing { get; private set; }

    public void Refresh(string fullPath, string relativePath, long sizeBytes, DateTime createdAtUtc, DateTime modifiedAtUtc, DateTime scannedAtUtc)
    {
        if (sizeBytes < 0) throw new ArgumentOutOfRangeException(nameof(sizeBytes));

        FullPath = NormalizeFullPath(fullPath);
        RelativePath = ValidateRelativePath(relativePath);
        FileName = Path.GetFileName(FullPath);
        Extension = Path.GetExtension(FullPath).ToLowerInvariant();
        SizeBytes = sizeBytes;
        CreatedAtUtc = EnsureUtc(createdAtUtc, nameof(createdAtUtc));
        ModifiedAtUtc = EnsureUtc(modifiedAtUtc, nameof(modifiedAtUtc));
        LastScannedAtUtc = EnsureUtc(scannedAtUtc, nameof(scannedAtUtc));
        IsMissing = false;
    }

    public void MarkMissing(DateTime scannedAtUtc)
    {
        LastScannedAtUtc = EnsureUtc(scannedAtUtc, nameof(scannedAtUtc));
        IsMissing = true;
    }

    private static string NormalizeFullPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var result = Path.GetFullPath(path.Trim());
        return result.Length <= 2048 ? result : throw new ArgumentOutOfRangeException(nameof(path));
    }

    private static string ValidateRelativePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var result = path.Trim();
        if (Path.IsPathRooted(result)) throw new ArgumentException("Relative path must not be rooted.", nameof(path));
        return result.Length <= 2048 ? result : throw new ArgumentOutOfRangeException(nameof(path));
    }

    private static DateTime EnsureUtc(DateTime value, string parameterName) =>
        value.Kind == DateTimeKind.Utc ? value : throw new ArgumentException("Timestamp must be UTC.", parameterName);
}
