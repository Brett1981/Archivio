using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Archivio.Application.Abstractions;

namespace Archivio.Application.Services;

public sealed partial class AudiobookOrganisationService(
    IAudiobookOrganisationStore organisationStore) : IAudiobookOrganisationService
{
    private const string ProposalAlgorithmVersion = "audiobook-organisation-v3";
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public async Task<IReadOnlyList<AudiobookCandidateGroup>> PrepareProposalsAsync(
        Guid librarySourceId,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
        {
            await organisationStore.SaveAsync(librarySourceId, [], [], cancellationToken);
            return candidates;
        }

        var now = DateTime.UtcNow;
        var generated = GenerateProposals(candidates, now);
        var cached = await organisationStore.LoadAsync(librarySourceId, cancellationToken);
        var entriesToSave = new List<AudiobookOrganisationCacheEntry>();
        var result = new List<AudiobookCandidateGroup>(candidates.Count);

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var generatedEntry = generated[candidate.CandidateKey];
            var proposal = cached.TryGetValue(candidate.CandidateKey, out var cachedEntry) &&
                           cachedEntry.InputSignature == generatedEntry.InputSignature
                ? cachedEntry.Proposal
                : generatedEntry.Proposal;
            if (!ReferenceEquals(proposal, cachedEntry?.Proposal))
            {
                entriesToSave.Add(generatedEntry);
            }

            result.Add(candidate with { OrganisationProposal = proposal });
        }

        await organisationStore.SaveAsync(
            librarySourceId,
            entriesToSave,
            generated.Keys.ToList(),
            cancellationToken);
        return result;
    }

    private static IReadOnlyDictionary<string, AudiobookOrganisationCacheEntry> GenerateProposals(
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        DateTime generatedAtUtc)
    {
        var result = new Dictionary<string, AudiobookOrganisationCacheEntry>(StringComparer.Ordinal);
        var groups = candidates.GroupBy(CreateIdentityKey, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var relatedCandidates = group.ToList();
            var onlineSuggestion = relatedCandidates
                .Select(candidate => candidate.OnlineSuggestion)
                .Where(suggestion => suggestion is not null)
                .OrderByDescending(suggestion => suggestion!.MatchConfidence)
                .FirstOrDefault();
            var identity = ResolveCanonicalIdentity(relatedCandidates, onlineSuggestion);
            var canonicalAuthor = identity.Author;
            var canonicalTitle = identity.Title;
            var localGenres = relatedCandidates
                .SelectMany(candidate => candidate.Parts)
                .Select(part => part.Metadata.Genre.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToList();
            var genre = ClassifyGenre(onlineSuggestion?.Subjects ?? [], localGenres);
            var sourceFileCount = relatedCandidates.Sum(candidate => candidate.Parts.Count);
            var confidence = onlineSuggestion?.MatchConfidence ??
                relatedCandidates.Min(candidate => candidate.Confidence);
            var safeAuthor = SanitizePathComponent(canonicalAuthor, "Unknown Author");
            var safeTitle = SanitizePathComponent(canonicalTitle, "Unknown Title");
            var titleFolder = onlineSuggestion?.FirstPublishedYear is null
                ? safeTitle
                : $"{safeTitle} ({onlineSuggestion.FirstPublishedYear.Value})";
            var folderParts = genre.Category == "Uncategorised"
                ? new[] { safeAuthor, titleFolder }
                : new[] { SanitizePathComponent(genre.Category, "Uncategorised"), safeAuthor, titleFolder };
            var suggestedFolder = Path.Combine(folderParts);
            var firstPart = relatedCandidates.SelectMany(candidate => candidate.Parts).First();
            var extension = firstPart.MediaItem.Extension;
            var fileNamePattern = sourceFileCount == 1
                ? SanitizeFileName($"{safeAuthor} - {safeTitle}{extension}")
                : $"001 - {safeTitle}{{original extension}}";
            var action = SelectAction(
                relatedCandidates,
                suggestedFolder,
                fileNamePattern,
                sourceFileCount);
            var meaningfulTitle = IsMeaningfulTitle(canonicalTitle) &&
                                  relatedCandidates.All(candidate => IsMeaningfulTitle(candidate.Title));
            var hasAuthoritativeLocalIdentity = relatedCandidates.All(candidate =>
                IsAuthoritativeLocalSource(candidate.AuthorSource) &&
                IsAuthoritativeLocalSource(candidate.TitleSource));
            var hasNoReviewFlags = relatedCandidates.All(candidate => !candidate.NeedsReview);
            var ready = !string.Equals(canonicalAuthor, "Unknown Author", StringComparison.OrdinalIgnoreCase) &&
                        meaningfulTitle &&
                        genre.Category != "Uncategorised" &&
                        hasNoReviewFlags &&
                        !identity.RequiresReview &&
                        (onlineSuggestion is not null
                            ? confidence >= 0.90m
                            : confidence >= 0.95m && hasAuthoritativeLocalIdentity);
            var reasons = BuildReasons(
                relatedCandidates.Count,
                sourceFileCount,
                onlineSuggestion,
                identity.DerivationReason,
                genre.Reason);
            var warnings = BuildWarnings(
                relatedCandidates,
                canonicalAuthor,
                meaningfulTitle,
                genre.Category,
                onlineSuggestion is null && !hasAuthoritativeLocalIdentity,
                identity.Warning,
                ready);
            var planKey = CreateHash(group.Key);
            var groupCandidateKeys = relatedCandidates
                .Select(candidate => candidate.CandidateKey)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();

            for (var index = 0; index < relatedCandidates.Count; index++)
            {
                var candidate = relatedCandidates[index];
                var proposal = new AudiobookOrganisationProposal(
                    planKey,
                    canonicalAuthor,
                    canonicalTitle,
                    onlineSuggestion?.FirstPublishedYear,
                    genre.Category,
                    suggestedFolder,
                    fileNamePattern,
                    action,
                    relatedCandidates.Count,
                    sourceFileCount,
                    IsPrimaryCandidate: index == 0,
                    UsesOnlineMetadata: onlineSuggestion is not null,
                    confidence,
                    ready,
                    FutureCombineCandidate: sourceFileCount > 1,
                    reasons,
                    warnings,
                    generatedAtUtc);
                var signature = CreateInputSignature(candidate, groupCandidateKeys, proposal);
                result[candidate.CandidateKey] = new AudiobookOrganisationCacheEntry(
                    candidate.CandidateKey,
                    signature,
                    generatedAtUtc,
                    proposal);
            }
        }

        return result;
    }

    private static string CreateIdentityKey(AudiobookCandidateGroup candidate)
    {
        if (candidate.OnlineSuggestion is not null)
        {
            return $"online|{candidate.OnlineSuggestion.ProviderName}|{candidate.OnlineSuggestion.ProviderItemId}";
        }

        var identity = ResolveCanonicalIdentity([candidate], null);
        return $"local|{Normalize(identity.Author)}|{Normalize(identity.Title)}";
    }

    private static ResolvedBookIdentity ResolveCanonicalIdentity(
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        OnlineMetadataSuggestion? suggestion)
    {
        if (suggestion is not null)
        {
            var author = suggestion.Authors.Count > 0
                ? string.Join(" & ", suggestion.Authors.Take(2))
                : SelectCanonicalAuthor(candidates, null);
            return new ResolvedBookIdentity(author, suggestion.Title, false, null, null);
        }

        var folderIdentity = candidates
            .Select(TryResolveNumberedBookLabelFromFolder)
            .FirstOrDefault(identity => identity is not null);
        if (folderIdentity is not null)
        {
            return folderIdentity;
        }

        return new ResolvedBookIdentity(
            SelectCanonicalAuthor(candidates, null),
            SelectCanonicalTitle(candidates, null),
            false,
            null,
            null);
    }

    private static ResolvedBookIdentity? TryResolveNumberedBookLabelFromFolder(
        AudiobookCandidateGroup candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.Author))
        {
            return null;
        }

        var numberedLabelMatch = NumberedBookLabelRegex().Match(candidate.Author);
        if (!numberedLabelMatch.Success)
        {
            return null;
        }

        var numberedBookTitle = numberedLabelMatch.Groups["title"].Value.Trim();
        foreach (var part in candidate.Parts)
        {
            var directory = Path.GetDirectoryName(part.MediaItem.RelativePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var folders = directory.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var folder in folders.Reverse())
            {
                var folderMatch = AuthorTitleFolderRegex().Match(folder);
                if (!folderMatch.Success)
                {
                    continue;
                }

                var folderAuthor = folderMatch.Groups["author"].Value.Trim();
                var folderTitle = OnlineMetadataLookupService.PrepareLookupTitle(
                    folderMatch.Groups["title"].Value,
                    folderAuthor);
                if (!string.Equals(
                    Normalize(numberedBookTitle),
                    Normalize(folderTitle),
                    StringComparison.Ordinal))
                {
                    continue;
                }

                return new ResolvedBookIdentity(
                    folderAuthor,
                    folderTitle,
                    true,
                    "Canonical identity used the matching author/book folder because the embedded author field contains a numbered book label.",
                    "The embedded author/title fields appear to contain a numbered book label and chapter title; confirm the folder-derived identity before unattended handling.");
            }
        }

        return null;
    }

    private static string SelectCanonicalAuthor(
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        OnlineMetadataSuggestion? suggestion)
    {
        if (suggestion?.Authors.Count > 0)
        {
            return string.Join(" & ", suggestion.Authors.Take(2));
        }

        return candidates
            .Select(candidate => candidate.Author)
            .FirstOrDefault(author => !string.IsNullOrWhiteSpace(author)) ?? "Unknown Author";
    }

    private static string SelectCanonicalTitle(
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        OnlineMetadataSuggestion? suggestion) =>
        suggestion?.Title ?? OnlineMetadataLookupService.PrepareLookupTitle(
            candidates[0].Title,
            candidates[0].Author);

    private static AudiobookOrganisationAction SelectAction(
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        string suggestedFolder,
        string suggestedFileName,
        int sourceFileCount)
    {
        if (candidates.Count > 1)
        {
            return AudiobookOrganisationAction.ConsolidateCandidates;
        }

        if (sourceFileCount > 1)
        {
            return AudiobookOrganisationAction.OrganiseMultipart;
        }

        var current = candidates[0].Parts[0].MediaItem.RelativePath;
        var proposed = Path.Combine(suggestedFolder, suggestedFileName);
        if (string.Equals(current, proposed, StringComparison.OrdinalIgnoreCase))
        {
            return AudiobookOrganisationAction.Keep;
        }

        return string.Equals(
            Path.GetDirectoryName(current),
            Path.GetDirectoryName(proposed),
            StringComparison.OrdinalIgnoreCase)
            ? AudiobookOrganisationAction.Rename
            : AudiobookOrganisationAction.MoveAndRename;
    }

    private static (string Category, string Reason) ClassifyGenre(
        IReadOnlyList<string> subjects,
        IReadOnlyList<string> localGenres)
    {
        var mappings = new (string Category, string[] Keywords)[]
        {
            ("Mystery & Thriller", ["mystery", "detective", "crime", "thriller", "suspense"]),
            ("Science Fiction", ["science fiction", "sci-fi", "scifi", "space opera", "dystopian", "time travel"]),
            ("Fantasy", ["fantasy", "magic", "epic fantasy", "urban fantasy"]),
            ("Horror", ["horror", "ghost", "occult"]),
            ("Romance", ["romance", "love stories"]),
            ("Children & Young Adult", ["juvenile", "children", "young adult"]),
            ("Biography & Memoir", ["biography", "autobiography", "memoir"]),
            ("History", ["history", "historical"]),
            ("Non-fiction", ["philosophy", "science", "economics", "politics", "religion", "self-help"]),
            ("Fiction", ["fiction", "literature"])
        };

        var subjectText = string.Join(' ', subjects).ToLowerInvariant();
        foreach (var mapping in mappings)
        {
            var keyword = mapping.Keywords.FirstOrDefault(subjectText.Contains);
            if (keyword is not null)
            {
                return (mapping.Category, $"Genre inferred from online subject '{keyword}'.");
            }
        }

        var localMatches = localGenres
            .Select(value => new
            {
                Value = value,
                Mapping = mappings.FirstOrDefault(mapping =>
                    mapping.Keywords.Any(keyword => value.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            })
            .Where(match => match.Mapping.Category is not null)
            .ToList();
        var categories = localMatches
            .Select(match => match.Mapping.Category)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (categories.Count == 1)
        {
            return (
                categories[0],
                $"Genre inferred from embedded tag '{localMatches[0].Value}'.");
        }

        return categories.Count > 1
            ? ("Uncategorised", "Embedded genre tags map to conflicting categories.")
            : ("Uncategorised", "No reliable online subject or embedded genre tag was available.");
    }

    private static IReadOnlyList<string> BuildReasons(
        int candidateCount,
        int sourceFileCount,
        OnlineMetadataSuggestion? suggestion,
        string? identityDerivationReason,
        string genreReason)
    {
        var reasons = new List<string>
        {
            identityDerivationReason ?? (suggestion is null
                ? "Canonical identity was derived from local metadata and filenames."
                : $"Canonical identity came from {suggestion.ProviderName} at {suggestion.MatchConfidence:P0} confidence."),
            candidateCount == 1
                ? $"The plan covers {sourceFileCount:N0} source file{(sourceFileCount == 1 ? string.Empty : "s")}."
                : $"{candidateCount:N0} candidates share one identity and are consolidated into one plan.",
            genreReason,
            "This proposal is read-only; no media file has been changed."
        };
        return reasons;
    }

    private static IReadOnlyList<string> BuildWarnings(
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        string canonicalAuthor,
        bool meaningfulTitle,
        string genreCategory,
        bool lacksAuthoritativeLocalIdentity,
        string? identityWarning,
        bool ready)
    {
        var warnings = new List<string>();
        if (string.Equals(canonicalAuthor, "Unknown Author", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("A canonical author is still required.");
        }

        if (!meaningfulTitle)
        {
            warnings.Add("The inferred title appears to be a track or disc label.");
        }

        if (genreCategory == "Uncategorised")
        {
            warnings.Add("No genre folder has been proposed.");
        }

        if (candidates.Any(candidate => candidate.NeedsReview) &&
            candidates.All(candidate => candidate.OnlineSuggestion is null))
        {
            warnings.Add("Local analysis warnings remain and no online identity was available.");
        }

        if (lacksAuthoritativeLocalIdentity)
        {
            warnings.Add("A filename-derived identity requires review before unattended handling.");
        }

        if (!string.IsNullOrWhiteSpace(identityWarning))
        {
            warnings.Add(identityWarning);
        }

        if (!ready)
        {
            warnings.Add("This plan is not eligible for future unattended handling yet.");
        }

        return warnings;
    }

    private static bool IsAuthoritativeLocalSource(MetadataValueSource source) =>
        source is MetadataValueSource.EmbeddedTag or MetadataValueSource.FolderStructure;

    private static bool IsMeaningfulTitle(string title)
    {
        var normalized = Normalize(title);
        return normalized.Length >= 3 &&
               !GenericTrackTitleRegex().IsMatch(normalized);
    }

    internal static string SanitizePathComponent(string value, string fallback)
    {
        var sanitized = InvalidPathCharacterRegex().Replace(value, " ");
        sanitized = WhitespaceRegex().Replace(sanitized, " ").Trim(' ', '.');
        if (sanitized.Length > 100)
        {
            sanitized = sanitized[..100].TrimEnd(' ', '.');
        }

        if (sanitized.Length == 0)
        {
            sanitized = fallback;
        }

        return ReservedWindowsNames.Contains(sanitized) ? $"_{sanitized}" : sanitized;
    }

    private static string SanitizeFileName(string value)
    {
        var extension = Path.GetExtension(value);
        var stem = Path.GetFileNameWithoutExtension(value);
        return SanitizePathComponent(stem, "Unknown Title") + extension;
    }

    private static string CreateInputSignature(
        AudiobookCandidateGroup candidate,
        IReadOnlyList<string> groupCandidateKeys,
        AudiobookOrganisationProposal proposal)
    {
        var value = string.Join(
            '|',
            ProposalAlgorithmVersion,
            candidate.CandidateKey,
            string.Join(',', groupCandidateKeys),
            proposal.PlanKey,
            Normalize(proposal.CanonicalAuthor),
            Normalize(proposal.CanonicalTitle),
            proposal.FirstPublishedYear,
            proposal.GenreCategory,
            proposal.SuggestedRelativeFolder,
            proposal.SuggestedFileNamePattern,
            proposal.RecommendedAction,
            proposal.RelatedCandidateCount,
            proposal.SourceFileCount,
            proposal.ReadyForAutomaticHandling);
        return CreateHash(value);
    }

    private static string CreateHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Normalize(string value) =>
        WhitespaceRegex().Replace(NonAlphaNumericRegex().Replace(value.ToLowerInvariant(), " "), " ").Trim();

    private sealed record ResolvedBookIdentity(
        string Author,
        string Title,
        bool RequiresReview,
        string? DerivationReason,
        string? Warning);

    [GeneratedRegex("""[<>:"/\\|?*\x00-\x1F]+""")]
    private static partial Regex InvalidPathCharacterRegex();

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+")]
    private static partial Regex NonAlphaNumericRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^(?:audio\s*track|track|disc|disk|cd)\s*\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericTrackTitleRegex();

    [GeneratedRegex(@"^\s*\d{1,3}\s+(?<title>.+?)\s*$")]
    private static partial Regex NumberedBookLabelRegex();

    [GeneratedRegex(@"^\s*(?<author>.+?)\s+[-–—]\s+(?<title>.+?)\s*$")]
    private static partial Regex AuthorTitleFolderRegex();
}
