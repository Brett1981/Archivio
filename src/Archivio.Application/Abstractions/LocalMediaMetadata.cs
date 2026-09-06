namespace Archivio.Application.Abstractions;

public enum MetadataValueSource
{
    None = 0,
    EmbeddedTag = 1,
    FolderStructure = 2,
    FileName = 3,
    Inferred = 4
}

public sealed record MetadataValue(string? Value, MetadataValueSource Source)
{
    public bool HasValue => !string.IsNullOrWhiteSpace(Value);

    public string SourceDisplay => Source switch
    {
        MetadataValueSource.EmbeddedTag => "Embedded tag",
        MetadataValueSource.FolderStructure => "Folder structure",
        MetadataValueSource.FileName => "File name",
        MetadataValueSource.Inferred => "Analysis inference",
        _ => "Unavailable"
    };
}

public sealed record LocalMediaMetadata(
    string FilePath,
    MetadataValue Title,
    MetadataValue Author,
    MetadataValue Album,
    MetadataValue Genre,
    uint? Year,
    uint? TrackNumber,
    TimeSpan? Duration,
    int? AudioBitrateKbps,
    int? SampleRateHz,
    int? ChannelCount,
    string? CodecDescription,
    bool HasEmbeddedArtwork,
    IReadOnlyList<string> SourceHints,
    IReadOnlyList<string> Warnings,
    uint? TrackCount = null,
    string? SeriesName = null,
    bool HasSidecarArtwork = false)
{
    public bool WasReadSuccessfully => Warnings.Count == 0 ||
        Title.HasValue || Author.HasValue || Duration is not null;

    public string TitleDisplay => Title.HasValue ? Title.Value! : "Unknown title";
    public string AuthorDisplay => Author.HasValue ? Author.Value! : "Unknown author";
    public string AlbumDisplay => Album.HasValue ? Album.Value! : "Not available";
    public string GenreDisplay => Genre.HasValue ? Genre.Value! : "Not available";

    public string DurationDisplay => Duration is null
        ? "Duration unavailable"
        : Duration.Value.TotalHours >= 1
            ? Duration.Value.ToString(@"h\:mm\:ss")
            : Duration.Value.ToString(@"m\:ss");

    public string TechnicalSummary
    {
        get
        {
            var values = new List<string>();
            if (!string.IsNullOrWhiteSpace(CodecDescription)) values.Add(CodecDescription);
            if (AudioBitrateKbps is not null) values.Add($"{AudioBitrateKbps} kbps");
            if (SampleRateHz is not null)
            {
                values.Add($"{SampleRateHz.Value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} Hz");
            }
            if (ChannelCount is not null) values.Add($"{ChannelCount} channel{(ChannelCount == 1 ? string.Empty : "s")}");
            return values.Count == 0 ? "Technical details unavailable" : string.Join(" · ", values);
        }
    }

    public string ArtworkDisplay => (HasEmbeddedArtwork, HasSidecarArtwork) switch
    {
        (true, true) => "Embedded artwork and local cover file",
        (true, false) => "Embedded artwork",
        (false, true) => "Local cover file",
        _ => "No local artwork"
    };
    public string SourceHintsDisplay => SourceHints.Count == 0 ? "No source hints" : string.Join(", ", SourceHints);

    public string ReleaseDetailsDisplay
    {
        get
        {
            var values = new List<string>();
            if (Year is not null) values.Add($"Year {Year}");
            if (TrackNumber is not null)
            {
                values.Add(TrackCount is not null
                    ? $"Track {TrackNumber} of {TrackCount}"
                    : $"Track {TrackNumber}");
            }
            if (!string.IsNullOrWhiteSpace(SeriesName)) values.Add($"Series {SeriesName}");
            return values.Count == 0 ? "Year and track unavailable" : string.Join(" · ", values);
        }
    }
}
