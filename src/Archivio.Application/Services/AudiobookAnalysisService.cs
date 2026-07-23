using System.Globalization;
using System.Text.RegularExpressions;
using Archivio.Application.Abstractions;
using Archivio.Domain;

namespace Archivio.Application.Services;

public sealed partial class AudiobookAnalysisService : IAudiobookAnalysisService
{
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

    private static ParsedCandidate Parse(MediaItem item)
    {
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

        return new ParsedCandidate(item, groupKey, author, title, sequence, partMatch.Success);
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

        var confidence = CalculateConfidence(first.Author, first.Title, ordered, warnings);
        var parts = ordered
            .Select((candidate, index) => new AudiobookCandidatePart(
                candidate.Item,
                candidate.Sequence > 0 ? candidate.Sequence : index + 1,
                candidate.SequenceWasInferred))
            .ToList();
        var displayName = string.IsNullOrWhiteSpace(first.Author)
            ? first.Title
            : $"{first.Author} - {first.Title}";

        return new AudiobookCandidateGroup(displayName, first.Author, first.Title, parts, confidence, warnings);
    }

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
        bool SequenceWasInferred);
}
