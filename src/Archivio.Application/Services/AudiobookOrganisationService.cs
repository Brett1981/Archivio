using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Archivio.Application.Abstractions;

namespace Archivio.Application.Services;

public sealed partial class AudiobookOrganisationService(
    IAudiobookOrganisationStore organisationStore,
    IAudiobookReviewOverrideStore? reviewOverrideStore = null) : IAudiobookOrganisationService
{
    private const string ProposalAlgorithmVersion = "audiobook-organisation-v11";
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
        var overrides = reviewOverrideStore is null
            ? EmptyReviewOverrides
            : await reviewOverrideStore.LoadAsync(librarySourceId, cancellationToken);
        var planningCandidates = ExpandSeparateBookCollections(candidates, overrides, out var collectionContexts);
        var generated = GenerateProposals(planningCandidates, now, overrides, collectionContexts);
        var cached = await organisationStore.LoadAsync(librarySourceId, cancellationToken);
        var entriesToSave = new List<AudiobookOrganisationCacheEntry>();
        var result = new List<AudiobookCandidateGroup>(planningCandidates.Count);

        foreach (var candidate in planningCandidates)
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

    public async Task<IReadOnlyList<AudiobookCandidateGroup>> SetGenreOverrideAsync(
        Guid librarySourceId,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        string planKey,
        string? genreCategory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(planKey);
        if (reviewOverrideStore is null)
        {
            throw new InvalidOperationException("Review corrections are not available.");
        }

        if (genreCategory is not null && !AudiobookGenreCategories.Contains(genreCategory))
        {
            throw new ArgumentOutOfRangeException(nameof(genreCategory), "Choose a supported audiobook genre.");
        }

        EnsurePlanExists(candidates, planKey);

        await reviewOverrideStore.SetGenreAsync(
            librarySourceId,
            planKey,
            genreCategory,
            cancellationToken);
        return await PrepareProposalsAsync(librarySourceId, candidates, cancellationToken);
    }

    public async Task<IReadOnlyList<AudiobookCandidateGroup>> SetIdentityOverrideAsync(
        Guid librarySourceId,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        string planKey,
        string? canonicalAuthor,
        string? canonicalTitle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(planKey);
        if (reviewOverrideStore is null)
        {
            throw new InvalidOperationException("Review corrections are not available.");
        }

        var isRestore = canonicalAuthor is null && canonicalTitle is null;
        if (!isRestore)
        {
            canonicalAuthor = canonicalAuthor?.Trim();
            canonicalTitle = canonicalTitle?.Trim();
            if (string.IsNullOrWhiteSpace(canonicalAuthor) ||
                string.Equals(canonicalAuthor, "Unknown Author", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Enter the audiobook author.", nameof(canonicalAuthor));
            }

            if (canonicalAuthor.Length > 200)
            {
                throw new ArgumentOutOfRangeException(nameof(canonicalAuthor), "The author must be 200 characters or fewer.");
            }

            if (string.IsNullOrWhiteSpace(canonicalTitle) || !IsMeaningfulTitle(canonicalTitle))
            {
                throw new ArgumentException("Enter the complete audiobook title, not a chapter or track label.", nameof(canonicalTitle));
            }

            if (canonicalTitle.Length > 300)
            {
                throw new ArgumentOutOfRangeException(nameof(canonicalTitle), "The title must be 300 characters or fewer.");
            }
        }

        EnsurePlanExists(candidates, planKey);
        await reviewOverrideStore.SetIdentityAsync(
            librarySourceId,
            planKey,
            canonicalAuthor,
            canonicalTitle,
            cancellationToken);
        return await PrepareProposalsAsync(librarySourceId, candidates, cancellationToken);
    }

    public async Task<IReadOnlyList<AudiobookCandidateGroup>> SetCollectionOverrideAsync(
        Guid librarySourceId,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        string planKey,
        AudiobookCollectionHandling collectionHandling,
        string? canonicalAuthor,
        string? seriesName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(planKey);
        if (reviewOverrideStore is null)
        {
            throw new InvalidOperationException("Review corrections are not available.");
        }

        canonicalAuthor = NormalizeOptionalReviewValue(canonicalAuthor, 200, "author");
        seriesName = NormalizeOptionalReviewValue(seriesName, 200, "series name");
        EnsurePlanExists(candidates, planKey);
        await reviewOverrideStore.SetCollectionAsync(
            librarySourceId,
            planKey,
            collectionHandling,
            canonicalAuthor,
            seriesName,
            cancellationToken);
        return await PrepareProposalsAsync(librarySourceId, candidates, cancellationToken);
    }

    public async Task<IReadOnlyList<AudiobookCandidateGroup>> SetSeriesOverrideAsync(
        Guid librarySourceId,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        string planKey,
        string? seriesName,
        int? seriesPosition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(planKey);
        if (reviewOverrideStore is null)
        {
            throw new InvalidOperationException("Review corrections are not available.");
        }

        seriesName = NormalizeOptionalReviewValue(seriesName, 200, "series name");
        if (seriesPosition is <= 0 or > 10000)
        {
            throw new ArgumentOutOfRangeException(nameof(seriesPosition), "The series position must be between 1 and 10,000.");
        }

        EnsurePlanExists(candidates, planKey);
        await reviewOverrideStore.SetSeriesAsync(
            librarySourceId,
            planKey,
            seriesName,
            seriesName is null ? null : seriesPosition,
            cancellationToken);
        return await PrepareProposalsAsync(librarySourceId, candidates, cancellationToken);
    }

    private static void EnsurePlanExists(
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        string planKey)
    {
        if (!candidates.Any(candidate =>
                candidate.IsPrimaryOrganisationPlan &&
                string.Equals(candidate.OrganisationProposal?.PlanKey, planKey, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The selected audiobook plan is no longer available.");
        }
    }

    private static IReadOnlyList<AudiobookCandidateGroup> ExpandSeparateBookCollections(
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        IReadOnlyDictionary<string, AudiobookReviewOverrideEntry> reviewOverrides,
        out IReadOnlyDictionary<string, CollectionContext> collectionContexts)
    {
        var expanded = new List<AudiobookCandidateGroup>(candidates.Count);
        var contexts = new Dictionary<string, CollectionContext>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var proposal = candidate.OrganisationProposal;
            var possiblePlanKeys = new[]
            {
                proposal?.CollectionPlanKey,
                proposal?.PlanKey,
                CreateHash(CreateIdentityKey(candidate))
            };
            var collectionEntry = possiblePlanKeys
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => reviewOverrides.GetValueOrDefault(key!))
                .FirstOrDefault(entry =>
                    entry?.CollectionHandling == AudiobookCollectionHandling.SeparateBooks);
            var isSeparate = collectionEntry is not null ||
                             proposal?.CollectionHandling == AudiobookCollectionHandling.SeparateBooks;
            if (!isSeparate)
            {
                expanded.Add(candidate);
                continue;
            }

            var collectionPlanKey = collectionEntry?.PlanKey ??
                                    proposal?.CollectionPlanKey ??
                                    proposal!.PlanKey;
            var effectiveEntry = collectionEntry ?? new AudiobookReviewOverrideEntry(
                collectionPlanKey,
                proposal?.UsesManualAuthor == true ? proposal.CanonicalAuthor : null,
                null,
                proposal?.UsesManualGenre == true ? proposal.GenreCategory : null,
                proposal?.GeneratedAtUtc ?? DateTime.UtcNow,
                AudiobookCollectionHandling.SeparateBooks,
                proposal?.SeriesName);

            var separateCandidates = candidate.Parts.Count <= 1
                ? [candidate]
                : candidate.Parts.Select(part => CreateSeparateBookCandidate(candidate, part, effectiveEntry)).ToList();
            foreach (var separateCandidate in separateCandidates)
            {
                expanded.Add(separateCandidate);
                contexts[separateCandidate.CandidateKey] = new CollectionContext(collectionPlanKey, effectiveEntry);
            }
        }

        collectionContexts = contexts;
        return expanded;
    }

    private static AudiobookCandidateGroup CreateSeparateBookCandidate(
        AudiobookCandidateGroup collection,
        AudiobookCandidatePart part,
        AudiobookReviewOverrideEntry reviewOverride)
    {
        var embeddedTitle = part.Metadata.Title.HasValue
            ? part.Metadata.Title.Value!.Trim()
            : Path.GetFileNameWithoutExtension(part.MediaItem.FileName);
        var title = RemoveSeriesReference(embeddedTitle, reviewOverride.SeriesName);
        title = OnlineMetadataLookupService.PrepareLookupTitle(title, part.Metadata.Author.Value);
        var author = part.Metadata.Author.HasValue
            ? part.Metadata.Author.Value!.Trim()
            : reviewOverride.CanonicalAuthor ?? collection.Author;
        var authorSource = part.Metadata.Author.HasValue
            ? part.Metadata.Author.Source
            : string.IsNullOrWhiteSpace(reviewOverride.CanonicalAuthor)
                ? collection.AuthorSource
                : MetadataValueSource.Inferred;
        var titleSource = part.Metadata.Title.HasValue
            ? part.Metadata.Title.Source
            : MetadataValueSource.FileName;
        var confidence = authorSource == MetadataValueSource.EmbeddedTag &&
                         titleSource == MetadataValueSource.EmbeddedTag
            ? Math.Max(collection.Confidence, 0.95m)
            : collection.Confidence;
        var warnings = part.Metadata.Warnings
            .Select(warning => $"{part.MediaItem.FileName}: {warning}")
            .ToList();

        return new AudiobookCandidateGroup(
            string.IsNullOrWhiteSpace(author) ? title : $"{author} - {title}",
            author,
            title,
            authorSource,
            titleSource,
            true,
            [part with { Sequence = 1 }],
            confidence,
            warnings);
    }

    private static string RemoveSeriesReference(string title, string? seriesName)
    {
        if (string.IsNullOrWhiteSpace(seriesName))
        {
            return title;
        }

        var series = Regex.Escape(seriesName.Trim());
        var pattern = $@"\s*[\(\[]?\s*{series}(?:\s+Series)?\s*[,;:\-–—]?\s*Book\s*[#:]?\s*\d+\s*[\)\]]?\s*$";
        var cleaned = Regex.Replace(title, pattern, string.Empty, RegexOptions.IgnoreCase).Trim(' ', '-', '–', '—', ':', ',', '(', ')');
        return cleaned.Length == 0 ? title : cleaned;
    }

    private static (string? Name, int? Position) InferSeriesMetadata(
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        string canonicalTitle)
    {
        foreach (var title in candidates
                     .SelectMany(candidate => candidate.Parts)
                     .Select(part => part.Metadata.Title.Value)
                     .Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var sourceTitle = title!.Trim();
            var remainder = sourceTitle.StartsWith(canonicalTitle, StringComparison.OrdinalIgnoreCase)
                ? sourceTitle[canonicalTitle.Length..].Trim(' ', '-', '–', '—', ':', ',', '(', ')')
                : sourceTitle;
            var match = SeriesBookSuffixRegex().Match(remainder);
            if (!match.Success)
            {
                continue;
            }

            var name = match.Groups["series"].Value.Trim(' ', '-', '–', '—', ':', ',', '(', ')');
            if (name.Length is 0 or > 200 ||
                !int.TryParse(match.Groups["position"].Value, out var position))
            {
                continue;
            }

            return (name, position);
        }

        return (null, null);
    }

    private static string? NormalizeOptionalReviewValue(string? value, int maximumLength, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"The {label} must be {maximumLength} characters or fewer.");
        }

        return normalized;
    }

    private static IReadOnlyDictionary<string, AudiobookOrganisationCacheEntry> GenerateProposals(
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        DateTime generatedAtUtc,
        IReadOnlyDictionary<string, AudiobookReviewOverrideEntry> reviewOverrides,
        IReadOnlyDictionary<string, CollectionContext> collectionContexts)
    {
        var result = new Dictionary<string, AudiobookOrganisationCacheEntry>(StringComparer.Ordinal);
        var groups = candidates.GroupBy(
            candidate => collectionContexts.ContainsKey(candidate.CandidateKey)
                ? $"separate|{candidate.CandidateKey}"
                : CreateIdentityKey(candidate),
            StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var relatedCandidates = group.ToList();
            var organisedPathIdentity = TryResolveOrganisedLibraryIdentity(relatedCandidates);
            var retainedReviewPlanKeys = relatedCandidates
                .Select(candidate => candidate.OrganisationProposal)
                .Where(proposal => proposal is
                {
                    UsesManualGenre: true
                } or
                {
                    UsesManualAuthor: true
                } or
                {
                    UsesManualTitle: true
                })
                .Select(proposal => proposal!.PlanKey)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var collectionContext = relatedCandidates
                .Select(candidate => collectionContexts.GetValueOrDefault(candidate.CandidateKey))
                .FirstOrDefault(context => context is not null);
            var planKey = collectionContext is not null
                ? CreateHash($"separate|{collectionContext.PlanKey}|{relatedCandidates[0].CandidateKey}")
                : retainedReviewPlanKeys.Count == 1
                    ? retainedReviewPlanKeys[0]
                    : CreateHash(group.Key);
            var onlineSuggestion = relatedCandidates
                .Select(candidate => candidate.OnlineSuggestion)
                .Where(suggestion => suggestion is not null)
                .OrderByDescending(suggestion => suggestion!.MatchConfidence)
                .FirstOrDefault();
            var effectiveOnlineSuggestion = organisedPathIdentity is null ? onlineSuggestion : null;
            var identity = organisedPathIdentity?.Identity ??
                           ResolveCanonicalIdentity(relatedCandidates, effectiveOnlineSuggestion);
            reviewOverrides.TryGetValue(planKey, out var directReviewOverride);
            var inheritedReviewOverride = collectionContext?.ReviewOverride;
            var manualAuthorValue = directReviewOverride?.CanonicalAuthor ?? inheritedReviewOverride?.CanonicalAuthor;
            var manualAuthor = IsValidReviewValue(manualAuthorValue, 200)
                ? manualAuthorValue!.Trim()
                : null;
            var manualTitle = IsValidReviewValue(directReviewOverride?.CanonicalTitle, 300) &&
                              IsMeaningfulTitle(directReviewOverride!.CanonicalTitle!)
                ? directReviewOverride.CanonicalTitle!.Trim()
                : null;
            var hasManualAuthor = manualAuthor is not null;
            var hasManualTitle = manualTitle is not null;
            var hasManualIdentity = hasManualAuthor || hasManualTitle;
            var canonicalAuthor = manualAuthor ?? identity.Author;
            var canonicalTitle = manualTitle ?? identity.Title;
            var localGenres = relatedCandidates
                .SelectMany(candidate => candidate.Parts)
                .Select(part => part.Metadata.Genre.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToList();
            var inferredGenre = organisedPathIdentity is null
                ? ClassifyGenre(effectiveOnlineSuggestion?.Subjects ?? [], localGenres)
                : (
                    Category: organisedPathIdentity.GenreCategory,
                    Reason: "Genre came from the existing canonical library folder.");
            var manualGenreValue = directReviewOverride?.GenreCategory ?? inheritedReviewOverride?.GenreCategory;
            var manualGenre = manualGenreValue is not null &&
                              AudiobookGenreCategories.Contains(manualGenreValue)
                ? manualGenreValue
                : null;
            var genre = manualGenre is null
                ? inferredGenre
                : (Category: manualGenre, Reason: $"Genre confirmed by you as '{manualGenre}'.");
            var sourceFileCount = relatedCandidates.Sum(candidate => candidate.Parts.Count);
            var confidence = organisedPathIdentity is not null
                ? 1m
                : effectiveOnlineSuggestion?.MatchConfidence ??
                  relatedCandidates.Min(candidate => candidate.Confidence);
            if (hasManualIdentity)
            {
                confidence = Math.Max(confidence, 0.95m);
            }
            var firstPublishedYear = hasManualTitle
                ? null
                : organisedPathIdentity?.FirstPublishedYear ??
                  effectiveOnlineSuggestion?.FirstPublishedYear;
            var inferredSeries = InferSeriesMetadata(relatedCandidates, canonicalTitle);
            var manualSeriesName = IsValidReviewValue(directReviewOverride?.SeriesName, 200)
                ? directReviewOverride!.SeriesName!.Trim()
                : null;
            var inheritedSeriesName = IsValidReviewValue(inheritedReviewOverride?.SeriesName, 200)
                ? inheritedReviewOverride!.SeriesName!.Trim()
                : null;
            var seriesName = manualSeriesName ?? inheritedSeriesName ??
                             organisedPathIdentity?.SeriesName ?? inferredSeries.Name;
            var seriesPosition = directReviewOverride?.SeriesPosition ??
                                 organisedPathIdentity?.SeriesPosition ?? inferredSeries.Position;
            var safeAuthor = SanitizePathComponent(canonicalAuthor, "Unknown Author");
            var safeTitle = SanitizePathComponent(canonicalTitle, "Unknown Title");
            var titleFolder = firstPublishedYear is null
                ? safeTitle
                : $"{safeTitle} ({firstPublishedYear.Value})";
            var seriesFolder = string.IsNullOrWhiteSpace(seriesName)
                ? null
                : SanitizePathComponent(seriesName, "Series");
            if (seriesFolder is not null && seriesPosition is not null)
            {
                titleFolder = $"{seriesPosition.Value:00} - {titleFolder}";
            }

            var folderParts = new List<string>();
            if (genre.Category != "Uncategorised")
            {
                folderParts.Add(SanitizePathComponent(genre.Category, "Uncategorised"));
            }

            folderParts.Add(safeAuthor);
            if (seriesFolder is not null)
            {
                folderParts.Add(seriesFolder);
            }

            folderParts.Add(titleFolder);
            var suggestedFolder = Path.Combine(folderParts.ToArray());
            var firstPart = relatedCandidates.SelectMany(candidate => candidate.Parts).First();
            var extension = firstPart.MediaItem.Extension;
            var fileNamePattern = sourceFileCount == 1
                ? SanitizeFileName($"{safeAuthor} - {safeTitle}{extension}")
                : $"001 - {safeTitle}{{original extension}}";
            var action = organisedPathIdentity is not null
                ? AudiobookOrganisationAction.Keep
                : SelectAction(
                    relatedCandidates,
                    suggestedFolder,
                    fileNamePattern,
                    sourceFileCount);
            var meaningfulTitle = IsMeaningfulTitle(canonicalTitle) &&
                                  (hasManualIdentity || identity.ResolvesSegmentTitles ||
                                   relatedCandidates.All(candidate => IsMeaningfulTitle(candidate.Title)));
            var hasAuthoritativeLocalIdentity = organisedPathIdentity is not null ||
                                                hasManualIdentity || relatedCandidates.All(candidate =>
                                                    IsAuthoritativeLocalSource(candidate.AuthorSource) &&
                                                    IsAuthoritativeLocalSource(candidate.TitleSource));
            var hasNoReviewFlags = organisedPathIdentity is not null ||
                                   relatedCandidates.All(candidate => candidate.Warnings.Count == 0) &&
                                   (hasManualIdentity || relatedCandidates.All(candidate => !candidate.NeedsReview));
            var identityReviewCleared = !identity.RequiresReview ||
                                        hasManualIdentity && !IsTrackSequenceWarning(identity.Warning);
            var ready = !string.Equals(canonicalAuthor, "Unknown Author", StringComparison.OrdinalIgnoreCase) &&
                        meaningfulTitle &&
                        genre.Category != "Uncategorised" &&
                        hasNoReviewFlags &&
                        identityReviewCleared &&
                        (organisedPathIdentity is not null || hasManualIdentity || effectiveOnlineSuggestion is not null
                            ? confidence >= 0.90m
                            : confidence >= 0.95m && hasAuthoritativeLocalIdentity);
            var reasons = BuildReasons(
                relatedCandidates.Count,
                sourceFileCount,
                effectiveOnlineSuggestion,
                hasManualIdentity
                    ? "Canonical author and title were confirmed by you."
                    : identity.DerivationReason,
                genre.Reason);
            var warnings = BuildWarnings(
                relatedCandidates,
                canonicalAuthor,
                meaningfulTitle,
                genre.Category,
                onlineSuggestion is null && !hasAuthoritativeLocalIdentity,
                identityReviewCleared ? null : identity.Warning,
                ready);
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
                    firstPublishedYear,
                    genre.Category,
                    suggestedFolder,
                    fileNamePattern,
                    action,
                    relatedCandidates.Count,
                    sourceFileCount,
                    IsPrimaryCandidate: index == 0,
                    UsesOnlineMetadata: effectiveOnlineSuggestion is not null,
                    confidence,
                    ready,
                    FutureCombineCandidate: sourceFileCount > 1,
                    reasons,
                    warnings,
                    generatedAtUtc,
                    UsesManualGenre: manualGenre is not null,
                    UsesManualAuthor: hasManualAuthor,
                    UsesManualTitle: hasManualTitle,
                    CollectionHandling: collectionContext is null
                        ? AudiobookCollectionHandling.Automatic
                        : AudiobookCollectionHandling.SeparateBooks,
                    SeriesName: seriesName,
                    SeriesPosition: seriesPosition,
                    CollectionPlanKey: collectionContext?.PlanKey,
                    CoverUrl: onlineSuggestion?.CoverUrl);
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

    private static IReadOnlyDictionary<string, AudiobookReviewOverrideEntry> EmptyReviewOverrides { get; } =
        new Dictionary<string, AudiobookReviewOverrideEntry>(StringComparer.Ordinal);

    private static string CreateIdentityKey(AudiobookCandidateGroup candidate)
    {
        var reviewedProposal = candidate.OrganisationProposal;
        if (reviewedProposal is not null &&
            (reviewedProposal.UsesManualGenre ||
             reviewedProposal.UsesManualAuthor ||
             reviewedProposal.UsesManualTitle))
        {
            return $"review|{reviewedProposal.PlanKey}";
        }

        if (candidate.OnlineSuggestion is not null)
        {
            var organisedPathIdentity = TryResolveOrganisedLibraryIdentity([candidate]);
            if (organisedPathIdentity is not null)
            {
                return $"organised|{Normalize(organisedPathIdentity.GenreCategory)}|" +
                       $"{Normalize(organisedPathIdentity.Identity.Author)}|" +
                       $"{Normalize(organisedPathIdentity.SeriesName ?? string.Empty)}|" +
                       $"{Normalize(organisedPathIdentity.Identity.Title)}|" +
                       $"{organisedPathIdentity.FirstPublishedYear}|{organisedPathIdentity.SeriesPosition}";
            }

            return $"online|{candidate.OnlineSuggestion.ProviderName}|{candidate.OnlineSuggestion.ProviderItemId}";
        }

        var canonicalPathIdentity = TryResolveOrganisedLibraryIdentity([candidate]);
        if (canonicalPathIdentity is not null)
        {
            return $"organised|{Normalize(canonicalPathIdentity.GenreCategory)}|" +
                   $"{Normalize(canonicalPathIdentity.Identity.Author)}|" +
                   $"{Normalize(canonicalPathIdentity.SeriesName ?? string.Empty)}|" +
                   $"{Normalize(canonicalPathIdentity.Identity.Title)}|" +
                   $"{canonicalPathIdentity.FirstPublishedYear}|{canonicalPathIdentity.SeriesPosition}";
        }

        var identity = ResolveCanonicalIdentity([candidate], null);
        return $"local|{Normalize(identity.Author)}|{Normalize(identity.Title)}";
    }

    private static OrganisedLibraryIdentity? TryResolveOrganisedLibraryIdentity(
        IReadOnlyList<AudiobookCandidateGroup> candidates)
    {
        var parts = candidates
            .SelectMany(candidate => candidate.Parts)
            .GroupBy(part => part.MediaItem.Id)
            .Select(group => group.First())
            .ToList();
        if (parts.Count == 0)
        {
            return null;
        }

        var directories = parts
            .Select(part => Path.GetDirectoryName(part.MediaItem.RelativePath) ?? string.Empty)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (directories.Count != 1)
        {
            return null;
        }

        var folders = directories[0].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (folders.Length is not (3 or 4) || !AudiobookGenreCategories.Contains(folders[0]))
        {
            return null;
        }

        var author = folders[1];
        var seriesName = folders.Length == 4 ? folders[2] : null;
        var titleFolder = folders[^1];
        var titleMatch = CanonicalTitleFolderRegex().Match(titleFolder);
        if (!titleMatch.Success)
        {
            return null;
        }

        var title = titleMatch.Groups["title"].Value.Trim();
        int? firstPublishedYear = int.TryParse(
            titleMatch.Groups["year"].Value,
            out var parsedYear)
            ? parsedYear
            : null;
        int? seriesPosition = int.TryParse(
            titleMatch.Groups["position"].Value,
            out var parsedPosition)
            ? parsedPosition
            : null;
        if (!IsMeaningfulTitle(title) || !HasCanonicalOrganisedFileNames(parts, author, title))
        {
            return null;
        }

        return new OrganisedLibraryIdentity(
            new ResolvedBookIdentity(
                author,
                title,
                false,
                true,
                "Canonical identity came from the existing organised library path.",
                null),
            folders[0],
            firstPublishedYear,
            seriesName,
            seriesPosition);
    }

    private static bool HasCanonicalOrganisedFileNames(
        IReadOnlyList<AudiobookCandidatePart> parts,
        string author,
        string title)
    {
        if (parts.Count == 1)
        {
            var stem = Path.GetFileNameWithoutExtension(parts[0].MediaItem.FileName);
            return string.Equals(
                Normalize(stem),
                Normalize($"{author} - {title}"),
                StringComparison.Ordinal);
        }

        var sequenceNumbers = new List<int>(parts.Count);
        foreach (var part in parts)
        {
            var stem = Path.GetFileNameWithoutExtension(part.MediaItem.FileName);
            var match = CanonicalTrackFileRegex().Match(stem);
            if (!match.Success ||
                !string.Equals(
                    Normalize(match.Groups["title"].Value),
                    Normalize(title),
                    StringComparison.Ordinal) ||
                !int.TryParse(match.Groups["sequence"].Value, out var sequence))
            {
                return false;
            }

            sequenceNumbers.Add(sequence);
        }

        return sequenceNumbers.Order().SequenceEqual(Enumerable.Range(1, parts.Count));
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
            return new ResolvedBookIdentity(author, suggestion.Title, false, true, null, null);
        }

        var folderIdentity = candidates
            .Select(TryResolveNumberedBookLabelFromFolder)
            .FirstOrDefault(identity => identity is not null);
        if (folderIdentity is not null)
        {
            return folderIdentity;
        }

        var collectionIdentity = TryResolveCollectionIdentity(candidates);
        if (collectionIdentity is not null)
        {
            return collectionIdentity;
        }

        var bookFolderIdentity = candidates
            .Select(TryResolveBookIdentityFromFolder)
            .FirstOrDefault(identity => identity is not null);
        if (bookFolderIdentity is not null)
        {
            var requiresCompleteSequence = candidates.Count > 1 ||
                                           candidates.Any(candidate => IsSegmentTitle(candidate.Title));
            var requiresReview = requiresCompleteSequence && !HasCompleteTrackSequence(candidates);
            return requiresReview
                ? bookFolderIdentity with
                {
                    RequiresReview = true,
                    Warning = "The folder-derived book does not provide one complete, unique track sequence from 1 to the total file count."
                }
                : bookFolderIdentity;
        }

        return new ResolvedBookIdentity(
            SelectCanonicalAuthor(candidates, null),
            SelectCanonicalTitle(candidates, null),
            false,
            false,
            null,
            null);
    }

    private static ResolvedBookIdentity? TryResolveCollectionIdentity(
        IReadOnlyList<AudiobookCandidateGroup> candidates)
    {
        var albumValues = candidates
            .SelectMany(candidate => candidate.Parts)
            .Select(part => part.Metadata.Album)
            .Where(album => album.Source == MetadataValueSource.EmbeddedTag &&
                            IsMeaningfulCollectionTitle(album.Value))
            .Select(album => album.Value!.Trim())
            .GroupBy(Normalize, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        if (albumValues.Count != 1)
        {
            return null;
        }

        var albumTitle = albumValues[0];
        var hasCollectionEvidence = candidates.Any(candidate =>
            IsSegmentTitle(candidate.Title) ||
            candidate.IsMultipart ||
            candidate.Parts.Any(part => part.Metadata.TrackNumber is not null) &&
            !string.Equals(Normalize(candidate.Title), Normalize(albumTitle), StringComparison.Ordinal));
        if (!hasCollectionEvidence)
        {
            return null;
        }

        var hasSegmentTitles = candidates.Any(candidate => IsSegmentTitle(candidate.Title));
        var explicitlyCompleteSingleFile = candidates.Count == 1 &&
                                           candidates[0].Parts.Count == 1 &&
                                           ExplicitCompleteSingleFileRegex().IsMatch(candidates[0].Title);
        var requiresCompleteSequence = candidates.Count > 1 ||
                                       hasSegmentTitles && !explicitlyCompleteSingleFile;
        var hasCompleteSequence = HasCompleteTrackSequence(candidates);
        var requiresReview = requiresCompleteSequence && !hasCompleteSequence;
        var canonicalAuthor = SelectCanonicalAuthor(candidates, null);
        var canonicalTitle = explicitlyCompleteSingleFile
            ? OnlineMetadataLookupService.PrepareLookupTitle(candidates[0].Title, candidates[0].Author)
            : OnlineMetadataLookupService.PrepareLookupTitle(albumTitle, canonicalAuthor);

        return new ResolvedBookIdentity(
            canonicalAuthor,
            canonicalTitle,
            requiresReview,
            true,
            "Canonical book title came from consistent embedded album metadata.",
            requiresReview
                ? "The files do not provide one complete, unique track sequence from 1 to the total file count."
                : null);
    }

    private static ResolvedBookIdentity? TryResolveBookIdentityFromFolder(
        AudiobookCandidateGroup candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.Author) ||
            !candidate.Parts.Any(part => part.Metadata.TrackNumber is not null) &&
            !IsSegmentTitle(candidate.Title))
        {
            return null;
        }

        foreach (var part in candidate.Parts)
        {
            foreach (var folder in GetDirectoryFolders(part.MediaItem.RelativePath))
            {
                var folderMatch = AuthorTitleFolderRegex().Match(folder);
                if (!folderMatch.Success)
                {
                    continue;
                }

                var folderAuthor = folderMatch.Groups["author"].Value.Trim();
                if (!string.Equals(
                    Normalize(folderAuthor),
                    Normalize(candidate.Author),
                    StringComparison.Ordinal))
                {
                    continue;
                }

                var folderTitle = OnlineMetadataLookupService.PrepareLookupTitle(
                    folderMatch.Groups["title"].Value,
                    folderAuthor);
                if (!IsMeaningfulTitle(folderTitle))
                {
                    continue;
                }

                return new ResolvedBookIdentity(
                    folderAuthor,
                    folderTitle,
                    false,
                    true,
                    "Canonical book identity came from the matching author/book folder.",
                    null);
            }
        }

        return null;
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
            foreach (var folder in GetDirectoryFolders(part.MediaItem.RelativePath))
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
                    true,
                    "Canonical identity used the matching author/book folder because the embedded author field contains a numbered book label.",
                    "The embedded author/title fields appear to contain a numbered book label and chapter title; confirm the folder-derived identity before unattended handling.");
            }
        }

        return null;
    }

    private static IEnumerable<string> GetDirectoryFolders(string relativePath)
    {
        var directory = Path.GetDirectoryName(relativePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return [];
        }

        return directory.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse();
    }

    private static bool HasCompleteTrackSequence(
        IReadOnlyList<AudiobookCandidateGroup> candidates)
    {
        var trackNumbers = candidates
            .SelectMany(candidate => candidate.Parts)
            .Select(part => part.Metadata.TrackNumber)
            .ToList();
        return trackNumbers.Count > 0 &&
               trackNumbers.All(number => number is not null) &&
               trackNumbers.Select(number => number!.Value).Order().SequenceEqual(
                   Enumerable.Range(1, trackNumbers.Count).Select(number => (uint)number));
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
            warnings.Add("The inferred title appears to be a chapter, track, disc, or part label.");
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

    private static bool IsValidReviewValue(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maximumLength;

    private static bool IsTrackSequenceWarning(string? warning) =>
        warning?.Contains("track sequence", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsMeaningfulTitle(string title)
    {
        var normalized = Normalize(title);
        return normalized.Length >= 3 &&
               !IsSegmentTitle(title);
    }

    private static bool IsMeaningfulCollectionTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var normalized = Normalize(title);
        return normalized.Length >= 3 &&
               !GenericCollectionTitleRegex().IsMatch(normalized) &&
               !IsSegmentTitle(title);
    }

    private static bool IsSegmentTitle(string title)
    {
        var normalized = Normalize(title);
        return GenericTrackTitleRegex().IsMatch(normalized) ||
               SegmentPrefixRegex().IsMatch(title) ||
               SegmentSuffixRegex().IsMatch(title);
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
            proposal.ReadyForAutomaticHandling,
            proposal.UsesManualGenre,
            proposal.UsesManualAuthor,
            proposal.UsesManualTitle,
            proposal.CollectionHandling,
            Normalize(proposal.SeriesName ?? string.Empty),
            proposal.SeriesPosition,
            proposal.CollectionPlanKey,
            proposal.CoverUrl);
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
        bool ResolvesSegmentTitles,
        string? DerivationReason,
        string? Warning);

    private sealed record OrganisedLibraryIdentity(
        ResolvedBookIdentity Identity,
        string GenreCategory,
        int? FirstPublishedYear,
        string? SeriesName,
        int? SeriesPosition);

    private sealed record CollectionContext(
        string PlanKey,
        AudiobookReviewOverrideEntry ReviewOverride);

    [GeneratedRegex("""[<>:"/\\|?*\x00-\x1F]+""")]
    private static partial Regex InvalidPathCharacterRegex();

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+")]
    private static partial Regex NonAlphaNumericRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^(?:audio\s*track|track|disc|disk|cd|chapter|ch|part)\s*[\p{L}\p{Nd}]+$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericTrackTitleRegex();

    [GeneratedRegex(@"^\s*(?:audio\s*track|chapter|ch|part|track|disc|disk|cd)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SegmentPrefixRegex();

    [GeneratedRegex(@"(?:^|\s[-–—:]?\s*)\d{1,4}\s*(?:of|/)\s*\d{1,4}\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SegmentSuffixRegex();

    [GeneratedRegex(@"^(?:audio\s*book|audiobook|book|unknown|untitled|various)$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericCollectionTitleRegex();

    [GeneratedRegex(@"(?:^|\s[-–—:]?\s*)0*1\s*(?:of|/)\s*0*1\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitCompleteSingleFileRegex();

    [GeneratedRegex(@"^\s*\d{1,3}\s+(?<title>.+?)\s*$")]
    private static partial Regex NumberedBookLabelRegex();

    [GeneratedRegex(@"^\s*(?<author>.+?)\s+[-–—]\s+(?<title>.+?)\s*$")]
    private static partial Regex AuthorTitleFolderRegex();

    [GeneratedRegex(@"^(?:(?<position>\d{1,3})\s+-\s+)?(?<title>.+?)(?:\s+\((?<year>\d{4})\))?$")]
    private static partial Regex CanonicalTitleFolderRegex();

    [GeneratedRegex(@"^(?<sequence>\d{3})\s+-\s+(?<title>.+?)$")]
    private static partial Regex CanonicalTrackFileRegex();

    [GeneratedRegex(@"^(?<series>.+?)(?:\s+Series)?\s*[,;:\-–—]?\s*Book\s*[#:]?\s*(?<position>\d+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesBookSuffixRegex();
}
