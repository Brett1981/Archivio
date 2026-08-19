namespace Archivio.Application.Abstractions;

public sealed record FileDiscoveryOptions(
    IReadOnlySet<string>? IncludedExtensions,
    IReadOnlySet<string> ExcludedDirectoryNames)
{
    private static readonly IReadOnlySet<string> DefaultExcludedDirectories =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "$RECYCLE.BIN",
            "System Volume Information",
            ".git",
            ".snapshot",
            "@eaDir",
            "lost+found"
        };

    public static FileDiscoveryOptions Default { get; } =
        new(null, DefaultExcludedDirectories);

    public bool IncludesExtension(string extension) =>
        IncludedExtensions is null || IncludedExtensions.Contains(extension);

    public bool ExcludesDirectory(string directoryName) =>
        ExcludedDirectoryNames.Contains(directoryName);
}
