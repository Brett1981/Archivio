using System.Text.RegularExpressions;
using Archivio.Application.Abstractions;

namespace Archivio.Application.Services;

public sealed partial class OnlineMetadataLookupService(
    IBookMetadataProvider metadataProvider,
    IAudiobookAnalysisStore analysisStore) : IOnlineMetadataLookupService
{
    private const int QueryBatchSize = 8;
    private static readonly TimeSpan MatchCacheLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan NoMatchCacheLifetime = TimeSpan.FromDays(7);

    public async Task<IReadOnlyList<AudiobookCandidateGroup>> EnrichCandidatesAsync(
        Guid librarySourceId,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        IProgress<OnlineMetadataLookupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var eligible = candidates.Where(NeedsOnlineLookup).ToList();
        var cache = await analysisStore.LoadOnlineMetadataCacheAsync(librarySourceId, cancellationToken);
        var suggestions = new Dictionary<string, OnlineMetadataSuggestion?>();
        var inputSignatures = new Dictionary<string, string>(StringComparer.Ordinal);
        var uncachedQueries = new List<OnlineMetadataQuery>();
        var now = DateTime.UtcNow;

        foreach (var candidate in eligible)
        {
            var signature = OnlineMetadataIdentity.CreateInputSignature(candidate.Title, candidate.Author);
            inputSignatures[candidate.CandidateKey] = signature;
            if (cache.TryGetValue(candidate.CandidateKey, out var cached) &&
                cached.InputSignature == signature &&
                IsCacheCurrent(cached, now))
            {
                suggestions[candidate.CandidateKey] = cached.Suggestion;
            }
            else
            {
                uncachedQueries.Add(new OnlineMetadataQuery(
                    candidate.CandidateKey,
                    PrepareLookupTitle(candidate.Title, candidate.Author),
                    candidate.Author));
            }
        }

        var processedCount = eligible.Count - uncachedQueries.Count;
        var matchCount = suggestions.Values.Count(value => value is not null);
        progress?.Report(new OnlineMetadataLookupProgress(
            processedCount,
            eligible.Count,
            matchCount,
            null));

        var queryGroups = uncachedQueries
            .GroupBy(query => $"{Normalize(query.Title)}|{Normalize(query.Author ?? string.Empty)}")
            .ToList();
        foreach (var groupBatch in queryGroups.Chunk(QueryBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var representativeQueries = groupBatch.Select(group => group.First()).ToList();
            var results = await metadataProvider.SearchAsync(representativeQueries, cancellationToken);
            var cacheEntries = new List<OnlineMetadataCacheEntry>();

            foreach (var queryGroup in groupBatch)
            {
                foreach (var query in queryGroup)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var suggestion = SelectSuggestion(query, results, now);
                    suggestions[query.CandidateKey] = suggestion;
                    if (suggestion is not null)
                    {
                        matchCount++;
                    }

                    processedCount++;
                    cacheEntries.Add(new OnlineMetadataCacheEntry(
                        query.CandidateKey,
                        inputSignatures[query.CandidateKey],
                        now,
                        suggestion));
                    progress?.Report(new OnlineMetadataLookupProgress(
                        processedCount,
                        eligible.Count,
                        matchCount,
                        query.Title));
                }
            }

            await analysisStore.SaveOnlineMetadataCacheAsync(
                librarySourceId,
                cacheEntries,
                cancellationToken);
        }

        await analysisStore.PruneOnlineMetadataCacheAsync(
            librarySourceId,
            eligible.Select(candidate => candidate.CandidateKey).ToList(),
            cancellationToken);

        return candidates
            .Select(candidate => suggestions.TryGetValue(candidate.CandidateKey, out var suggestion)
                ? candidate with { OnlineSuggestion = suggestion }
                : candidate)
            .ToList();
    }

    internal static OnlineMetadataSuggestion? SelectSuggestion(
        OnlineMetadataQuery query,
        IReadOnlyList<OnlineBookSearchResult> results,
        DateTime retrievedAtUtc)
    {
        var scored = results
            .GroupBy(result =>
                $"{Normalize(result.Title)}|" +
                string.Join('|', result.Authors.Select(Normalize).OrderBy(author => author)))
            .Select(group => Score(query, group.First()))
            .OrderByDescending(match => match.Score)
            .ToList();
        if (scored.Count == 0)
        {
            return null;
        }

        var best = scored[0];
        var runnerUpScore = scored.Count > 1 ? scored[1].Score : 0m;
        var hasAuthor = !string.IsNullOrWhiteSpace(query.Author);
        var minimumScore = hasAuthor ? 0.78m : 0.92m;
        var minimumTitleScore = hasAuthor ? 0.65m : 0.92m;
        if (best.Score < minimumScore ||
            best.TitleScore < minimumTitleScore ||
            best.Score - runnerUpScore < 0.05m)
        {
            return null;
        }

        return new OnlineMetadataSuggestion(
            best.Result.ProviderName,
            best.Result.ProviderItemId,
            best.Result.Title,
            best.Result.Authors,
            best.Result.FirstPublishedYear,
            best.Result.Subjects,
            best.Result.CoverUrl,
            best.Result.SourceUrl,
            best.Score,
            hasAuthor
                ? $"Title {best.TitleScore:P0} · author {best.AuthorScore:P0}"
                : $"Title {best.TitleScore:P0}; author was unavailable locally",
            retrievedAtUtc);
    }

    private static bool NeedsOnlineLookup(AudiobookCandidateGroup candidate) =>
        candidate.NeedsReview || string.IsNullOrWhiteSpace(candidate.Author);

    internal static string PrepareLookupTitle(string title, string? author)
    {
        var result = title.Trim();
        if (!string.IsNullOrWhiteSpace(author) &&
            result.StartsWith(author, StringComparison.OrdinalIgnoreCase))
        {
            var remainder = result[author.Length..];
            if (AuthorSeparatorRegex().IsMatch(remainder))
            {
                result = AuthorSeparatorRegex().Replace(remainder, string.Empty, 1);
            }
        }

        result = SizeMarkerRegex().Replace(result, string.Empty);
        result = EncodingDurationRegex().Replace(result, string.Empty);
        result = TrailingPartMarkerRegex().Replace(result, string.Empty);
        result = TrailingYearRegex().Replace(result, string.Empty);
        result = TrailingQualifierRegex().Replace(result, string.Empty);
        result = LeadingYearRegex().Replace(result, string.Empty);
        result = KnownSeriesPrefixRegex().Replace(result, string.Empty);
        result = LeadingOrdinalRegex().Replace(result, string.Empty);
        result = WhitespaceRegex().Replace(result, " ").Trim(' ', '-', '_', '.', '–', '—');
        return result.Length == 0 ? title.Trim() : result;
    }

    private static bool IsCacheCurrent(OnlineMetadataCacheEntry entry, DateTime now) =>
        now - entry.RetrievedAtUtc <= (entry.Suggestion is null ? NoMatchCacheLifetime : MatchCacheLifetime);

    private static ScoredResult Score(OnlineMetadataQuery query, OnlineBookSearchResult result)
    {
        var titleScore = TokenDiceCoefficient(query.Title, result.Title);
        var authorScore = string.IsNullOrWhiteSpace(query.Author)
            ? 0m
            : result.Authors.Select(author => TokenDiceCoefficient(query.Author, author)).DefaultIfEmpty(0m).Max();
        var score = string.IsNullOrWhiteSpace(query.Author)
            ? titleScore
            : (titleScore * 0.75m) + (authorScore * 0.25m);
        return new ScoredResult(result, score, titleScore, authorScore);
    }

    private static decimal TokenDiceCoefficient(string left, string right)
    {
        var leftTokens = Normalize(left).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var rightTokens = Normalize(right).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return 0m;
        }

        var intersection = leftTokens.Count(rightTokens.Contains);
        return (2m * intersection) / (leftTokens.Count + rightTokens.Count);
    }

    private static string Normalize(string value) =>
        WhitespaceRegex().Replace(NonAlphaNumericRegex().Replace(value.ToLowerInvariant(), " "), " ").Trim();

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+")]
    private static partial Regex NonAlphaNumericRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^\s*[-–—:]{1,2}\s*")]
    private static partial Regex AuthorSeparatorRegex();

    [GeneratedRegex(@"\s*\{[^{}]*\}\s*$")]
    private static partial Regex SizeMarkerRegex();

    [GeneratedRegex(@"\s+\d{2,3}k?\s+\d{1,2}(?:[.:]\d{2}){2}\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex EncodingDurationRegex();

    [GeneratedRegex(@"\s*(?:\(\s*\d+\s*(?:of|/)\s*\d+\s*\)|\d+\s*--\s*\d+|\d{1,4}\s*-\s*(?:end|\d{1,4})|(?:part|disc|cd|track)\s*\d+(?:\s*(?:of|/)\s*\d+)?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingPartMarkerRegex();

    [GeneratedRegex(@"\s+(?:18|19|20)\d{2}\s*$")]
    private static partial Regex TrailingYearRegex();

    [GeneratedRegex(@"\s*\([^()]*\)\s*$")]
    private static partial Regex TrailingQualifierRegex();

    [GeneratedRegex(@"^\s*(?:18|19|20)\d{2}\s*[-–—]\s*")]
    private static partial Regex LeadingYearRegex();

    [GeneratedRegex(@"^\s*(?:miss\s+marple|hercule\s+poirot|bbc\s+radio)\s+\d+\s+", RegexOptions.IgnoreCase)]
    private static partial Regex KnownSeriesPrefixRegex();

    [GeneratedRegex(@"^\s*\d{1,3}\s*[.)_-]\s*")]
    private static partial Regex LeadingOrdinalRegex();

    private sealed record ScoredResult(
        OnlineBookSearchResult Result,
        decimal Score,
        decimal TitleScore,
        decimal AuthorScore);
}
