using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Archivio.Application.Abstractions;

public sealed record OnlineMetadataSuggestion(
    string ProviderName,
    string ProviderItemId,
    string Title,
    IReadOnlyList<string> Authors,
    int? FirstPublishedYear,
    IReadOnlyList<string> Subjects,
    string? CoverUrl,
    string SourceUrl,
    decimal MatchConfidence,
    string MatchReason,
    DateTime RetrievedAtUtc)
{
    public string AuthorDisplay => Authors.Count == 0 ? "Unknown author" : string.Join(", ", Authors);
    public string YearDisplay => FirstPublishedYear is null ? "Year unavailable" : FirstPublishedYear.Value.ToString();
    public string SubjectDisplay => Subjects.Count == 0 ? "Subjects unavailable" : string.Join(", ", Subjects.Take(5));
    public string ProvenanceDisplay => $"Suggested by {ProviderName} · {MatchConfidence:P0} match";
}

public sealed record OnlineMetadataQuery(
    string CandidateKey,
    string Title,
    string? Author);

public sealed record OnlineBookSearchResult(
    string ProviderName,
    string ProviderItemId,
    string Title,
    IReadOnlyList<string> Authors,
    int? FirstPublishedYear,
    IReadOnlyList<string> Subjects,
    string? CoverUrl,
    string SourceUrl);

public sealed record OnlineMetadataCacheEntry(
    string CandidateKey,
    string InputSignature,
    DateTime RetrievedAtUtc,
    OnlineMetadataSuggestion? Suggestion);

public static partial class OnlineMetadataIdentity
{
    public static string CreateInputSignature(string title, string? author)
    {
        const string lookupAlgorithmVersion = "online-lookup-v2";
        var value = $"{lookupAlgorithmVersion}|{Normalize(title)}|{Normalize(author ?? string.Empty)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string Normalize(string value) =>
        WhitespaceRegex().Replace(NonAlphaNumericRegex().Replace(value.ToLowerInvariant(), " "), " ").Trim();

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+")]
    private static partial Regex NonAlphaNumericRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
