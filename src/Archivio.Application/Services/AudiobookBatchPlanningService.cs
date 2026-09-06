using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Archivio.Application.Abstractions;
using Archivio.Domain;

namespace Archivio.Application.Services;

public sealed partial class AudiobookBatchPlanningService(
    IAudiobookBatchDecisionStore decisionStore) : IAudiobookBatchPlanningService
{
    private const string BatchAlgorithmVersion = "audiobook-batch-v4";

    public async Task<IReadOnlyList<AudiobookCandidateGroup>> PrepareBatchAsync(
        Guid librarySourceId,
        string libraryRoot,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        IReadOnlyList<MediaItem> indexedMedia,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(indexedMedia);

        if (candidates.Count == 0)
        {
            return candidates;
        }

        var preparedAtUtc = DateTime.UtcNow;
        var indexedByRelativePath = indexedMedia
            .Where(item => !item.IsMissing)
            .GroupBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var plans = candidates
            .Where(candidate => candidate.OrganisationProposal is not null)
            .GroupBy(candidate => candidate.OrganisationProposal!.PlanKey, StringComparer.Ordinal)
            .Select(group => CreatePlan(
                libraryRoot,
                group.ToList(),
                indexedByRelativePath,
                preparedAtUtc))
            .ToDictionary(plan => plan.PlanKey, StringComparer.Ordinal);

        AddCrossPlanDestinationConflicts(plans);
        plans = plans.Values
            .Select(WithInputSignature)
            .ToDictionary(plan => plan.PlanKey, StringComparer.Ordinal);

        var savedDecisions = await decisionStore.LoadAsync(librarySourceId, cancellationToken);
        var entriesToSave = new List<AudiobookBatchDecisionEntry>();
        foreach (var (planKey, plan) in plans.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decision = AudiobookBatchDecision.Pending;
            if (savedDecisions.TryGetValue(planKey, out var saved) &&
                saved.InputSignature == plan.InputSignature)
            {
                decision = saved.Decision == AudiobookBatchDecision.Approved && !plan.CanApprove
                    ? AudiobookBatchDecision.Pending
                    : saved.Decision;
            }

            var updated = plan with { Decision = decision };
            plans[planKey] = updated;
            if (!savedDecisions.TryGetValue(planKey, out var current) ||
                current.InputSignature != updated.InputSignature ||
                current.Decision != updated.Decision)
            {
                entriesToSave.Add(ToDecisionEntry(updated, preparedAtUtc));
            }
        }

        await decisionStore.SaveAsync(librarySourceId, entriesToSave, cancellationToken);
        return candidates
            .Select(candidate => candidate.OrganisationProposal is not null &&
                                 plans.TryGetValue(candidate.OrganisationProposal.PlanKey, out var plan)
                ? candidate with { BatchPlan = plan }
                : candidate)
            .ToList();
    }

    public async Task<IReadOnlyList<AudiobookCandidateGroup>> SetDecisionAsync(
        Guid librarySourceId,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        IReadOnlyCollection<string> planKeys,
        AudiobookBatchDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(planKeys);
        var requestedKeys = planKeys.ToHashSet(StringComparer.Ordinal);
        if (requestedKeys.Count == 0)
        {
            return candidates;
        }

        var now = DateTime.UtcNow;
        var updatedPlans = candidates
            .Where(candidate => candidate.BatchPlan is not null)
            .Select(candidate => candidate.BatchPlan!)
            .GroupBy(plan => plan.PlanKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .Where(plan => requestedKeys.Contains(plan.PlanKey))
            .Where(plan => decision != AudiobookBatchDecision.Approved || plan.CanApprove)
            .Select(plan => plan with { Decision = decision })
            .ToDictionary(plan => plan.PlanKey, StringComparer.Ordinal);

        if (updatedPlans.Count == 0)
        {
            return candidates;
        }

        await decisionStore.SaveAsync(
            librarySourceId,
            updatedPlans.Values.Select(plan => ToDecisionEntry(plan, now)).ToList(),
            cancellationToken);
        return candidates
            .Select(candidate => candidate.BatchPlan is not null &&
                                 updatedPlans.TryGetValue(candidate.BatchPlan.PlanKey, out var plan)
                ? candidate with { BatchPlan = plan }
                : candidate)
            .ToList();
    }

    private static AudiobookBatchPlan CreatePlan(
        string libraryRoot,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        IReadOnlyDictionary<string, List<MediaItem>> indexedByRelativePath,
        DateTime preparedAtUtc)
    {
        var proposal = candidates
            .Select(candidate => candidate.OrganisationProposal)
            .FirstOrDefault(proposal => proposal?.IsPrimaryCandidate == true) ??
            candidates[0].OrganisationProposal!;
        var uniqueParts = candidates
            .SelectMany(candidate => candidate.Parts)
            .GroupBy(part => part.MediaItem.Id)
            .Select(group => group.First())
            .ToList();
        var hasCompleteTrackOrder = TryGetCompleteTrackOrder(uniqueParts, out var detectedTrackOrder);
        var parts = (hasCompleteTrackOrder
                ? uniqueParts.OrderBy(part => detectedTrackOrder[part.MediaItem.Id])
                    .ThenBy(part => part.MediaItem.RelativePath, StringComparer.OrdinalIgnoreCase)
                : uniqueParts.OrderBy(part => part.MediaItem.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(part => part.Sequence))
            .ToList();
        var sourceIds = parts.Select(part => part.MediaItem.Id).ToHashSet();
        var warnings = new List<string>();
        if (proposal.RecommendedAction == AudiobookOrganisationAction.ConsolidateCandidates &&
            !hasCompleteTrackOrder)
        {
            warnings.Add(
                "Consolidated candidates require one complete, unique track sequence from 1 to the total file count in embedded metadata or filenames.");
        }
        var operations = new List<AudiobookFileOperation>(parts.Count);

        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            var destinationFileName = CreateDestinationFileName(
                proposal,
                index + 1,
                parts.Count,
                part.MediaItem.Extension);
            var destination = Path.Combine(proposal.SuggestedRelativeFolder, destinationFileName);
            if (!TryValidateRelativeDestination(libraryRoot, destination, out var validationWarning))
            {
                warnings.Add($"{part.MediaItem.FileName}: {validationWarning}");
            }

            if (part.MediaItem.IsMissing)
            {
                warnings.Add($"Source is marked missing: {part.MediaItem.RelativePath}");
            }

            if (indexedByRelativePath.TryGetValue(destination, out var occupants) &&
                occupants.Any(item => item.Id != part.MediaItem.Id))
            {
                var outsidePlan = occupants.Any(item => !sourceIds.Contains(item.Id));
                warnings.Add(outsidePlan
                    ? $"Destination is already occupied by another indexed item: {destination}"
                    : $"Destination is occupied by another source file in this plan: {destination}");
            }

            operations.Add(new AudiobookFileOperation(
                part.MediaItem.Id,
                part.MediaItem.RelativePath,
                destination,
                SelectOperationKind(
                    part.MediaItem.RelativePath,
                    destination,
                    NeedsMetadataUpdate(part, proposal, index + 1, parts.Count))));
        }

        var duplicateTargets = operations
            .GroupBy(operation => operation.DestinationRelativePath, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(operation => operation.MediaItemId).Distinct().Count() > 1)
            .Select(group => group.Key);
        warnings.AddRange(duplicateTargets.Select(target => $"More than one source maps to: {target}"));

        var status = warnings.Count > 0
            ? AudiobookBatchValidationStatus.Conflict
            : !proposal.ReadyForAutomaticHandling
                ? AudiobookBatchValidationStatus.ReviewRequired
                : operations.All(operation => operation.Kind == AudiobookFileOperationKind.NoChange)
                    ? AudiobookBatchValidationStatus.NoChange
                    : AudiobookBatchValidationStatus.Ready;
        return new AudiobookBatchPlan(
            proposal.PlanKey,
            string.Empty,
            proposal.CanonicalDisplay,
            status,
            AudiobookBatchDecision.Pending,
            operations,
            warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            preparedAtUtc);
    }

    private static void AddCrossPlanDestinationConflicts(
        IDictionary<string, AudiobookBatchPlan> plans)
    {
        var duplicates = plans.Values
            .SelectMany(plan => plan.Operations.Select(operation => (plan.PlanKey, Operation: operation)))
            .GroupBy(value => value.Operation.DestinationRelativePath, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(value => value.PlanKey).Distinct(StringComparer.Ordinal).Count() > 1)
            .ToList();

        foreach (var duplicate in duplicates)
        {
            foreach (var planKey in duplicate.Select(value => value.PlanKey).Distinct(StringComparer.Ordinal))
            {
                var plan = plans[planKey];
                plans[planKey] = plan with
                {
                    ValidationStatus = AudiobookBatchValidationStatus.Conflict,
                    Warnings = plan.Warnings
                        .Append($"Another organisation plan uses the same destination: {duplicate.Key}")
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                };
            }
        }
    }

    private static string CreateDestinationFileName(
        AudiobookOrganisationProposal proposal,
        int sequence,
        int operationCount,
        string extension)
    {
        var fileName = proposal.SuggestedFileNamePattern.Replace(
            "{original extension}",
            extension,
            StringComparison.OrdinalIgnoreCase);
        return operationCount <= 1
            ? fileName
            : LeadingSequenceRegex().Replace(fileName, $"{sequence:000}", 1);
    }

    private static bool TryValidateRelativeDestination(
        string libraryRoot,
        string destination,
        out string warning)
    {
        if (Path.IsPathRooted(destination) || destination.Length > 2048)
        {
            warning = "The proposed destination is not a valid relative library path.";
            return false;
        }

        try
        {
            var root = Path.GetFullPath(libraryRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullDestination = Path.GetFullPath(Path.Combine(root, destination));
            var rootPrefix = root + Path.DirectorySeparatorChar;
            if (!fullDestination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                warning = "The proposed destination escapes the selected library folder.";
                return false;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            warning = "The proposed destination contains an invalid path component.";
            return false;
        }

        warning = string.Empty;
        return true;
    }

    private static AudiobookFileOperationKind SelectOperationKind(
        string source,
        string destination,
        bool metadataNeedsUpdate = false)
    {
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            return metadataNeedsUpdate
                ? AudiobookFileOperationKind.UpdateMetadata
                : AudiobookFileOperationKind.NoChange;
        }

        return string.Equals(
            Path.GetDirectoryName(source),
            Path.GetDirectoryName(destination),
            StringComparison.OrdinalIgnoreCase)
            ? AudiobookFileOperationKind.Rename
            : AudiobookFileOperationKind.MoveAndRename;
    }

    private static bool NeedsMetadataUpdate(
        AudiobookCandidatePart part,
        AudiobookOrganisationProposal proposal,
        int sequence,
        int totalCount)
    {
        var metadata = part.Metadata;
        if (!string.IsNullOrWhiteSpace(proposal.CoverUrl) &&
            (!metadata.HasEmbeddedArtwork || !metadata.HasSidecarArtwork) ||
            !MetadataEquals(metadata.Author.Value, proposal.CanonicalAuthor) ||
            !MetadataEquals(metadata.Album.Value, proposal.CanonicalTitle) ||
            !MetadataEquals(metadata.Genre.Value, proposal.GenreCategory) ||
            metadata.TrackNumber != (uint)sequence ||
            metadata.TrackCount != (uint)totalCount ||
            !MetadataEquals(metadata.SeriesName, proposal.SeriesName))
        {
            return true;
        }

        var expectedTitle = totalCount == 1
            ? proposal.CanonicalTitle
            : $"{proposal.CanonicalTitle} - Track {sequence:000}";
        if (!MetadataEquals(metadata.Title.Value, expectedTitle))
        {
            return true;
        }

        return proposal.FirstPublishedYear is not null &&
               metadata.Year != (uint)proposal.FirstPublishedYear.Value;
    }

    private static bool MetadataEquals(string? current, string? expected) =>
        string.Equals(
            current?.Trim(),
            expected?.Trim(),
            StringComparison.OrdinalIgnoreCase);

    private static bool TryGetCompleteTrackOrder(
        IReadOnlyCollection<AudiobookCandidatePart> parts,
        out IReadOnlyDictionary<Guid, int> order)
    {
        if (TryBuildCompleteTrackOrder(parts, TryGetEmbeddedTrackNumber, out order) ||
            TryBuildCompleteTrackOrder(parts, TryGetFilenameTrackNumber, out order) ||
            TryBuildCompleteTrackOrder(parts, TryGetPreviouslyDetectedTrackNumber, out order))
        {
            return true;
        }

        order = new Dictionary<Guid, int>();
        return false;
    }

    private static bool TryBuildCompleteTrackOrder(
        IReadOnlyCollection<AudiobookCandidatePart> parts,
        TryGetTrackNumber tryGetTrackNumber,
        out IReadOnlyDictionary<Guid, int> order)
    {
        var detected = new Dictionary<Guid, int>();
        foreach (var part in parts)
        {
            if (!tryGetTrackNumber(part, out var trackNumber) || trackNumber <= 0)
            {
                order = detected;
                return false;
            }

            detected[part.MediaItem.Id] = trackNumber;
        }

        var complete = detected.Values
            .Order()
            .SequenceEqual(Enumerable.Range(1, parts.Count));
        order = detected;
        return complete;
    }

    private static bool TryGetEmbeddedTrackNumber(AudiobookCandidatePart part, out int trackNumber)
    {
        if (part.Metadata.TrackNumber is > 0 and <= int.MaxValue)
        {
            trackNumber = (int)part.Metadata.TrackNumber.Value;
            return true;
        }

        trackNumber = 0;
        return false;
    }

    private static bool TryGetFilenameTrackNumber(AudiobookCandidatePart part, out int trackNumber)
    {
        trackNumber = 0;
        var stem = Path.GetFileNameWithoutExtension(part.MediaItem.FileName);
        var match = PartNumberRegex().Match(stem);
        if (!match.Success)
        {
            match = ChapterNumberRegex().Match(stem);
        }

        if (!match.Success)
        {
            match = LeadingSequenceNumberRegex().Match(stem);
        }

        if (!match.Success)
        {
            match = TrailingSequenceRegex().Match(stem);
        }

        return match.Success &&
               int.TryParse(match.Groups[1].Value, out trackNumber) &&
               trackNumber > 0;
    }

    private static bool TryGetPreviouslyDetectedTrackNumber(
        AudiobookCandidatePart part,
        out int trackNumber)
    {
        trackNumber = part.Sequence;
        return part.SequenceWasInferred && trackNumber > 0;
    }

    private static AudiobookBatchPlan WithInputSignature(AudiobookBatchPlan plan)
    {
        var value = string.Join(
            '|',
            BatchAlgorithmVersion,
            plan.PlanKey,
            plan.ValidationStatus,
            string.Join(';', plan.Operations.Select(operation =>
                $"{operation.MediaItemId}:{operation.SourceRelativePath}>{operation.DestinationRelativePath}:{operation.Kind}")),
            string.Join(';', plan.Warnings));
        var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        return plan with { InputSignature = signature };
    }

    private static AudiobookBatchDecisionEntry ToDecisionEntry(
        AudiobookBatchPlan plan,
        DateTime updatedAtUtc) =>
        new(plan.PlanKey, plan.InputSignature, plan.Decision, updatedAtUtc);

    [GeneratedRegex(@"^\d{3}(?=\s*[-–—])")]
    private static partial Regex LeadingSequenceRegex();

    [GeneratedRegex(@"(?i)(?:^|[\s._-])(?:part|pt|cd|disc|disk)[\s._-]*(\d{1,4})(?:$|[\s._-])")]
    private static partial Regex PartNumberRegex();

    [GeneratedRegex(@"(?i)(?:^|[\s._-])(?:chapter|ch)[\s._-]*(\d{1,4})(?:$|[\s._-])")]
    private static partial Regex ChapterNumberRegex();

    [GeneratedRegex(@"^\s*(\d{1,3})(?=[\s._-])")]
    private static partial Regex LeadingSequenceNumberRegex();

    [GeneratedRegex(@"\s[-–—]\s*(\d{1,3})\s*$")]
    private static partial Regex TrailingSequenceRegex();

    private delegate bool TryGetTrackNumber(AudiobookCandidatePart part, out int trackNumber);
}
