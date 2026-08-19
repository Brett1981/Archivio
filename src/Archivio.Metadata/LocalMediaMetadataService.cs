using Archivio.Application.Abstractions;
using TagLib;

namespace Archivio.Metadata;

public sealed class LocalMediaMetadataService : ILocalMediaMetadataService
{
    public LocalMediaMetadata Read(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var parsed = MetadataFilenameParser.Parse(filePath);
        var warnings = new List<string>();

        if (!System.IO.File.Exists(filePath))
        {
            warnings.Add("The media file does not exist or is not currently accessible.");
            return BuildFallback(filePath, parsed, warnings);
        }

        try
        {
            using var file = TagLib.File.Create(filePath);
            var tag = file.Tag;
            var properties = file.Properties;

            var embeddedTitle = First(tag.Title, tag.Album);
            var embeddedAuthor = First(tag.FirstPerformer, tag.FirstAlbumArtist, tag.FirstComposer);
            var title = !string.IsNullOrWhiteSpace(embeddedTitle)
                ? new MetadataValue(embeddedTitle.Trim(), MetadataValueSource.EmbeddedTag)
                : new MetadataValue(parsed.Title, parsed.TitleSource);
            var author = !string.IsNullOrWhiteSpace(embeddedAuthor)
                ? new MetadataValue(embeddedAuthor.Trim(), MetadataValueSource.EmbeddedTag)
                : new MetadataValue(parsed.Author, parsed.AuthorSource);

            var codec = properties.Codecs
                .Select(candidate => candidate.Description)
                .FirstOrDefault(description => !string.IsNullOrWhiteSpace(description));

            return new LocalMediaMetadata(
                filePath,
                title,
                author,
                new MetadataValue(tag.Album, string.IsNullOrWhiteSpace(tag.Album) ? MetadataValueSource.None : MetadataValueSource.EmbeddedTag),
                new MetadataValue(tag.FirstGenre, string.IsNullOrWhiteSpace(tag.FirstGenre) ? MetadataValueSource.None : MetadataValueSource.EmbeddedTag),
                tag.Year == 0 ? null : tag.Year,
                tag.Track == 0 ? null : tag.Track,
                properties.Duration == TimeSpan.Zero ? null : properties.Duration,
                properties.AudioBitrate <= 0 ? null : properties.AudioBitrate,
                properties.AudioSampleRate <= 0 ? null : properties.AudioSampleRate,
                properties.AudioChannels <= 0 ? null : properties.AudioChannels,
                codec,
                tag.Pictures.Length > 0,
                parsed.SourceHints,
                warnings);
        }
        catch (Exception exception) when (exception is CorruptFileException or UnsupportedFormatException or IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Metadata could not be read: {exception.Message}");
            return BuildFallback(filePath, parsed, warnings);
        }
    }

    private static LocalMediaMetadata BuildFallback(
        string filePath,
        ParsedFileMetadata parsed,
        IReadOnlyList<string> warnings) =>
        new(
            filePath,
            new MetadataValue(parsed.Title, parsed.TitleSource),
            new MetadataValue(parsed.Author, parsed.AuthorSource),
            new MetadataValue(null, MetadataValueSource.None),
            new MetadataValue(null, MetadataValueSource.None),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            parsed.SourceHints,
            warnings);

    private static string? First(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
