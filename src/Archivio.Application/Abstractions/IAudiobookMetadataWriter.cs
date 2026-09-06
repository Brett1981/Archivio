namespace Archivio.Application.Abstractions;

public sealed record AudiobookTagState(
    string? Title,
    IReadOnlyList<string> Performers,
    IReadOnlyList<string> AlbumArtists,
    string? Album,
    IReadOnlyList<string> Genres,
    uint Year,
    uint Track,
    uint TrackCount,
    string? Grouping);

public sealed record AudiobookTagUpdate(
    string Title,
    string Author,
    string Album,
    string Genre,
    uint? Year,
    uint Track,
    uint TrackCount,
    string? SeriesName);

public interface IAudiobookMetadataWriter
{
    AudiobookTagState Read(string path);
    void Write(string path, AudiobookTagUpdate update);
    void Restore(string path, AudiobookTagState state);
}
