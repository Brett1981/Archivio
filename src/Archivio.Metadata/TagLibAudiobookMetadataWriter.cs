using Archivio.Application.Abstractions;

namespace Archivio.Metadata;

public sealed class TagLibAudiobookMetadataWriter : IAudiobookMetadataWriter
{
    public AudiobookTagState Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        return new AudiobookTagState(
            tag.Title,
            tag.Performers,
            tag.AlbumArtists,
            tag.Album,
            tag.Genres,
            tag.Year,
            tag.Track,
            tag.TrackCount,
            tag.Grouping);
    }

    public void Write(string path, AudiobookTagUpdate update)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(update);
        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        tag.Title = update.Title;
        tag.Performers = [update.Author];
        tag.AlbumArtists = [update.Author];
        tag.Album = update.Album;
        tag.Genres = [update.Genre];
        tag.Year = update.Year ?? 0;

        tag.Track = update.Track;
        tag.TrackCount = update.TrackCount;
        tag.Grouping = string.IsNullOrWhiteSpace(update.SeriesName)
            ? null
            : update.SeriesName;

        file.Save();
    }

    public void Restore(string path, AudiobookTagState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(state);
        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        tag.Title = state.Title;
        tag.Performers = state.Performers.ToArray();
        tag.AlbumArtists = state.AlbumArtists.ToArray();
        tag.Album = state.Album;
        tag.Genres = state.Genres.ToArray();
        tag.Year = state.Year;
        tag.Track = state.Track;
        tag.TrackCount = state.TrackCount;
        tag.Grouping = state.Grouping;
        file.Save();
    }
}
