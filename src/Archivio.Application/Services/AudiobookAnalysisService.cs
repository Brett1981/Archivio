using System.Globalization;
using System.Text.RegularExpressions;
using Archivio.Application.Abstractions;
using Archivio.Domain;

namespace Archivio.Application.Services;

public sealed partial class AudiobookAnalysisService : IAudiobookAnalysisService
{
    private const int CheckpointBatchSize = 50;
    private readonly ILocalMediaMetadataService _localMediaMetadataService;
    private readonly IAudiobookAnalysisStore _analysisStore;

    public AudiobookAnalysisService(
        ILocalMediaMetadataService localMediaMetadataService,
        IAudiobookAnalysisStore? analysisStore = null)
    {
        _localMediaMetadataService = localMediaMetadataService ??
            throw new ArgumentNullException(nameof(localMediaMetadataService));
        _analysisStore = analysisStore ?? NullAudiobookAnalysisStore.Instance;
    }

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m4b", ".m4a", ".mp3", ".aac", ".flac", ".ogg", ".opus", ".wav", ".wma"
    };

    public IReadOnlyList<AudiobookCandidateGroup> Analyse(IEnumerable<MediaItem> mediaItems)
    {
        ArgumentNullException.ThrowIfNull(mediaItems);

        var groups = AnalyseCore(mediaItems, null, CancellationToken.None);
        return EnrichMetadataCore(groups, null, CancellationToken.None);
    }

    public async Task<IReadOnlyList<AudiobookCandidateGroup>> AnalyseAsync(
        IEnumerable<MediaItem> mediaItems,
        IProgress<AudiobookAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaItems);
        var snapshot = mediaItems.ToList();
        var eligibleItems = GetEligibleItems(snapshot);
        var sourceId = GetLibrarySourceId(eligibleItems);
        var groups = await Task.Run(
            () => AnalyseCore(eligibleItems, progress, cancellationToken),
            cancellationToken);

        if (sourceId is null)
        {
            return groups;
        }

        var cachedEntries = await _analysisStore.LoadMetadataCacheAsync(sourceId.Value, cancellationToken);
        var validCache = eligibleItems
            .Where(item => cachedEntries.TryGetValue(item.Id, out var entry) && IsCacheValid(item, entry))
            .ToDictionary(item => item.Id, item => cachedEntries[item.Id]);
        var reusedWarnings = validCache.Values.Count(entry => entry.Metadata.Warnings.Count > 0);

        await _analysisStore.BeginAnalysisAsync(
            sourceId.Value,
            eligibleItems.Count,
            validCache.Count,
            reusedWarnings,
            cancellationToken);

        try
        {
            var result = await EnrichMetadataWithCheckpointsAsync(
                sourceId.Value,
                groups,
                validCache,
                progress,
                cancellationToken);
            var warningCount = result
                .SelectMany(candidate => candidate.Parts)
                .Count(part => part.Metadata.Warnings.Count > 0);

            await _analysisStore.CompleteAnalysisAsync(
                sourceId.Value,
                result,
                eligibleItems.Count,
                eligibleItems.Count,
                warningCount,
                cancellationToken);
            return result;
        }
        catch (OperationCanceledException)
        {
            await _analysisStore.MarkAnalysisInterruptedAsync(
                sourceId.Value,
                wasCancelled: true,
                errorMessage: null,
                CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            await _analysisStore.MarkAnalysisInterruptedAsync(
                sourceId.Value,
                wasCancelled: false,
                exception.Message,
                CancellationToken.None);
            throw;
        }
    }

    public async Task<SavedAudiobookAnalysis?> LoadSavedAnalysisAsync(
        IEnumerable<MediaItem> mediaItems,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaItems);
        var snapshot = mediaItems.ToList();
        var eligibleItems = GetEligibleItems(snapshot);
        var sourceId = GetLibrarySourceId(eligibleItems);
        if (sourceId is null)
        {
            return null;
        }

        var saved = await _analysisStore.LoadCompletedAnalysisAsync(
            sourceId.Value,
            snapshot,
            cancellationToken);
        if (saved is null)
        {
            return null;
        }

        var currentIds = eligibleItems.Select(item => item.Id).ToHashSet();
        var savedIds = saved.Candidates
            .SelectMany(candidate => candidate.Parts)
            .Select(part => part.MediaItem.Id)
            .ToHashSet();
        return currentIds.SetEquals(savedIds) ? saved : null;
    }

    public Task<AudiobookCandidateGroup> EnrichMetadataAsync(
        AudiobookCandidateGroup candidate,
        IProgress<AudiobookAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (candidate.HasLoadedLocalMetadata)
        {
            return Task.FromResult(candidate);
        }

        return Task.Run(() =>
        {
            var parsedCandidates = new List<ParsedCandidate>(candidate.Parts.Count);
            for (var index = 0; index < candidate.Parts.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mediaItem = candidate.Parts[index].MediaItem;
                progress?.Report(new AudiobookAnalysisProgress(
                    AudiobookAnalysisStage.ReadingMetadata,
                    mediaItem.FullPath,
                    index,
                    candidate.Parts.Count));

                var metadata = ReadMetadataSafely(mediaItem, candidate.Parts[index].Metadata);
                parsedCandidates.Add(Parse(mediaItem, metadata, metadataWasLoaded: true));

                progress?.Report(new AudiobookAnalysisProgress(
                    AudiobookAnalysisStage.ReadingMetadata,
                    mediaItem.FullPath,
                    index + 1,
                    candidate.Parts.Count));
            }

            var enrichedGroups = parsedCandidates
                .GroupBy(value => value.GroupKey, StringComparer.OrdinalIgnoreCase)
                .Select(CreateGroup)
                .ToList();
            return enrichedGroups.Count == 1
                ? enrichedGroups[0]
                : throw new InvalidOperationException("The selected audiobook candidate could not be enriched as one group.");
        }, cancellationToken);
    }

    private static IReadOnlyList<AudiobookCandidateGroup> AnalyseCore(
        IEnumerable<MediaItem> mediaItems,
        IProgress<AudiobookAnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        var eligibleItems = GetEligibleItems(mediaItems);
        var parsedCandidates = new List<ParsedCandidate>(eligibleItems.Count);

        progress?.Report(new AudiobookAnalysisProgress(
            AudiobookAnalysisStage.Grouping,
            null,
            0,
            eligibleItems.Count));

        for (var index = 0; index < eligibleItems.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = eligibleItems[index];
            parsedCandidates.Add(Parse(item));

            if (index == 0 || (index + 1) % 100 == 0 || index == eligibleItems.Count - 1)
            {
                progress?.Report(new AudiobookAnalysisProgress(
                    AudiobookAnalysisStage.Grouping,
                    item.FullPath,
                    index + 1,
                    eligibleItems.Count));
            }
        }

        return parsedCandidates
            .GroupBy(candidate => candidate.GroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(CreateGroup)
            .OrderBy(group => group.Author ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<AudiobookCandidateGroup> EnrichMetadataCore(
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        IProgress<AudiobookAnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        var totalParts = candidates.Sum(candidate => candidate.Parts.Count);
        var processedParts = 0;
        var warningCount = 0;
        var enrichedCandidates = new List<AudiobookCandidateGroup>(candidates.Count);

        progress?.Report(new AudiobookAnalysisProgress(
            AudiobookAnalysisStage.ReadingMetadata,
            null,
            0,
            totalParts));

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parsedCandidates = new List<ParsedCandidate>(candidate.Parts.Count);

            foreach (var part in candidate.Parts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mediaItem = part.MediaItem;
                progress?.Report(new AudiobookAnalysisProgress(
                    AudiobookAnalysisStage.ReadingMetadata,
                    mediaItem.FullPath,
                    processedParts,
                    totalParts,
                    warningCount));

                var metadata = ReadMetadataSafely(mediaItem, part.Metadata);
                parsedCandidates.Add(Parse(mediaItem, metadata, metadataWasLoaded: true));
                processedParts++;
                if (metadata.Warnings.Count > 0)
                {
                    warningCount++;
                }

                progress?.Report(new AudiobookAnalysisProgress(
                    AudiobookAnalysisStage.ReadingMetadata,
                    mediaItem.FullPath,
                    processedParts,
                    totalParts,
                    warningCount));
            }

            enrichedCandidates.AddRange(CreateMetadataAwareGroups(parsedCandidates));
        }

        return enrichedCandidates
            .OrderBy(group => group.Author ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<AudiobookCandidateGroup>> EnrichMetadataWithCheckpointsAsync(
        Guid librarySourceId,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        IReadOnlyDictionary<Guid, AudiobookMetadataCacheEntry> validCache,
        IProgress<AudiobookAnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        var parts = candidates.SelectMany(candidate => candidate.Parts).ToList();
        var metadataByMediaItemId = validCache.ToDictionary(entry => entry.Key, entry => entry.Value.Metadata);
        var processedCount = 0;
        var warningCount = 0;

        progress?.Report(new AudiobookAnalysisProgress(
            AudiobookAnalysisStage.ReadingMetadata,
            null,
            0,
            parts.Count));

        foreach (var batch in parts.Chunk(CheckpointBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchOffset = processedCount;
            var batchWarningOffset = warningCount;
            var batchResult = await Task.Run(() => ProcessMetadataBatch(
                batch,
                validCache,
                batchOffset,
                parts.Count,
                batchWarningOffset,
                progress,
                cancellationToken), cancellationToken);

            foreach (var result in batchResult.Results)
            {
                metadataByMediaItemId[result.MediaItemId] = result.Metadata;
            }

            processedCount += batch.Length;
            warningCount += batchResult.WarningCount;
            if (batchResult.NewEntries.Count > 0)
            {
                await _analysisStore.SaveCheckpointAsync(
                    librarySourceId,
                    batchResult.NewEntries,
                    processedCount,
                    parts.Count,
                    warningCount,
                    cancellationToken);
            }
        }

        return await Task.Run(() => CreateEnrichedCandidates(
            candidates,
            metadataByMediaItemId,
            cancellationToken), cancellationToken);
    }

    private MetadataBatchResult ProcessMetadataBatch(
        IReadOnlyList<AudiobookCandidatePart> parts,
        IReadOnlyDictionary<Guid, AudiobookMetadataCacheEntry> validCache,
        int processedOffset,
        int totalCount,
        int warningOffset,
        IProgress<AudiobookAnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        var results = new List<MetadataResult>(parts.Count);
        var newEntries = new List<AudiobookMetadataCacheEntry>(parts.Count);
        var batchWarningCount = 0;

        for (var index = 0; index < parts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var part = parts[index];
            var mediaItem = part.MediaItem;
            progress?.Report(new AudiobookAnalysisProgress(
                AudiobookAnalysisStage.ReadingMetadata,
                mediaItem.FullPath,
                processedOffset + index,
                totalCount,
                warningOffset + batchWarningCount));

            LocalMediaMetadata metadata;
            if (validCache.TryGetValue(mediaItem.Id, out var cachedEntry))
            {
                metadata = cachedEntry.Metadata;
            }
            else
            {
                metadata = ReadMetadataSafely(mediaItem, part.Metadata);
                newEntries.Add(new AudiobookMetadataCacheEntry(
                    mediaItem.Id,
                    mediaItem.SizeBytes,
                    mediaItem.ModifiedAtUtc,
                    DateTime.UtcNow,
                    metadata));
            }

            if (metadata.Warnings.Count > 0)
            {
                batchWarningCount++;
            }

            results.Add(new MetadataResult(mediaItem.Id, metadata));
            progress?.Report(new AudiobookAnalysisProgress(
                AudiobookAnalysisStage.ReadingMetadata,
                mediaItem.FullPath,
                processedOffset + index + 1,
                totalCount,
                warningOffset + batchWarningCount));
        }

        return new MetadataBatchResult(results, newEntries, batchWarningCount);
    }

    private static IReadOnlyList<AudiobookCandidateGroup> CreateEnrichedCandidates(
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        IReadOnlyDictionary<Guid, LocalMediaMetadata> metadataByMediaItemId,
        CancellationToken cancellationToken)
    {
        var enrichedCandidates = new List<AudiobookCandidateGroup>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parsedCandidates = candidate.Parts
                .Select(part => Parse(part.MediaItem, metadataByMediaItemId[part.MediaItem.Id], metadataWasLoaded: true))
                .ToList();
            enrichedCandidates.AddRange(CreateMetadataAwareGroups(parsedCandidates));
        }

        return enrichedCandidates
            .OrderBy(group => group.Author ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<MediaItem> GetEligibleItems(IEnumerable<MediaItem> mediaItems) =>
        mediaItems
            .Where(item => !item.IsMissing && AudioExtensions.Contains(item.Extension))
            .ToList();

    private static Guid? GetLibrarySourceId(IReadOnlyCollection<MediaItem> mediaItems)
    {
        if (mediaItems.Count == 0)
        {
            return null;
        }

        var sourceIds = mediaItems.Select(item => item.LibrarySourceId).Distinct().ToList();
        return sourceIds.Count == 1
            ? sourceIds[0]
            : throw new ArgumentException("Audiobook analysis can only process one library source at a time.", nameof(mediaItems));
    }

    private static bool IsCacheValid(MediaItem mediaItem, AudiobookMetadataCacheEntry entry) =>
        mediaItem.SizeBytes == entry.SizeBytes && mediaItem.ModifiedAtUtc == entry.ModifiedAtUtc;

    private LocalMediaMetadata ReadMetadataSafely(MediaItem mediaItem, LocalMediaMetadata fallback)
    {
        try
        {
            return _localMediaMetadataService.Read(mediaItem.FullPath);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
                                          not StackOverflowException and
                                          not AccessViolationException)
        {
            return fallback with
            {
                Warnings =
                [
                    $"Metadata reader failed with {exception.GetType().Name}: {exception.Message}"
                ]
            };
        }
    }

    private static ParsedCandidate Parse(
        MediaItem item,
        LocalMediaMetadata? metadata = null,
        bool metadataWasLoaded = false)
    {
        var stem = Path.GetFileNameWithoutExtension(item.FileName).Trim();
        var sequenceMatch = FindSequenceMatch(stem);
        var filenameSequence = sequenceMatch.Success &&
                               int.TryParse(sequenceMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var parsed) &&
                               parsed > 0
            ? parsed
            : 0;
        var trackNumber = metadata?.TrackNumber;
        var metadataSequence = trackNumber is > 0 and <= int.MaxValue
            ? (int)trackNumber.Value
            : 0;
        var sequence = metadataSequence > 0 ? metadataSequence : filenameSequence;
        var sequenceWasInferred = metadataSequence > 0 || filenameSequence > 0;

        var cleanedStem = sequenceMatch.Success
            ? NormalizeWhitespace(stem.Remove(sequenceMatch.Index, sequenceMatch.Length).Trim(' ', '-', '_', '.'))
            : NormalizeWhitespace(stem);

        var (author, title) = ParseAuthorAndTitle(cleanedStem, item.RelativePath);
        var parentPath = Path.GetDirectoryName(item.RelativePath) ?? string.Empty;
        var normalizedTitle = NormalizeKey(title);
        var groupKey = $"{NormalizeKey(parentPath)}|{normalizedTitle}";

        metadata ??= CreateInferredMetadata(item.FullPath, title, author);

        return new ParsedCandidate(
            item,
            groupKey,
            author,
            title,
            sequence,
            sequenceWasInferred,
            metadataSequence,
            filenameSequence,
            metadata,
            metadataWasLoaded);
    }

    private static LocalMediaMetadata CreateInferredMetadata(string filePath, string title, string? author) =>
        new(
            filePath,
            new MetadataValue(title, MetadataValueSource.Inferred),
            new MetadataValue(
                author,
                string.IsNullOrWhiteSpace(author) ? MetadataValueSource.None : MetadataValueSource.Inferred),
            new MetadataValue(null, MetadataValueSource.None),
            new MetadataValue(null, MetadataValueSource.None),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            [],
            []);

    private static (string? Author, string Title) ParseAuthorAndTitle(string cleanedStem, string relativePath)
    {
        var parent = Path.GetFileName(Path.GetDirectoryName(relativePath));
        var grandParent = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(relativePath) ?? string.Empty));
        var separatorIndex = cleanedStem.IndexOf(" - ", StringComparison.Ordinal);
        if (separatorIndex > 0 && separatorIndex < cleanedStem.Length - 3)
        {
            var possibleAuthor = NormalizeWhitespace(cleanedStem[..separatorIndex]);
            var title = NormalizeWhitespace(cleanedStem[(separatorIndex + 3)..]);
            if (PublicationYearRegex().IsMatch(possibleAuthor))
            {
                return (FindFolderAuthor(parent, grandParent, title), title);
            }

            return (
                possibleAuthor,
                title);
        }

        if (!string.IsNullOrWhiteSpace(parent) && !string.Equals(parent, ".", StringComparison.Ordinal))
        {
            var folderTitle = NormalizeWhitespace(parent);
            var author = string.IsNullOrWhiteSpace(grandParent) ? null : NormalizeWhitespace(grandParent);
            return (author, folderTitle);
        }

        return (null, cleanedStem);
    }

    private static string? FindFolderAuthor(string? parent, string? grandParent, string title)
    {
        if (IsPlausibleFolderAuthor(grandParent, title))
        {
            return NormalizeWhitespace(grandParent!);
        }

        if (!string.IsNullOrWhiteSpace(parent))
        {
            var separatorIndex = parent.IndexOf(" - ", StringComparison.Ordinal);
            var possibleAuthor = separatorIndex > 0
                ? parent[..separatorIndex]
                : parent;
            if (IsPlausibleFolderAuthor(possibleAuthor, title))
            {
                return NormalizeWhitespace(possibleAuthor);
            }
        }

        return null;
    }

    private static bool IsPlausibleFolderAuthor(string? value, string title) =>
        !string.IsNullOrWhiteSpace(value) &&
        !string.Equals(value, ".", StringComparison.Ordinal) &&
        !PublicationYearRegex().IsMatch(value.Trim()) &&
        !string.Equals(NormalizeKey(value), NormalizeKey(title), StringComparison.Ordinal);

    private static AudiobookCandidateGroup CreateGroup(IGrouping<string, ParsedCandidate> candidates)
    {
        var materialized = candidates.ToList();
        var useFilenameSequence = HasCompleteSequence(
            materialized.Select(candidate => candidate.FilenameSequence),
            materialized.Count);
        var useMetadataSequence = !useFilenameSequence && HasCompleteSequence(
            materialized.Select(candidate => candidate.MetadataSequence),
            materialized.Count);
        int EffectiveSequence(ParsedCandidate candidate) => useFilenameSequence
            ? candidate.FilenameSequence
            : useMetadataSequence
                ? candidate.MetadataSequence
                : candidate.Sequence;

        var ordered = materialized
            .OrderBy(candidate => EffectiveSequence(candidate) == 0 ? int.MaxValue : EffectiveSequence(candidate))
            .ThenBy(candidate => candidate.Item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var first = ordered[0];
        var warnings = new List<string>();
        if (ordered.Count > 1 && ordered.Any(candidate => EffectiveSequence(candidate) == 0))
        {
            warnings.Add("One or more part numbers could not be inferred; filename order is being used.");
        }

        var duplicateSequences = ordered
            .Where(candidate => EffectiveSequence(candidate) > 0)
            .GroupBy(candidate => EffectiveSequence(candidate))
            .Where(group => group.Count() > 1)
            .ToList();
        foreach (var duplicateSequence in duplicateSequences)
        {
            warnings.Add(
                $"Duplicate part number {duplicateSequence.Key}: " +
                $"{string.Join(", ", duplicateSequence.Select(candidate => candidate.Item.FileName))}.");
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
        var titleMetadata = SelectConsistentMultipartAlbum(ordered) ??
                            (ordered.Count > 1 && useFilenameSequence
                                ? fallbackTitle
                                : SelectGroupMetadataValue(
                                    ordered.Select(candidate => candidate.Metadata.Title),
                                    fallbackTitle,
                                    "Title",
                                    warnings));
        var author = authorMetadata.Value;
        var title = titleMetadata.Value ?? first.Title;

        var confidence = CalculateConfidence(author, title, ordered, warnings);
        var parts = ordered
            .Select((candidate, index) => new AudiobookCandidatePart(
                candidate.Item,
                EffectiveSequence(candidate) > 0 ? EffectiveSequence(candidate) : index + 1,
                EffectiveSequence(candidate) > 0,
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
            ordered.All(candidate => candidate.MetadataWasLoaded),
            parts,
            confidence,
            warnings);
    }

    private static bool HasCompleteSequence(IEnumerable<int> values, int count) =>
        values.Order().SequenceEqual(Enumerable.Range(1, count));

    private static IReadOnlyList<AudiobookCandidateGroup> CreateMetadataAwareGroups(
        IReadOnlyList<ParsedCandidate> candidates)
    {
        var keepEachFileSeparate = LooksLikeSeparateCompleteBooks(candidates);
        return candidates
            .GroupBy(
                candidate => keepEachFileSeparate
                    ? candidate.Item.Id.ToString("N")
                    : candidate.GroupKey,
                StringComparer.OrdinalIgnoreCase)
            .Select(CreateGroup)
            .ToList();
    }

    private static bool LooksLikeSeparateCompleteBooks(IReadOnlyList<ParsedCandidate> candidates)
    {
        if (candidates.Count <= 1 ||
            candidates.Any(candidate => !string.Equals(
                candidate.Item.Extension,
                ".m4b",
                StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var embeddedTitles = candidates
            .Select(candidate => candidate.Metadata.Title)
            .ToList();
        if (embeddedTitles.Any(title => title.Source != MetadataValueSource.EmbeddedTag || !title.HasValue) ||
            embeddedTitles.Select(title => NormalizeKey(title.Value!))
                .Distinct(StringComparer.Ordinal)
                .Count() != candidates.Count)
        {
            return false;
        }

        var explicitBookLabels = embeddedTitles.Count(title =>
            CompleteBookMarkerRegex().IsMatch(title.Value!));
        var longFormFiles = candidates.Count(candidate =>
            candidate.Metadata.Duration >= TimeSpan.FromMinutes(45));
        return explicitBookLabels >= Math.Max(2, candidates.Count / 2) ||
               longFormFiles == candidates.Count;
    }

    private static Match FindSequenceMatch(string stem)
    {
        var match = PartNumberRegex().Match(stem);
        if (match.Success)
        {
            return match;
        }

        match = ChapterNumberRegex().Match(stem);
        if (match.Success)
        {
            return match;
        }

        match = LeadingSequenceRegex().Match(stem);
        return match.Success ? match : TrailingSequenceRegex().Match(stem);
    }

    private static MetadataValue? SelectConsistentMultipartAlbum(IReadOnlyCollection<ParsedCandidate> candidates)
    {
        if (candidates.Count <= 1 || candidates.Any(candidate => !candidate.Metadata.Album.HasValue))
        {
            return null;
        }

        var albums = candidates
            .Select(candidate => candidate.Metadata.Album)
            .GroupBy(album => album.Value!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (albums.Count != 1)
        {
            return null;
        }

        var album = albums[0].OrderBy(value => GetSourcePriority(value.Source)).First();
        return album with { Value = album.Value!.Trim() };
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

    [GeneratedRegex(@"(?i)(?:^|[\s._-])(?:chapter|ch)[\s._-]*(\d{1,4})(?:$|[\s._-])")]
    private static partial Regex ChapterNumberRegex();

    [GeneratedRegex(@"^\s*(\d{1,3})(?=[\s._-])[\s._-]*")]
    private static partial Regex LeadingSequenceRegex();

    [GeneratedRegex(@"\s[-–—]\s*(\d{1,3})\s*$")]
    private static partial Regex TrailingSequenceRegex();

    [GeneratedRegex(@"^(?:18|19|20)\d{2}$")]
    private static partial Regex PublicationYearRegex();

    [GeneratedRegex(@"\b(?:book|volume)\s*[#:]?\s*\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex CompleteBookMarkerRegex();

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
        int MetadataSequence,
        int FilenameSequence,
        LocalMediaMetadata Metadata,
        bool MetadataWasLoaded);

    private sealed record MetadataResult(Guid MediaItemId, LocalMediaMetadata Metadata);

    private sealed record MetadataBatchResult(
        IReadOnlyList<MetadataResult> Results,
        IReadOnlyList<AudiobookMetadataCacheEntry> NewEntries,
        int WarningCount);

    private sealed class NullAudiobookAnalysisStore : IAudiobookAnalysisStore
    {
        public static NullAudiobookAnalysisStore Instance { get; } = new();

        public Task<IReadOnlyDictionary<Guid, AudiobookMetadataCacheEntry>> LoadMetadataCacheAsync(
            Guid librarySourceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, AudiobookMetadataCacheEntry>>(
                new Dictionary<Guid, AudiobookMetadataCacheEntry>());

        public Task<SavedAudiobookAnalysis?> LoadCompletedAnalysisAsync(
            Guid librarySourceId,
            IReadOnlyList<MediaItem> currentMediaItems,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SavedAudiobookAnalysis?>(null);

        public Task BeginAnalysisAsync(
            Guid librarySourceId,
            int totalCount,
            int reusedCount,
            int warningCount,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveCheckpointAsync(
            Guid librarySourceId,
            IReadOnlyCollection<AudiobookMetadataCacheEntry> metadataEntries,
            int processedCount,
            int totalCount,
            int warningCount,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CompleteAnalysisAsync(
            Guid librarySourceId,
            IReadOnlyList<AudiobookCandidateGroup> candidates,
            int processedCount,
            int totalCount,
            int warningCount,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task MarkAnalysisInterruptedAsync(
            Guid librarySourceId,
            bool wasCancelled,
            string? errorMessage,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyDictionary<string, OnlineMetadataCacheEntry>> LoadOnlineMetadataCacheAsync(
            Guid librarySourceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, OnlineMetadataCacheEntry>>(
                new Dictionary<string, OnlineMetadataCacheEntry>());

        public Task SaveOnlineMetadataCacheAsync(
            Guid librarySourceId,
            IReadOnlyCollection<OnlineMetadataCacheEntry> entries,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PruneOnlineMetadataCacheAsync(
            Guid librarySourceId,
            IReadOnlyCollection<string> currentCandidateKeys,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
