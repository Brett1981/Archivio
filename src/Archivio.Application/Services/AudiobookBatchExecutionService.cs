using Archivio.Application.Abstractions;
using System.Text.Json;

namespace Archivio.Application.Services;

public sealed class AudiobookBatchExecutionService(
    IAudiobookExecutionJournalStore journalStore,
    IAudiobookFileOperator fileOperator,
    IAudiobookMetadataWriter metadataWriter,
    IAudiobookCoverArtworkProvider coverArtworkProvider,
    IDatabaseBackupService databaseBackupService) : IAudiobookBatchExecutionService
{
    public Task<AudiobookExecutionResult> ExecuteApprovedAsync(
        Guid librarySourceId,
        string libraryRoot,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        IProgress<AudiobookExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteApprovedAsync(
            librarySourceId,
            libraryRoot,
            libraryRoot,
            candidates,
            progress,
            cancellationToken);

    public async Task<AudiobookExecutionResult> ExecuteApprovedAsync(
        Guid librarySourceId,
        string sourceRoot,
        string destinationRoot,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        IProgress<AudiobookExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentNullException.ThrowIfNull(candidates);
        var sourceBase = NormalizeRoot(sourceRoot);
        var destinationBase = NormalizeRoot(destinationRoot);

        var latest = await journalStore.LoadLatestAsync(librarySourceId, cancellationToken);
        if (latest?.Status is AudiobookExecutionRunStatus.Prepared or
            AudiobookExecutionRunStatus.Running or
            AudiobookExecutionRunStatus.FailedNeedsRecovery or
            AudiobookExecutionRunStatus.CompletedNeedsRecovery)
        {
            throw new InvalidOperationException(
                "A previous execution was interrupted. Recover that journal before starting another run.");
        }

        var preparation = await PrepareOperationsAsync(
            sourceBase,
            destinationBase,
            candidates,
            latest,
            cancellationToken);
        var prepared = preparation.Operations;
        if (prepared.Count == 0 && preparation.FailedOperations.Count == 0)
        {
            if (preparation.AlreadyCompletedCount > 0)
            {
                return new AudiobookExecutionResult(
                    latest?.Id,
                    AudiobookExecutionRunStatus.Completed,
                    preparation.AlreadyCompletedCount,
                    preparation.AlreadyCompletedCount,
                    0,
                    $"Execution was already complete: {preparation.AlreadyCompletedCount:N0} approved file{(preparation.AlreadyCompletedCount == 1 ? string.Empty : "s")} are at their recorded destinations.");
            }

            return new AudiobookExecutionResult(
                null, null, 0, 0, 0,
                "No approved file operations are ready to execute.");
        }

        if (prepared.Count > 0)
        {
            await databaseBackupService.CreateBackupAsync(
                "before-audiobook-execution",
                cancellationToken);
        }

        var now = DateTime.UtcNow;
        var journalOperations = prepared
            .Select(item => item.Entry)
            .Concat(preparation.FailedOperations)
            .OrderBy(operation => operation.SortOrder)
            .ToList();
        var run = new AudiobookExecutionRunEntry(
            Guid.NewGuid(),
            librarySourceId,
            AudiobookExecutionRunStatus.Prepared,
            journalOperations.Count,
            0,
            0,
            now,
            now,
            null,
            null,
            journalOperations,
            sourceBase,
            destinationBase);
        await journalStore.CreateAsync(run, cancellationToken);
        await journalStore.UpdateRunAsync(
            run.Id,
            AudiobookExecutionRunStatus.Running,
            0,
            0,
            BuildOperationFailureSummary(
                preparation.FailedOperations.Select(ToOperationFailureMessage).ToList()),
            DateTime.UtcNow,
            null,
            cancellationToken);

        var completed = new List<PreparedOperation>();
        var failedOperationMessages = preparation.FailedOperations
            .Select(ToOperationFailureMessage)
            .ToList();
        var needsAttentionOperationIds = new HashSet<Guid>();
        var processedOperationCount = preparation.FailedOperations.Count;
        PreparedOperation? current = null;
        try
        {
            for (var index = 0; index < prepared.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureLibraryRootsAvailable(sourceBase, destinationBase);
                current = prepared[index];
                processedOperationCount = preparation.FailedOperations.Count + index + 1;
                progress?.Report(new AudiobookExecutionProgress(
                    preparation.FailedOperations.Count + index,
                    run.PlannedOperationCount,
                    current.Entry.SourceRelativePath,
                    $"Updating metadata and organising file {preparation.FailedOperations.Count + index + 1:N0} of {run.PlannedOperationCount:N0}"));
                await journalStore.UpdateOperationAsync(
                    current.Entry.Id,
                    AudiobookExecutionOperationStatus.Running,
                    null,
                    cancellationToken);

                try
                {
                    await ExecuteFileOperationAsync(
                        current,
                        sourceBase,
                        destinationBase,
                        cancellationToken);
                }
                catch (Exception exception) when (
                    !cancellationToken.IsCancellationRequested &&
                    IsIndividualFileFailure(exception))
                {
                    var resolution = ReconcileOperationFailure(
                        current,
                        exception,
                        sourceBase,
                        destinationBase);
                    if (resolution.OperationCompleted)
                    {
                        completed.Add(current);
                        await journalStore.UpdateOperationAsync(
                            current.Entry.Id,
                            AudiobookExecutionOperationStatus.Completed,
                            LimitJournalMessage(resolution.Message),
                            CancellationToken.None);
                        await journalStore.UpdateRunAsync(
                            run.Id,
                            AudiobookExecutionRunStatus.Running,
                            completed.Count,
                            0,
                            BuildOperationFailureSummary(failedOperationMessages),
                            DateTime.UtcNow,
                            null,
                            CancellationToken.None);
                        progress?.Report(new AudiobookExecutionProgress(
                            preparation.FailedOperations.Count + index + 1,
                            run.PlannedOperationCount,
                            current.Entry.DestinationRelativePath,
                            $"Processed {preparation.FailedOperations.Count + index + 1:N0} of {run.PlannedOperationCount:N0} files: the completed destination was verified after a file-system warning"));
                        current = null;
                        continue;
                    }

                    var failureMessage = resolution.Message;
                    failedOperationMessages.Add(
                        $"{current.Entry.SourceRelativePath}: {failureMessage}");
                    if (resolution.NeedsAttention)
                    {
                        needsAttentionOperationIds.Add(current.Entry.Id);
                    }

                    await journalStore.UpdateOperationAsync(
                        current.Entry.Id,
                        resolution.FailureStatus,
                        LimitJournalMessage(failureMessage),
                        CancellationToken.None);
                    await journalStore.UpdateRunAsync(
                        run.Id,
                        AudiobookExecutionRunStatus.Running,
                        completed.Count,
                        0,
                        BuildOperationFailureSummary(failedOperationMessages),
                        DateTime.UtcNow,
                        null,
                        CancellationToken.None);
                    progress?.Report(new AudiobookExecutionProgress(
                        preparation.FailedOperations.Count + index + 1,
                        run.PlannedOperationCount,
                        current.Entry.SourceRelativePath,
                        $"Skipped {failedOperationMessages.Count:N0} file{(failedOperationMessages.Count == 1 ? string.Empty : "s")} that could not be processed; continuing with the remaining files"));
                    current = null;
                    continue;
                }

                completed.Add(current);

                await journalStore.UpdateOperationAsync(
                    current.Entry.Id,
                    AudiobookExecutionOperationStatus.Completed,
                    null,
                    CancellationToken.None);
                await journalStore.UpdateRunAsync(
                    run.Id,
                    AudiobookExecutionRunStatus.Running,
                    completed.Count,
                    0,
                    BuildOperationFailureSummary(failedOperationMessages),
                    DateTime.UtcNow,
                    null,
                    CancellationToken.None);
                progress?.Report(new AudiobookExecutionProgress(
                    preparation.FailedOperations.Count + index + 1,
                    run.PlannedOperationCount,
                    current.Entry.DestinationRelativePath,
                    failedOperationMessages.Count == 0
                        ? $"Updated and organised {completed.Count:N0} of {run.PlannedOperationCount:N0} files"
                        : $"Processed {preparation.FailedOperations.Count + index + 1:N0} of {run.PlannedOperationCount:N0} files: {completed.Count:N0} organised, {failedOperationMessages.Count:N0} skipped"));
                current = null;
            }

            var completedAtUtc = DateTime.UtcNow;
            var needsAttentionCount = needsAttentionOperationIds.Count;
            var finalStatus = needsAttentionCount == 0
                ? AudiobookExecutionRunStatus.Completed
                : AudiobookExecutionRunStatus.CompletedNeedsRecovery;
            await journalStore.UpdateRunAsync(
                run.Id,
                finalStatus,
                completed.Count,
                0,
                BuildOperationFailureSummary(failedOperationMessages),
                completedAtUtc,
                completedAtUtc,
                CancellationToken.None);
            var sidecars = WriteCoverSidecars(completed);
            var artworkMessage = BuildArtworkResultMessage(preparation.UnavailableCoverCount, sidecars);
            var skippedMessage = BuildSkippedResultMessage(
                failedOperationMessages.Count,
                needsAttentionCount);
            var completionMessage = preparation.AlreadyCompletedCount == 0
                ? $"{BuildCompletionMessage(completed.Count, needsAttentionCount)}{skippedMessage}{artworkMessage}"
                : $"{BuildCompletionMessage(completed.Count, needsAttentionCount)} {preparation.AlreadyCompletedCount:N0} had already reached their recorded destinations.{skippedMessage}{artworkMessage}";
            progress?.Report(new AudiobookExecutionProgress(
                run.PlannedOperationCount,
                run.PlannedOperationCount,
                failedOperationMessages.Count == 0
                    ? completed.LastOrDefault()?.Entry.DestinationRelativePath ?? string.Empty
                    : preparation.FailedOperations.LastOrDefault()?.SourceRelativePath ??
                      completed.LastOrDefault()?.Entry.DestinationRelativePath ??
                      string.Empty,
                completionMessage));
            return new AudiobookExecutionResult(
                run.Id,
                finalStatus,
                run.PlannedOperationCount + preparation.AlreadyCompletedCount,
                completed.Count + preparation.AlreadyCompletedCount,
                0,
                completionMessage);
        }
        catch (Exception exception)
        {
            var cancelled = exception is OperationCanceledException ||
                            cancellationToken.IsCancellationRequested;
            string? currentRecoveryDetail = null;
            if (current is not null && !completed.Contains(current))
            {
                OperationFailureResolution currentResolution;
                try
                {
                    currentResolution = ReconcileOperationFailure(
                        current,
                        exception,
                        sourceBase,
                        destinationBase);
                }
                catch (Exception reconciliationException)
                {
                    currentResolution = new OperationFailureResolution(
                        false,
                        AudiobookExecutionOperationStatus.NeedsAttention,
                        $"{exception.Message} The current file could not be returned to a verified state: {reconciliationException.Message}");
                }

                if (currentResolution.OperationCompleted)
                {
                    completed.Add(current);
                    var currentJournalUpdated = await TryUpdateOperationAsync(
                        current.Entry.Id,
                        AudiobookExecutionOperationStatus.Completed,
                        LimitJournalMessage(currentResolution.Message));
                    if (!currentJournalUpdated)
                    {
                        needsAttentionOperationIds.Add(current.Entry.Id);
                        currentRecoveryDetail =
                            $"The journal could not record the verified state of '{current.Entry.SourceRelativePath}'.";
                    }
                }
                else
                {
                    if (currentResolution.NeedsAttention)
                    {
                        needsAttentionOperationIds.Add(current.Entry.Id);
                        currentRecoveryDetail = currentResolution.Message;
                    }

                    var currentJournalUpdated = await TryUpdateOperationAsync(
                        current.Entry.Id,
                        currentResolution.FailureStatus,
                        LimitJournalMessage(currentResolution.Message));
                    if (!currentJournalUpdated)
                    {
                        needsAttentionOperationIds.Add(current.Entry.Id);
                        currentRecoveryDetail ??=
                            $"The journal could not record the recovery state of '{current.Entry.SourceRelativePath}'.";
                    }
                }
            }

            var rollback = await RollBackAsync(
                completed,
                progress,
                processedOperationCount,
                run.PlannedOperationCount,
                sourceBase,
                destinationBase);
            var unresolvedCount = needsAttentionOperationIds.Count + rollback.FailedCount;
            var status = unresolvedCount > 0
                ? AudiobookExecutionRunStatus.FailedNeedsRecovery
                : cancelled
                    ? AudiobookExecutionRunStatus.CancelledRolledBack
                    : AudiobookExecutionRunStatus.FailedRolledBack;
            var message = unresolvedCount > 0
                ? $"Execution stopped and {unresolvedCount:N0} file operation{(unresolvedCount == 1 ? string.Empty : "s")} could not be recovered automatically."
                : cancelled
                    ? $"Execution cancelled; {rollback.RolledBackCount:N0} completed move{(rollback.RolledBackCount == 1 ? string.Empty : "s")} rolled back."
                    : $"Execution stopped safely; {rollback.RolledBackCount:N0} completed move{(rollback.RolledBackCount == 1 ? string.Empty : "s")} rolled back.";
            if (!string.IsNullOrWhiteSpace(currentRecoveryDetail))
            {
                message += $" {currentRecoveryDetail}";
            }

            var errorMessage = $"{exception.Message} {message}";
            var journalUpdated = await TryUpdateRunAsync(
                run.Id,
                status,
                completed.Count,
                rollback.RolledBackCount,
                errorMessage,
                DateTime.UtcNow,
                DateTime.UtcNow);
            if (!journalUpdated)
            {
                status = AudiobookExecutionRunStatus.FailedNeedsRecovery;
                message += " The execution journal could not record the final recovery state; reopen this source and use Recover interrupted.";
            }

            return new AudiobookExecutionResult(
                run.Id,
                status,
                run.PlannedOperationCount + preparation.AlreadyCompletedCount,
                completed.Count + preparation.AlreadyCompletedCount,
                rollback.RolledBackCount,
                $"{message} {exception.Message}");
        }
    }

    public async Task<AudiobookExecutionResult> RecoverInterruptedAsync(
        Guid librarySourceId,
        string sourceRoot,
        string destinationRoot,
        IProgress<AudiobookExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        var run = await journalStore.LoadLatestAsync(librarySourceId, cancellationToken);
        if (run?.Status is not (AudiobookExecutionRunStatus.Prepared or
            AudiobookExecutionRunStatus.Running or
            AudiobookExecutionRunStatus.FailedNeedsRecovery or
            AudiobookExecutionRunStatus.CompletedNeedsRecovery))
        {
            return new AudiobookExecutionResult(
                run?.Id, run?.Status, run?.PlannedOperationCount ?? 0,
                run?.CompletedOperationCount ?? 0, run?.RolledBackOperationCount ?? 0,
                "There is no interrupted execution to recover.");
        }

        var sourceBase = NormalizeRoot(run.SourceRoot ?? sourceRoot);
        var destinationBase = NormalizeRoot(run.DestinationRoot ?? destinationRoot);
        var rolledBack = 0;
        var failed = 0;
        var manualReviewOperations = new List<AudiobookExecutionOperationEntry>();
        var completedRunNeedsRecovery =
            run.Status == AudiobookExecutionRunStatus.CompletedNeedsRecovery;
        var operations = run.Operations
            .Where(operation => completedRunNeedsRecovery
                ? operation.Status is
                    AudiobookExecutionOperationStatus.RollbackFailed or
                    AudiobookExecutionOperationStatus.NeedsAttention
                : operation.Status is
                    AudiobookExecutionOperationStatus.Running or
                    AudiobookExecutionOperationStatus.Completed or
                    AudiobookExecutionOperationStatus.RollbackFailed or
                    AudiobookExecutionOperationStatus.NeedsAttention)
            .OrderByDescending(operation => operation.SortOrder)
            .ToList();
        for (var index = 0; index < operations.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = operations[index];
            var source = ResolveInsideRoot(sourceBase, operation.SourceRelativePath, "source");
            var destination = ResolveInsideRoot(
                destinationBase,
                operation.DestinationRelativePath,
                "destination");
            progress?.Report(new AudiobookExecutionProgress(
                index,
                operations.Count,
                operation.SourceRelativePath,
                $"Recovering operation {index + 1:N0} of {operations.Count:N0}"));
            var recoveryFailureStatus = operation.Status is
                AudiobookExecutionOperationStatus.NeedsAttention or
                AudiobookExecutionOperationStatus.Running
                ? AudiobookExecutionOperationStatus.NeedsAttention
                : AudiobookExecutionOperationStatus.RollbackFailed;
            try
            {
                EnsureLibraryRootsAvailable(sourceBase, destinationBase);
                var sourceExists = FileExistsWithinAvailableRoot(
                    source,
                    sourceBase,
                    "source");
                var destinationExists = FileExistsWithinAvailableRoot(
                    destination,
                    destinationBase,
                    "destination");
                if (operation.Status == AudiobookExecutionOperationStatus.NeedsAttention)
                {
                    if (operation.Kind == AudiobookFileOperationKind.UpdateMetadata)
                    {
                        if (!sourceExists)
                        {
                            throw new FileNotFoundException(
                                "The file recorded for metadata recovery is unavailable.",
                                source);
                        }
                    }
                    else if (!sourceExists || destinationExists)
                    {
                        var state = sourceExists
                            ? "both recorded paths exist"
                            : destinationExists
                                ? "only the unverified destination exists"
                                : "neither recorded path exists";
                        throw new InvalidOperationException(
                            $"Manual review is still required because {state}. Metaroq will not move an unverified destination automatically.");
                    }

                    recoveryFailureStatus = AudiobookExecutionOperationStatus.RollbackFailed;
                    RestoreJournalledMetadata(operation, source);
                    rolledBack++;
                }
                else if (operation.Kind == AudiobookFileOperationKind.UpdateMetadata)
                {
                    if (!sourceExists)
                    {
                        recoveryFailureStatus = AudiobookExecutionOperationStatus.NeedsAttention;
                        throw new FileNotFoundException(
                            "The file recorded for metadata recovery is unavailable.",
                            source);
                    }

                    recoveryFailureStatus = AudiobookExecutionOperationStatus.RollbackFailed;
                    RestoreJournalledMetadata(operation, source);
                    rolledBack++;
                }
                else if (destinationExists && !sourceExists)
                {
                    if (completedRunNeedsRecovery ||
                        operation.Status == AudiobookExecutionOperationStatus.Running)
                    {
                        recoveryFailureStatus = AudiobookExecutionOperationStatus.NeedsAttention;
                        throw new IOException(
                            "The destination cannot be moved automatically because the operation was not journalled as verified complete.");
                    }

                    var sourceDirectory = Path.GetDirectoryName(source);
                    if (string.IsNullOrWhiteSpace(sourceDirectory))
                    {
                        throw new InvalidOperationException("The original source folder could not be resolved.");
                    }

                    fileOperator.CreateDirectory(sourceDirectory);
                    fileOperator.Move(destination, source);
                    RestoreJournalledMetadata(operation, source);
                    rolledBack++;
                }
                else if (sourceExists && !destinationExists)
                {
                    recoveryFailureStatus = AudiobookExecutionOperationStatus.RollbackFailed;
                    RestoreJournalledMetadata(operation, source);
                    rolledBack++;
                }
                else
                {
                    recoveryFailureStatus = AudiobookExecutionOperationStatus.NeedsAttention;
                    throw new IOException(sourceExists
                        ? $"Recovery will not overwrite either existing path for {operation.SourceRelativePath}."
                        : $"Neither recorded path exists for {operation.SourceRelativePath}.");
                }

                if (!await TryUpdateOperationAsync(
                        operation.Id,
                        completedRunNeedsRecovery
                            ? AudiobookExecutionOperationStatus.Failed
                            : AudiobookExecutionOperationStatus.RolledBack,
                        null))
                {
                    failed++;
                }
            }
            catch (Exception exception)
            {
                failed++;
                if (recoveryFailureStatus == AudiobookExecutionOperationStatus.NeedsAttention)
                {
                    manualReviewOperations.Add(operation);
                }

                await TryUpdateOperationAsync(
                    operation.Id,
                    recoveryFailureStatus,
                    exception.Message);
            }
        }

        var status = completedRunNeedsRecovery
            ? failed == 0
                ? AudiobookExecutionRunStatus.Completed
                : AudiobookExecutionRunStatus.CompletedNeedsRecovery
            : failed == 0
                ? AudiobookExecutionRunStatus.FailedRolledBack
                : AudiobookExecutionRunStatus.FailedNeedsRecovery;
        var message = completedRunNeedsRecovery
            ? failed == 0
                ? $"Skipped-file recovery completed; {rolledBack:N0} file operation{(rolledBack == 1 ? string.Empty : "s")} restored to its original state. Successful files were kept at their destinations."
                : $"Recovery still needs attention: {failed:N0} skipped file{(failed == 1 ? string.Empty : "s")} could not be restored automatically. Successful files were kept at their destinations."
            : failed == 0
                ? $"Interrupted execution recovered safely; {rolledBack:N0} file operation{(rolledBack == 1 ? string.Empty : "s")} restored, including original metadata."
                : $"Recovery needs attention: {failed:N0} operation{(failed == 1 ? string.Empty : "s")} could not be restored automatically.";
        if (manualReviewOperations.Count > 0)
        {
            var first = manualReviewOperations[0];
            message +=
                $" Manual review is required for '{first.SourceRelativePath}' -> '{first.DestinationRelativePath}'. Check both locations, leave the correct file at the source path with the destination path clear, then use Recover interrupted again.";
            if (manualReviewOperations.Count > 1)
            {
                message += $" {manualReviewOperations.Count - 1:N0} more operation{(manualReviewOperations.Count == 2 ? string.Empty : "s")} also need manual review.";
            }
        }

        await journalStore.UpdateRunAsync(
            run.Id,
            status,
            run.CompletedOperationCount,
            rolledBack,
            message,
            DateTime.UtcNow,
            DateTime.UtcNow,
            CancellationToken.None);
        return new AudiobookExecutionResult(
            run.Id,
            status,
            run.PlannedOperationCount,
            run.CompletedOperationCount,
            rolledBack,
            message);
    }

    public Task<AudiobookExecutionResult> RecoverInterruptedAsync(
        Guid librarySourceId,
        string libraryRoot,
        IProgress<AudiobookExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        RecoverInterruptedAsync(
            librarySourceId,
            libraryRoot,
            libraryRoot,
            progress,
            cancellationToken);

    public Task<AudiobookExecutionRunEntry?> LoadLatestAsync(
        Guid librarySourceId,
        CancellationToken cancellationToken = default) =>
        journalStore.LoadLatestAsync(librarySourceId, cancellationToken);

    private async Task<PreparationResult> PrepareOperationsAsync(
        string sourceRoot,
        string destinationRoot,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        AudiobookExecutionRunEntry? latestRun,
        CancellationToken cancellationToken)
    {
        var sourceBase = NormalizeRoot(sourceRoot);
        var destinationBase = NormalizeRoot(destinationRoot);
        EnsureLibraryRootsAvailable(sourceBase, destinationBase);

        var approvedPlans = candidates
            .Select(candidate => candidate.BatchPlan)
            .Where(plan => plan is not null)
            .Select(plan => plan!)
            .GroupBy(plan => plan.PlanKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .Where(plan => plan.Decision == AudiobookBatchDecision.Approved)
            .ToList();
        var invalidPlan = approvedPlans.FirstOrDefault(plan => plan.ValidationStatus is not (
            AudiobookBatchValidationStatus.Ready or AudiobookBatchValidationStatus.NoChange));
        if (invalidPlan is not null)
        {
            throw new InvalidOperationException(
                $"Approved plan '{invalidPlan.CanonicalDisplay}' no longer passes the batch safety checks " +
                $"({invalidPlan.ValidationLabel.ToLowerInvariant()}). Review or reset that plan before executing again.");
        }

        // Approved no-change plans are completed decisions, not executable file operations.
        var plans = approvedPlans
            .Where(plan => plan.ValidationStatus == AudiobookBatchValidationStatus.Ready)
            .ToList();

        var operations = plans
            .SelectMany(plan => plan.Operations
                .Where(operation => operation.Kind != AudiobookFileOperationKind.NoChange)
                .Select(operation => (Plan: plan, Operation: operation)))
            .ToList();
        var duplicateSource = operations
            .GroupBy(item => item.Operation.SourceRelativePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateSource is not null)
        {
            throw new InvalidOperationException($"More than one operation uses source: {duplicateSource.Key}");
        }

        var duplicateDestination = operations
            .GroupBy(item => item.Operation.DestinationRelativePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateDestination is not null)
        {
            throw new InvalidOperationException($"More than one operation uses destination: {duplicateDestination.Key}");
        }

        var completedJournalOperations = latestRun?.Status == AudiobookExecutionRunStatus.Completed
            ? latestRun.Operations
                .Where(operation => operation.Status == AudiobookExecutionOperationStatus.Completed)
                .ToList()
            : [];
        var prepared = new List<PreparedOperation>(operations.Count);
        var failedOperations = new List<AudiobookExecutionOperationEntry>();
        var alreadyCompletedCount = 0;
        var artworkCache = new Dictionary<string, AudiobookArtwork?>(StringComparer.OrdinalIgnoreCase);
        var unavailableCoverUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var partsByMediaItemId = candidates
            .SelectMany(candidate => candidate.Parts)
            .GroupBy(part => part.MediaItem.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var proposalsByPlanKey = candidates
            .Where(candidate => candidate.OrganisationProposal is not null && candidate.BatchPlan is not null)
            .GroupBy(candidate => candidate.BatchPlan!.PlanKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(candidate => candidate.OrganisationProposal!).First(),
                StringComparer.Ordinal);
        for (var index = 0; index < operations.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureLibraryRootsAvailable(sourceBase, destinationBase);
            var item = operations[index];
            if (string.IsNullOrWhiteSpace(item.Plan.InputSignature))
            {
                throw new InvalidOperationException($"Plan {item.Plan.CanonicalDisplay} has no safety signature.");
            }

            var source = ResolveInsideRoot(sourceBase, item.Operation.SourceRelativePath, "source");
            var destination = ResolveInsideRoot(
                destinationBase,
                item.Operation.DestinationRelativePath,
                "destination");
            if (!partsByMediaItemId.TryGetValue(item.Operation.MediaItemId, out var part) ||
                !proposalsByPlanKey.TryGetValue(item.Plan.PlanKey, out var proposal))
            {
                throw new InvalidOperationException(
                    $"The approved metadata plan could not be resolved for {item.Operation.SourceRelativePath}.");
            }

            var planOperations = item.Plan.Operations
                .Where(operation => operation.Kind != AudiobookFileOperationKind.NoChange)
                .ToList();
            var trackIndex = planOperations.FindIndex(operation =>
                operation.MediaItemId == item.Operation.MediaItemId) + 1;
            if (trackIndex <= 0)
            {
                throw new InvalidOperationException(
                    $"The approved track order could not be resolved for {item.Operation.SourceRelativePath}.");
            }

            AudiobookFileSnapshot? snapshot = null;
            AudiobookTagState? originalMetadata = null;
            try
            {
                if (!FileExistsWithinAvailableRoot(source, sourceBase, "source"))
                {
                    var completedOperation = completedJournalOperations.FirstOrDefault(operation =>
                        operation.PlanKey == item.Plan.PlanKey &&
                        operation.InputSignature == item.Plan.InputSignature &&
                        operation.MediaItemId == item.Operation.MediaItemId &&
                        operation.Kind == item.Operation.Kind &&
                        string.Equals(
                            operation.SourceRelativePath,
                            item.Operation.SourceRelativePath,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            operation.DestinationRelativePath,
                            item.Operation.DestinationRelativePath,
                            StringComparison.OrdinalIgnoreCase));
                    if (completedOperation is not null &&
                        FileExistsWithinAvailableRoot(
                            destination,
                            destinationBase,
                            "destination"))
                    {
                        alreadyCompletedCount++;
                        continue;
                    }

                    throw new FileNotFoundException(
                        "An approved source file is no longer available.",
                        source);
                }

                if (item.Operation.Kind != AudiobookFileOperationKind.UpdateMetadata &&
                    FileExistsWithinAvailableRoot(
                        destination,
                        destinationBase,
                        "destination"))
                {
                    throw new IndividualFileOperationException(
                        $"Metaroq will not overwrite the existing destination: {destination}");
                }

                snapshot = fileOperator.GetSnapshot(source);
                originalMetadata = metadataWriter.Read(source);
                var coverArtwork = await TryFetchCoverArtworkAsync(
                    proposal.CoverUrl,
                    artworkCache,
                    unavailableCoverUrls,
                    cancellationToken);
                var desiredMetadata = CreateMetadataUpdate(
                    proposal,
                    part,
                    trackIndex,
                    planOperations.Count,
                    coverArtwork);
                var entry = new AudiobookExecutionOperationEntry(
                    Guid.NewGuid(),
                    index,
                    item.Plan.PlanKey,
                    item.Plan.InputSignature,
                    item.Operation.MediaItemId,
                    item.Operation.SourceRelativePath,
                    item.Operation.DestinationRelativePath,
                    item.Operation.Kind,
                    AudiobookExecutionOperationStatus.Pending,
                    snapshot.SizeBytes,
                    snapshot.ModifiedAtUtc,
                    OriginalMetadataJson: JsonSerializer.Serialize(originalMetadata));
                prepared.Add(new PreparedOperation(
                    entry,
                    source,
                    destination,
                    snapshot,
                    originalMetadata,
                    desiredMetadata));
            }
            catch (Exception exception) when (
                !cancellationToken.IsCancellationRequested &&
                IsIndividualFileFailure(exception))
            {
                var failureMessage = LimitJournalMessage(exception.Message);
                failedOperations.Add(new AudiobookExecutionOperationEntry(
                    Guid.NewGuid(),
                    index,
                    item.Plan.PlanKey,
                    item.Plan.InputSignature,
                    item.Operation.MediaItemId,
                    item.Operation.SourceRelativePath,
                    item.Operation.DestinationRelativePath,
                    item.Operation.Kind,
                    AudiobookExecutionOperationStatus.Failed,
                    snapshot?.SizeBytes ?? 0,
                    snapshot?.ModifiedAtUtc ?? DateTime.UnixEpoch,
                    failureMessage,
                    originalMetadata is null
                        ? null
                        : JsonSerializer.Serialize(originalMetadata)));
            }
        }

        return new PreparationResult(
            prepared,
            failedOperations,
            alreadyCompletedCount,
            unavailableCoverUrls.Count);
    }

    private void Revalidate(
        PreparedOperation operation,
        string sourceRoot,
        string destinationRoot)
    {
        if (!FileExistsWithinAvailableRoot(
                operation.SourceFullPath,
                sourceRoot,
                "source"))
        {
            throw new FileNotFoundException(
                "A source file disappeared after the execution preflight.",
                operation.SourceFullPath);
        }

        if (operation.Entry.Kind != AudiobookFileOperationKind.UpdateMetadata &&
            FileExistsWithinAvailableRoot(
                operation.DestinationFullPath,
                destinationRoot,
                "destination"))
        {
            throw new IndividualFileOperationException(
                $"Metaroq will not overwrite the existing destination: {operation.DestinationFullPath}");
        }

        var current = fileOperator.GetSnapshot(operation.SourceFullPath);
        if (current != operation.Snapshot)
        {
            throw new IndividualFileOperationException(
                $"The source file changed after preflight: {operation.SourceFullPath}");
        }
    }

    private async Task ExecuteFileOperationAsync(
        PreparedOperation operation,
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        Revalidate(operation, sourceRoot, destinationRoot);
        var destinationDirectory = Path.GetDirectoryName(operation.DestinationFullPath);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new InvalidOperationException("The destination folder could not be resolved.");
        }

        fileOperator.CreateDirectory(destinationDirectory);
        operation.MetadataWriteAttempted = true;
        metadataWriter.Write(operation.SourceFullPath, operation.DesiredMetadata);
        operation.WrittenSnapshot = fileOperator.GetSnapshot(operation.SourceFullPath);
        if (operation.Entry.Kind != AudiobookFileOperationKind.UpdateMetadata)
        {
            await MoveWithSharingViolationRetryAsync(
                operation.SourceFullPath,
                operation.DestinationFullPath,
                cancellationToken);
            if (FileExistsWithinAvailableRoot(
                    operation.SourceFullPath,
                    sourceRoot,
                    "source") ||
                !FileExistsWithinAvailableRoot(
                    operation.DestinationFullPath,
                    destinationRoot,
                    "destination") ||
                fileOperator.GetSnapshot(operation.DestinationFullPath) != operation.WrittenSnapshot)
            {
                throw new IndividualFileOperationException(
                    "The move did not reach a verified final state; both source and destination have been preserved for review.");
            }
        }
    }

    private async Task MoveWithSharingViolationRetryAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 3;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                fileOperator.Move(sourcePath, destinationPath);
                return;
            }
            catch (IOException exception) when (
                attempt < maximumAttempts &&
                IsSharingViolation(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), cancellationToken);
            }
        }
    }

    private static bool IsSharingViolation(IOException exception) =>
        (exception.HResult & 0xFFFF) is 32 or 33;

    private OperationFailureResolution ReconcileOperationFailure(
        PreparedOperation operation,
        Exception operationException,
        string sourceRoot,
        string destinationRoot)
    {
        EnsureLibraryRootsAvailable(sourceRoot, destinationRoot);
        var sourceExists = FileExistsWithinAvailableRoot(
            operation.SourceFullPath,
            sourceRoot,
            "source");
        if (operation.Entry.Kind != AudiobookFileOperationKind.UpdateMetadata)
        {
            var destinationExists = FileExistsWithinAvailableRoot(
                operation.DestinationFullPath,
                destinationRoot,
                "destination");
            if (!sourceExists && destinationExists)
            {
                try
                {
                    if (operation.WrittenSnapshot is not null &&
                        fileOperator.GetSnapshot(operation.DestinationFullPath) == operation.WrittenSnapshot)
                    {
                        return new OperationFailureResolution(
                            true,
                            AudiobookExecutionOperationStatus.Failed,
                            $"{operationException.Message} The destination exists, matches the updated source, and the source is absent, so the move was verified as complete.");
                    }
                }
                catch (Exception verificationException) when (
                    IsIndividualFileFailure(verificationException))
                {
                    return new OperationFailureResolution(
                        false,
                        AudiobookExecutionOperationStatus.NeedsAttention,
                        $"{operationException.Message} The destination exists and the source is absent, but the moved file could not be verified: {verificationException.Message}");
                }

                return new OperationFailureResolution(
                    false,
                    AudiobookExecutionOperationStatus.NeedsAttention,
                    $"{operationException.Message} The destination exists and the source is absent, but the moved file does not match the last verified source state.");
            }

            if (!sourceExists || destinationExists)
            {
                var state = sourceExists
                    ? "both source and destination exist"
                    : destinationExists
                        ? "only the destination exists"
                        : "neither source nor destination is currently accessible";
                return new OperationFailureResolution(
                    false,
                    AudiobookExecutionOperationStatus.NeedsAttention,
                    $"{operationException.Message} The file needs attention because {state}.");
            }
        }
        else if (!sourceExists)
        {
            return new OperationFailureResolution(
                false,
                AudiobookExecutionOperationStatus.NeedsAttention,
                $"{operationException.Message} The metadata file is no longer accessible at its recorded path.");
        }

        try
        {
            TryRestoreMetadata(
                operation,
                operation.SourceFullPath,
                throwOnFailure: true);
            return new OperationFailureResolution(
                false,
                AudiobookExecutionOperationStatus.Failed,
                operationException.Message);
        }
        catch (Exception restoreException) when (IsIndividualFileFailure(restoreException))
        {
            return new OperationFailureResolution(
                false,
                AudiobookExecutionOperationStatus.RollbackFailed,
                $"{operationException.Message} Original metadata could not be restored: {restoreException.Message}");
        }
    }

    private static bool IsIndividualFileFailure(Exception exception)
    {
        if (exception is IndividualFileOperationException or
            FileNotFoundException or
            PathTooLongException or
            InvalidDataException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            return true;
        }

        return exception is IOException ioException &&
               (IsSharingViolation(ioException) || IsDestinationAlreadyExists(ioException));
    }

    private static bool IsDestinationAlreadyExists(IOException exception) =>
        (exception.HResult & 0xFFFF) is 80 or 183;

    private static string? BuildOperationFailureSummary(IReadOnlyList<string> failures)
    {
        if (failures.Count == 0)
        {
            return null;
        }

        const int sampleCount = 3;
        var samples = string.Join(" | ", failures.Take(sampleCount));
        var remaining = failures.Count - sampleCount;
        var message = remaining > 0
            ? $"{failures.Count:N0} file operations were skipped. {samples} | and {remaining:N0} more"
            : $"{failures.Count:N0} file operation{(failures.Count == 1 ? string.Empty : "s")} {(failures.Count == 1 ? "was" : "were")} skipped. {samples}";
        return LimitJournalMessage(message);
    }

    private static string ToOperationFailureMessage(
        AudiobookExecutionOperationEntry operation) =>
        $"{operation.SourceRelativePath}: {operation.ErrorMessage ?? "The file could not be prepared."}";

    private static string BuildSkippedResultMessage(
        int failedCount,
        int needsAttentionCount)
    {
        if (failedCount == 0)
        {
            return string.Empty;
        }

        var message =
            $" {failedCount:N0} file{(failedCount == 1 ? string.Empty : "s")} could not be accessed or updated and {(failedCount == 1 ? "was" : "were")} skipped; processing continued and successful files were kept at their destinations.";
        return needsAttentionCount == 0
            ? message
            : $"{message} {needsAttentionCount:N0} skipped file{(needsAttentionCount == 1 ? string.Empty : "s")} could not be returned to a verified original state and {(needsAttentionCount == 1 ? "needs" : "need")} attention.";
    }

    private static string BuildCompletionMessage(
        int completedCount,
        int needsAttentionCount) =>
        needsAttentionCount == 0
            ? $"Execution complete: {completedCount:N0} file{(completedCount == 1 ? string.Empty : "s")} updated and organised safely."
            : $"Execution finished: {completedCount:N0} file{(completedCount == 1 ? string.Empty : "s")} updated and organised.";

    private static string LimitJournalMessage(string message) =>
        message.Length <= 2048 ? message : $"{message[..2045]}...";

    private async Task<RollbackResult> RollBackAsync(
        IReadOnlyList<PreparedOperation> completed,
        IProgress<AudiobookExecutionProgress>? progress,
        int processedOperationCount,
        int plannedOperationCount,
        string sourceRoot,
        string destinationRoot)
    {
        var rolledBack = 0;
        var failed = 0;
        var attempted = 0;
        foreach (var operation in completed.Reverse())
        {
            attempted++;
            try
            {
                if (operation.Entry.Kind == AudiobookFileOperationKind.UpdateMetadata)
                {
                    if (!FileExistsWithinAvailableRoot(
                            operation.SourceFullPath,
                            sourceRoot,
                            "source"))
                    {
                        throw new FileNotFoundException(
                            "The file is unavailable for metadata rollback.",
                            operation.SourceFullPath);
                    }

                    TryRestoreMetadata(operation, operation.SourceFullPath, throwOnFailure: true);
                    rolledBack++;
                }
                else
                {
                    if (FileExistsWithinAvailableRoot(
                            operation.SourceFullPath,
                            sourceRoot,
                            "source"))
                    {
                        throw new IOException($"Rollback would overwrite the restored source: {operation.SourceFullPath}");
                    }

                    if (!FileExistsWithinAvailableRoot(
                            operation.DestinationFullPath,
                            destinationRoot,
                            "destination"))
                    {
                        throw new FileNotFoundException(
                            "The moved destination is unavailable for rollback.",
                            operation.DestinationFullPath);
                    }

                    var sourceDirectory = Path.GetDirectoryName(operation.SourceFullPath);
                    if (string.IsNullOrWhiteSpace(sourceDirectory))
                    {
                        throw new InvalidOperationException("The source folder could not be resolved for rollback.");
                    }

                    fileOperator.CreateDirectory(sourceDirectory);
                    fileOperator.Move(operation.DestinationFullPath, operation.SourceFullPath);
                    TryRestoreMetadata(operation, operation.SourceFullPath, throwOnFailure: true);
                    rolledBack++;
                }

                if (!await TryUpdateOperationAsync(
                        operation.Entry.Id,
                        AudiobookExecutionOperationStatus.RolledBack,
                        null))
                {
                    failed++;
                }
            }
            catch (Exception rollbackException)
            {
                failed++;
                await TryUpdateOperationAsync(
                    operation.Entry.Id,
                    AudiobookExecutionOperationStatus.RollbackFailed,
                    rollbackException.Message);
            }

            progress?.Report(new AudiobookExecutionProgress(
                processedOperationCount,
                plannedOperationCount,
                operation.Entry.SourceRelativePath,
                $"Rollback attempt {attempted:N0} of {completed.Count:N0}: {rolledBack:N0} restored, {failed:N0} need recovery"));
        }

        return new RollbackResult(rolledBack, failed);
    }

    private async Task<bool> TryUpdateOperationAsync(
        Guid operationId,
        AudiobookExecutionOperationStatus status,
        string? errorMessage)
    {
        try
        {
            await journalStore.UpdateOperationAsync(
                operationId,
                status,
                errorMessage,
                CancellationToken.None);
            return true;
        }
        catch
        {
            // The run-level journal update still captures the recovery outcome.
            return false;
        }
    }

    private async Task<bool> TryUpdateRunAsync(
        Guid runId,
        AudiobookExecutionRunStatus status,
        int completedOperationCount,
        int rolledBackOperationCount,
        string? errorMessage,
        DateTime updatedAtUtc,
        DateTime? completedAtUtc)
    {
        try
        {
            await journalStore.UpdateRunAsync(
                runId,
                status,
                completedOperationCount,
                rolledBackOperationCount,
                errorMessage,
                updatedAtUtc,
                completedAtUtc,
                CancellationToken.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeRoot(string root) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    private void EnsureLibraryRootsAvailable(string sourceRoot, string destinationRoot)
    {
        EnsureLibraryRootAvailable(sourceRoot, "source");
        EnsureLibraryRootAvailable(destinationRoot, "destination");
    }

    private bool FileExistsWithinAvailableRoot(
        string path,
        string root,
        string rootRole)
    {
        var exists = fileOperator.FileExists(path);
        if (!exists)
        {
            // File-system existence APIs can report false when a backing drive or
            // network share disappears. Recheck the root before treating the
            // individual path as genuinely absent.
            EnsureLibraryRootAvailable(root, rootRole);
        }

        return exists;
    }

    private void EnsureLibraryRootAvailable(string root, string rootRole)
    {
        if (!fileOperator.DirectoryExists(root))
        {
            throw new DirectoryNotFoundException(
                $"The library {rootRole} folder is unavailable: {root}");
        }
    }

    private static string ResolveInsideRoot(string root, string relativePath, string pathRole)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException($"The {pathRole} path must be relative to the selected library.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootPrefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The {pathRole} path escapes the selected library.");
        }

        return fullPath;
    }

    private static AudiobookTagUpdate CreateMetadataUpdate(
        AudiobookOrganisationProposal proposal,
        AudiobookCandidatePart part,
        int trackNumber,
        int trackCount,
        AudiobookArtwork? coverArtwork)
    {
        var title = trackCount == 1
            ? proposal.CanonicalTitle
            : $"{proposal.CanonicalTitle} - Track {trackNumber:000}";
        var genre = string.Equals(
            proposal.GenreCategory,
            "Uncategorised",
            StringComparison.Ordinal)
            ? null
            : proposal.GenreCategory;
        return new AudiobookTagUpdate(
            title,
            proposal.CanonicalAuthor,
            proposal.CanonicalTitle,
            genre,
            proposal.FirstPublishedYear is null ? null : (uint)proposal.FirstPublishedYear.Value,
            (uint)trackNumber,
            (uint)trackCount,
            proposal.SeriesName,
            coverArtwork);
    }

    private async Task<AudiobookArtwork?> TryFetchCoverArtworkAsync(
        string? coverUrl,
        IDictionary<string, AudiobookArtwork?> cache,
        ISet<string> unavailableCoverUrls,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(coverUrl))
        {
            return null;
        }

        if (!cache.TryGetValue(coverUrl, out var artwork))
        {
            try
            {
                artwork = await coverArtworkProvider.FetchAsync(coverUrl, cancellationToken);
            }
            catch (Exception exception) when (
                !cancellationToken.IsCancellationRequested &&
                exception is HttpRequestException or IOException or TaskCanceledException)
            {
                artwork = null;
            }

            cache[coverUrl] = artwork;
        }

        if (artwork is null)
        {
            unavailableCoverUrls.Add(coverUrl);
        }

        return artwork;
    }

    private CoverSidecarResult WriteCoverSidecars(IReadOnlyList<PreparedOperation> completed)
    {
        var created = 0;
        var existing = 0;
        var failed = 0;
        var folders = completed
            .Where(operation => operation.DesiredMetadata.CoverArtwork is not null)
            .GroupBy(
                operation => Path.GetDirectoryName(operation.DestinationFullPath)!,
                StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            var artwork = folder.First().DesiredMetadata.CoverArtwork!;
            var supportedPaths = new[]
            {
                Path.Combine(folder.Key, "cover.jpg"),
                Path.Combine(folder.Key, "cover.jpeg"),
                Path.Combine(folder.Key, "cover.png")
            };
            try
            {
                if (supportedPaths.Any(fileOperator.FileExists))
                {
                    existing++;
                    continue;
                }

                var path = Path.Combine(
                    folder.Key,
                    artwork.MimeType == "image/png" ? "cover.png" : "cover.jpg");
                fileOperator.WriteAllBytesNew(path, artwork.Data);
                created++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }

        return new CoverSidecarResult(created, existing, failed);
    }

    private static string BuildArtworkResultMessage(
        int unavailableCoverCount,
        CoverSidecarResult sidecars)
    {
        var messages = new List<string>();
        if (sidecars.CreatedCount > 0)
        {
            messages.Add($"{sidecars.CreatedCount:N0} Plex-compatible cover file{(sidecars.CreatedCount == 1 ? string.Empty : "s")} created");
        }

        if (sidecars.ExistingCount > 0)
        {
            messages.Add($"{sidecars.ExistingCount:N0} existing cover file{(sidecars.ExistingCount == 1 ? string.Empty : "s")} preserved");
        }

        var failedCount = unavailableCoverCount + sidecars.FailedCount;
        if (failedCount > 0)
        {
            messages.Add($"{failedCount:N0} cover{(failedCount == 1 ? string.Empty : "s")} could not be added");
        }

        return messages.Count == 0 ? string.Empty : $" Artwork: {string.Join("; ", messages)}.";
    }

    private void TryRestoreMetadata(
        PreparedOperation operation,
        string path,
        bool throwOnFailure = false)
    {
        if (!operation.MetadataWriteAttempted)
        {
            return;
        }

        try
        {
            metadataWriter.Restore(path, operation.OriginalMetadata);
            operation.MetadataWriteAttempted = false;
        }
        catch when (!throwOnFailure)
        {
            // The run-level recovery result reports any move that still needs attention.
        }
    }

    private void RestoreJournalledMetadata(AudiobookExecutionOperationEntry operation, string path)
    {
        if (string.IsNullOrWhiteSpace(operation.OriginalMetadataJson))
        {
            return;
        }

        var originalMetadata = JsonSerializer.Deserialize<AudiobookTagState>(operation.OriginalMetadataJson) ??
                               throw new InvalidOperationException("The original metadata journal is invalid.");
        metadataWriter.Restore(path, originalMetadata);
    }

    private sealed class PreparedOperation(
        AudiobookExecutionOperationEntry entry,
        string sourceFullPath,
        string destinationFullPath,
        AudiobookFileSnapshot snapshot,
        AudiobookTagState originalMetadata,
        AudiobookTagUpdate desiredMetadata)
    {
        public AudiobookExecutionOperationEntry Entry { get; } = entry;
        public string SourceFullPath { get; } = sourceFullPath;
        public string DestinationFullPath { get; } = destinationFullPath;
        public AudiobookFileSnapshot Snapshot { get; } = snapshot;
        public AudiobookTagState OriginalMetadata { get; } = originalMetadata;
        public AudiobookTagUpdate DesiredMetadata { get; } = desiredMetadata;
        public bool MetadataWriteAttempted { get; set; }
        public AudiobookFileSnapshot? WrittenSnapshot { get; set; }
    }

    private sealed record PreparationResult(
        List<PreparedOperation> Operations,
        IReadOnlyList<AudiobookExecutionOperationEntry> FailedOperations,
        int AlreadyCompletedCount,
        int UnavailableCoverCount);

    private sealed record OperationFailureResolution(
        bool OperationCompleted,
        AudiobookExecutionOperationStatus FailureStatus,
        string Message)
    {
        public bool NeedsAttention => FailureStatus is
            AudiobookExecutionOperationStatus.RollbackFailed or
            AudiobookExecutionOperationStatus.NeedsAttention;
    }

    private sealed class IndividualFileOperationException(string message) : IOException(message);

    private sealed record CoverSidecarResult(int CreatedCount, int ExistingCount, int FailedCount);

    private sealed record RollbackResult(int RolledBackCount, int FailedCount);
}
