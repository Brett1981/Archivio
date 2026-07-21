namespace Archivio.Domain;

public sealed class LibrarySource
{
    private LibrarySource() { }

    public LibrarySource(string name, string path, LibrarySourceType type)
    {
        Id = Guid.NewGuid();
        Name = ValidateName(name);
        Path = NormalizePath(path);
        Type = ValidateType(type);
        IsEnabled = true;
        CreatedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = CreatedAtUtc;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Path { get; private set; } = string.Empty;
    public LibrarySourceType Type { get; private set; }
    public bool IsEnabled { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public void Rename(string name)
    {
        Name = ValidateName(name);
        Touch();
    }

    public void ChangePath(string path)
    {
        Path = NormalizePath(path);
        Touch();
    }

    public void ChangeType(LibrarySourceType type)
    {
        Type = ValidateType(type);
        Touch();
    }

    public void SetEnabled(bool isEnabled)
    {
        if (IsEnabled == isEnabled)
        {
            return;
        }

        IsEnabled = isEnabled;
        Touch();
    }

    private static string ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();

        if (trimmed.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(name), "Library source names cannot exceed 128 characters.");
        }

        return trimmed;
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = System.IO.Path.GetFullPath(path.Trim());
        var root = System.IO.Path.GetPathRoot(fullPath);

        if (!string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            fullPath = fullPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        }

        if (fullPath.Length > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(path), "Library source paths cannot exceed 1024 characters.");
        }

        return fullPath;
    }

    private static LibrarySourceType ValidateType(LibrarySourceType type)
    {
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported library source type.");
        }

        return type;
    }

    private void Touch() => UpdatedAtUtc = DateTime.UtcNow;
}
