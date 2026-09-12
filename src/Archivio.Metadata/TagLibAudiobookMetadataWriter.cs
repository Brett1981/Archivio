using Archivio.Application.Abstractions;

namespace Archivio.Metadata;

public sealed class TagLibAudiobookMetadataWriter : IAudiobookMetadataWriter
{
    public AudiobookTagState Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
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
                tag.Grouping,
                tag.Pictures.Select(picture => new AudiobookArtwork(
                    picture.MimeType ?? "application/octet-stream",
                    picture.Data.Data)).ToList());
        }
        catch (Exception exception) when (IsUnsupportedOrCorrupt(exception))
        {
            throw new InvalidDataException(
                $"The audiobook metadata could not be read: {exception.Message}",
                exception);
        }
    }

    public void Write(string path, AudiobookTagUpdate update)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(update);
        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;
            tag.Title = update.Title;
            tag.Performers = [update.Author];
            tag.AlbumArtists = [update.Author];
            tag.Album = update.Album;
            if (!string.IsNullOrWhiteSpace(update.Genre))
            {
                tag.Genres = [update.Genre];
            }

            if (update.Year is not null)
            {
                tag.Year = update.Year.Value;
            }

            tag.Track = update.Track;
            tag.TrackCount = update.TrackCount;
            if (update.SeriesName is not null)
            {
                tag.Grouping = string.IsNullOrWhiteSpace(update.SeriesName)
                    ? null
                    : update.SeriesName;
            }
            if (update.CoverArtwork is not null)
            {
                tag.Pictures = [CreatePicture(update.CoverArtwork)];
            }

            file.Save();
        }
        catch (Exception exception) when (IsUnsupportedOrCorrupt(exception))
        {
            throw new InvalidDataException(
                $"The audiobook metadata could not be updated: {exception.Message}",
                exception);
        }
    }

    public void Restore(string path, AudiobookTagState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(state);
        try
        {
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
            tag.Pictures = state.Pictures?.Select(CreatePicture).ToArray() ?? [];
            file.Save();
        }
        catch (Exception exception) when (IsUnsupportedOrCorrupt(exception))
        {
            throw new InvalidDataException(
                $"The original audiobook metadata could not be restored: {exception.Message}",
                exception);
        }
    }

    private static bool IsUnsupportedOrCorrupt(Exception exception) =>
        exception is TagLib.CorruptFileException or TagLib.UnsupportedFormatException;

    private static TagLib.IPicture CreatePicture(AudiobookArtwork artwork) => new TagLib.Picture
    {
        Type = TagLib.PictureType.FrontCover,
        MimeType = artwork.MimeType,
        Description = "Audiobook cover",
        Data = new TagLib.ByteVector(artwork.Data)
    };
}
