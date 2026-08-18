using Archivio.Application.Abstractions;
using Archivio.Domain;

namespace Archivio.Application.Services;

internal static class LibrarySourceDiscoveryOptions
{
    private static readonly IReadOnlySet<string> VideoExtensions = Extensions(
        ".avi", ".m2ts", ".m4v", ".mkv", ".mov", ".mp4", ".mpeg", ".mpg", ".ts", ".vob", ".webm", ".wmv");

    private static readonly IReadOnlySet<string> MusicExtensions = Extensions(
        ".aac", ".alac", ".ape", ".flac", ".m4a", ".mp3", ".ogg", ".opus", ".wav", ".wma");

    private static readonly IReadOnlySet<string> AudiobookExtensions = Extensions(
        ".aa", ".aac", ".aax", ".flac", ".m4a", ".m4b", ".mp3", ".ogg", ".opus", ".wav", ".wma");

    private static readonly IReadOnlySet<string> DocumentExtensions = Extensions(
        ".azw", ".azw3", ".cbr", ".cbz", ".doc", ".docx", ".epub", ".mobi", ".odt", ".pdf", ".rtf", ".txt");

    private static readonly IReadOnlySet<string> PhotoExtensions = Extensions(
        ".arw", ".bmp", ".cr2", ".dng", ".gif", ".heic", ".jpeg", ".jpg", ".nef", ".png", ".raw", ".tif", ".tiff", ".webp");

    private static readonly IReadOnlySet<string> MixedExtensions =
        Extensions(VideoExtensions
            .Concat(MusicExtensions)
            .Concat(AudiobookExtensions)
            .Concat(DocumentExtensions)
            .Concat(PhotoExtensions));

    public static FileDiscoveryOptions For(LibrarySourceType sourceType) =>
        new(
            sourceType switch
            {
                LibrarySourceType.Movies or LibrarySourceType.Television => VideoExtensions,
                LibrarySourceType.Music => MusicExtensions,
                LibrarySourceType.Audiobooks => AudiobookExtensions,
                LibrarySourceType.Documents => DocumentExtensions,
                LibrarySourceType.Photos => PhotoExtensions,
                _ => MixedExtensions
            },
            FileDiscoveryOptions.Default.ExcludedDirectoryNames);

    private static IReadOnlySet<string> Extensions(params string[] values) =>
        Extensions((IEnumerable<string>)values);

    private static IReadOnlySet<string> Extensions(IEnumerable<string> values) =>
        new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
}
