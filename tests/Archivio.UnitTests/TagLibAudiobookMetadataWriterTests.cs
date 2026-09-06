using Archivio.Application.Abstractions;
using Archivio.Metadata;

namespace Archivio.UnitTests;

public sealed class TagLibAudiobookMetadataWriterTests
{
    [Fact]
    public void WriteAndRestore_RoundTripsJournalledTags()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "Metaroq.Metadata.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "book.wav");
        CreateSilentWave(path);
        try
        {
            var writer = new TagLibAudiobookMetadataWriter();
            var original = writer.Read(path);

            writer.Write(path, new AudiobookTagUpdate(
                "The Book - Track 003",
                "The Author",
                "The Book",
                "Fiction",
                2026,
                3,
                12,
                "The Series"));

            var updated = writer.Read(path);
            Assert.Equal("The Book - Track 003", updated.Title);
            Assert.Contains("The Author", updated.Performers);
            Assert.Contains("The Author", updated.AlbumArtists);
            Assert.Equal("The Book", updated.Album);
            Assert.Contains("Fiction", updated.Genres);
            Assert.Equal(2026u, updated.Year);
            Assert.Equal(3u, updated.Track);
            Assert.Equal(12u, updated.TrackCount);
            Assert.Equal("The Series", updated.Grouping);

            writer.Restore(path, original);

            var restored = writer.Read(path);
            Assert.Equal(original.Title, restored.Title);
            Assert.Equal(original.Performers, restored.Performers);
            Assert.Equal(original.AlbumArtists, restored.AlbumArtists);
            Assert.Equal(original.Album, restored.Album);
            Assert.Equal(original.Genres, restored.Genres);
            Assert.Equal(original.Year, restored.Year);
            Assert.Equal(original.Track, restored.Track);
            Assert.Equal(original.TrackCount, restored.TrackCount);
            Assert.Equal(original.Grouping, restored.Grouping);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CreateSilentWave(string path)
    {
        const int sampleRate = 8_000;
        const short channels = 1;
        const short bitsPerSample = 16;
        const int sampleCount = 800;
        var dataLength = sampleCount * channels * bitsPerSample / 8;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8);
        writer.Write((short)(channels * bitsPerSample / 8));
        writer.Write(bitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(dataLength);
        writer.Write(new byte[dataLength]);
    }
}
