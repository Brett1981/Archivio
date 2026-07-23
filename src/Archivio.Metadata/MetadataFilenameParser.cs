using System.Globalization;
using System.Text.RegularExpressions;

namespace Archivio.Metadata;

public sealed record ParsedFileMetadata(string? Author, string Title, IReadOnlyList<string> SourceHints);

public static partial class MetadataFilenameParser
{
    private static readonly HashSet<string> TechnicalTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "librivox", "audiobook", "audio", "unabridged", "abridged", "mono", "stereo"
    };

    public static ParsedFileMetadata Parse(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var originalName = Path.GetFileNameWithoutExtension(filePath);
        var name = originalName;
        var hints = new List<string>();

        if (name.Contains("librivox", StringComparison.OrdinalIgnoreCase))
        {
            hints.Add("LibriVox");
        }

        name = CamelCaseBoundaryRegex().Replace(name, "$1 $2");
        name = SeparatorsRegex().Replace(name, " ");
        name = BitrateRegex().Replace(name, " ");
        name = TrackPrefixRegex().Replace(name, " ");

        var tokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => !TechnicalTokens.Contains(token))
            .ToArray();

        var cleaned = ToTitleCase(string.Join(' ', tokens));
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = originalName;
        }

        var explicitParts = AuthorTitleSeparatorRegex().Split(cleaned, 2);
        if (explicitParts.Length == 2)
        {
            return new ParsedFileMetadata(
                Normalize(explicitParts[0]),
                Normalize(explicitParts[1]) ?? cleaned,
                hints);
        }

        var parent = Directory.GetParent(filePath)?.Name;
        var grandParent = Directory.GetParent(filePath)?.Parent?.Name;
        if (IsGenericTrackName(originalName) && IsUsefulFolder(parent) && IsUsefulFolder(grandParent))
        {
            return new ParsedFileMetadata(Normalize(grandParent), Normalize(parent) ?? cleaned, hints);
        }

        return new ParsedFileMetadata(null, Normalize(cleaned) ?? cleaned, hints);
    }

    private static bool IsGenericTrackName(string value)
    {
        var normalized = SeparatorsRegex().Replace(value, " ").Trim();
        return GenericTrackNameRegex().IsMatch(normalized);
    }

    private static bool IsUsefulFolder(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Equals("audio", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("audiobooks", StringComparison.OrdinalIgnoreCase);

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Regex.Replace(value.Trim(), @"\s+", " ");
    }

    private static string ToTitleCase(string value)
    {
        var lowered = value.ToLowerInvariant();
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(lowered);
    }

    [GeneratedRegex(@"([a-z])([A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex CamelCaseBoundaryRegex();

    [GeneratedRegex(@"[_\.]+", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatorsRegex();

    [GeneratedRegex(@"\b\d{2,4}\s*k(?:b|bps)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BitrateRegex();

    [GeneratedRegex(@"^\s*(?:track|part|cd|disc)?\s*\d{1,4}\s*[-_\. ]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrackPrefixRegex();

    [GeneratedRegex(@"^(?:track|part|cd|disc)?\s*\d{1,4}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GenericTrackNameRegex();

    [GeneratedRegex(@"\s+-\s+", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorTitleSeparatorRegex();
}
