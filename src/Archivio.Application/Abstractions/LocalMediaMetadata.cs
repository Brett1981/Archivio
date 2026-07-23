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
    IReadOnlyList<string> Warnings)
{
    public bool WasReadSuccessfully => Warnings.Count == 0 ||
        Title.HasValue || Author.HasValue || Duration is not null;
}
