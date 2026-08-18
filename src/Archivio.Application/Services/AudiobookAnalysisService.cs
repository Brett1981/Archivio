using System.Globalization;
using System.Text.RegularExpressions;
using Archivio.Application.Abstractions;
using Archivio.Domain;

namespace Archivio.Application.Services;

public sealed partial class AudiobookAnalysisService : IAudiobookAnalysisService
{
    private readonly ILocalMediaMetadataService _localMediaMetadataService;

    public AudiobookAnalysisService(ILocalMediaMetadataService localMediaMetadataService)
    {
        _localMediaMetadataService = localMediaMetadataService ??
            throw new ArgumentNullException(nameof(localMediaMetadataService));
    }

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m4b", ".m4a", ".mp3", ".aac", ".flac", ".ogg", ".opus", ".wav", ".wma"
    };

    public IReadOnlyList<AudiobookCandidateGroup> Analyse(IEnumerable<MediaItem> mediaItems)
    {
        ArgumentNullException.ThrowIfNull(mediaItems);

        var candidates = mediaItems
            .Where(item => !item.IsMissing && AudioExtensions.Contains(item.Extension))
            .Select(Parse)
            .GroupBy(candidate => candidate.GroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(CreateGroup)
            .OrderBy(group => group.Author ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return candidates;
    }

    private ParsedCandidate Parse(MediaItem item)
    {
        var metadata = _localMediaMetadataService.Read(item.FullPath);
        var stem = Path.GetFileNameWithoutExtension(item.FileName).Trim();
        var partMatch = PartNumberRegex().Match(stem);
        var sequence = partMatch.Success && int.TryParse(partMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

        var cleanedStem = partMatch.Success
            ? NormalizeWhitespace(stem.Remove(partMatch.Index, partMatch.Length).Trim(' ', '-', '_', '.'))
            : NormalizeWhitespace(stem);

        var (author, title) = ParseAuthorAndTitle(cleanedStem, item.RelativePath);
        var parentPath = Path.GetDirectoryName(item.RelativePath) ?? string.Empty;
        var normalizedTitle = NormalizeKey(title);
        var groupKey = $"{NormalizeKey(parentPath)}|{normalizedTitle}";

        return new ParsedCandidate(item, groupKey, author, title, sequence, partMatch.Success, metadata);
    }

    private static (string? Author, string Title) ParseAuthorAndTitle(string cleanedStem, string relativePath)
    {
        var separatorIndex = cleanedStem.IndexOf(" - ", StringComparison.Ordinal);
        if (separatorIndex > 0 && separatorIndex < cleanedStem.Length - 3)
        {
            return (
                NormalizeWhitespace(cleanedStem[..separatorIndex]),
                NormalizeWhitespace(cleanedStem[(separatorIndex + 3)..]));
        }

        var parent = Path.GetFileName(Path.GetDirectoryName(relativePath));
        var grandParent = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(relativePath) ?? string.Empty));

        if (!string.IsNullOrWhiteSpace(parent) && !string.Equals(parent, ".", StringComparison.Ordinal))
        {
            var folderTitle = NormalizeWhitespace(parent);
            var author = string.IsNullOrWhiteSpace(grandParent) ? null : NormalizeWhitespace(grandParent);
            return (author, folderTitle);
        }

        return (null, cleanedStem);
    }

    private static AudiobookCandidateGroup CreateGroup(IGrouping<string, ParsedCandidate> candidates)
    {
        var ordered = candidates
            .OrderBy(candidate => candidate.Sequence == 0 ? int.MaxValue : candidate.Sequence)
            .ThenBy(candidate => candidate.Item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var first = ordered[0];
        var warnings = new List<string>();
        if (ordered.Count > 1 && ordered.Any(candidate => !candidate.SequenceWasInferred))
        {
            warnings.Add("One or more part numbers could not be inferred; filename order is being used.");
        }

        var duplicateSequences = ordered
            .Where(candidate => candidate.Sequence > 0)
            .GroupBy(candidate => candidate.Sequence)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        if (duplicateSequences.Count > 0)
        {
            warnings.Add($"Duplicate part numbers detected: {string.Join(", ", duplicateSequences)}.");
        }

        foreach (var candidate in ordered)
        {
            warnings.AddRange(candidate.Metadata.Warnings.Select(warning => $"{candidate.Item.FileName}: {warning}"));
        }

        var fallbackAuthor = new MetadataValue(
            first.Author,
            string.IsNullOrWhiteSpace(first.Author) ? MetadataValueSource.None : MetadataValueSource.Inferred);
        var fallbackTitle = new MetadataValue(first.Title, MetadataValueSource.Inferred);
        var authorMetadata = SelectGroupMetadataValue(
            ordered.Select(candidate => candidate.Metadata.Author), fallbackAuthor, "Author", warnings);
        var titleMetadata = SelectGroupMetadataValue(
            ordered.Select(candidate => candidate.Metadata.Title), fallbackTitle, "Title", warnings);
        var author = authorMetadata.Value;
        var title = titleMetadata.Value ?? first.Title;

        var confidence = CalculateConfidence(author, title, ordered, warnings);
        var parts = ordered
            .Select((candidate, index) => new AudiobookCandidatePart(
                candidate.Item,
                candidate.Sequence > 0 ? candidate.Sequence : index + 1,
                candidate.SequenceWasInferred,
                candidate.Metadata))
            .ToList();
        var displayName = string.IsNullOrWhiteSpace(author)
            ? title
            : $"{author} - {title}";

        return new AudiobookCandidateGroup(
            displayName,
            author,
            title,
            authorMetadata.Source,
            titleMetadata.Source,
            parts,
            confidence,
            warnings);
    }

    private static MetadataValue SelectGroupMetadataValue(
        IEnumerable<MetadataValue> values,
        MetadataValue fallback,
        string fieldName,
        ICollection<string> warnings)
    {
        var availableValues = values
            .Where(value => value.HasValue)
            .ToList();

        if (availableValues.Count == 0)
        {
            return fallback;
        }

        var bestPriority = availableValues.Min(value => GetSourcePriority(value.Source));
        var preferredValues = availableValues
            .Where(value => GetSourcePriority(value.Source) == bestPriority)
            .ToList();
        var distinctValues = preferredValues
            .GroupBy(value => value.Value!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctValues.Count == 1)
        {
            var preferred = distinctValues[0].First();
            return preferred with { Value = preferred.Value!.Trim() };
        }

        if (preferredValues[0].Source is MetadataValueSource.EmbeddedTag or MetadataValueSource.FolderStructure)
        {
            warnings.Add($"{fieldName} metadata differs across files; the inferred candidate value is shown.");
        }

        return fallback;
    }

    private static int GetSourcePriority(MetadataValueSource source) => source switch
    {
        MetadataValueSource.EmbeddedTag => 0,
        MetadataValueSource.FolderStructure => 1,
        MetadataValueSource.FileName => 2,
        MetadataValueSource.Inferred => 3,
        _ => 4
    };

    private static decimal CalculateConfidence(
        string? author,
        string title,
        IReadOnlyCollection<ParsedCandidate> candidates,
        IReadOnlyCollection<string> warnings)
    {
        var confidence = 0.45m;
        if (!string.IsNullOrWhiteSpace(author)) confidence += 0.20m;
        if (!string.IsNullOrWhiteSpace(title)) confidence += 0.15m;
        if (candidates.Count == 1 || candidates.All(candidate => candidate.SequenceWasInferred)) confidence += 0.15m;
        if (warnings.Count == 0) confidence += 0.05m;
        return Math.Min(confidence, 1.00m);
    }

    private static string NormalizeWhitespace(string value) =>
        WhitespaceRegex().Replace(value.Replace('_', ' '), " ").Trim();

    private static string NormalizeKey(string value) =>
        NonAlphaNumericRegex().Replace(value.ToLowerInvariant(), string.Empty);

    [GeneratedRegex(@"(?i)(?:^|[\s._-])(?:part|pt|cd|disc|disk)[\s._-]*(\d{1,4})(?:$|[\s._-])")]
    private static partial Regex PartNumberRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphaNumericRegex();

    private sealed record ParsedCandidate(
        MediaItem Item,
        string GroupKey,
        string? Author,
        string Title,
        int Sequence,
        bool SequenceWasInferred,
        LocalMediaMetadata Metadata);
}
