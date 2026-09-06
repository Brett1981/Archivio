using Archivio.Application.Abstractions;
using System.Text.Json;

namespace Archivio.Application.Services;

public sealed class AudiobookBatchExecutionService(
    IAudiobookExecutionJournalStore journalStore,
    IAudiobookFileOperator fileOperator,
    IAudiobookMetadataWriter metadataWriter,
    IDatabaseBackupService databaseBackupService) : IAudiobookBatchExecutionService
{
    public async Task<AudiobookExecutionResult> ExecuteApprovedAsync(
        Guid librarySourceId,
        string libraryRoot,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        IProgress<AudiobookExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentNullException.ThrowIfNull(candidates);

        var latest = await journalStore.LoadLatestAsync(librarySourceId, cancellationToken);
        if (latest?.Status is AudiobookExecutionRunStatus.Prepared or
            AudiobookExecutionRunStatus.Running or
            AudiobookExecutionRunStatus.FailedNeedsRecovery)
        {
            throw new InvalidOperationException(
                "A previous execution was interrupted. Recover that journal before starting another run.");
        }

        var preparation = PrepareOperations(libraryRoot, candidates, latest);
        var prepared = preparation.Operations;
        if (prepared.Count == 0)
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

        await databaseBackupService.CreateBackupAsync(
            "before-audiobook-execution",
            cancellationToken);

        var now = DateTime.UtcNow;
        var run = new AudiobookExecutionRunEntry(
            Guid.NewGuid(),
            librarySourceId,
            AudiobookExecutionRunStatus.Prepared,
            prepared.Count,
            0,
            0,
            now,
            now,
            null,
            null,
            prepared.Select(item => item.Entry).ToList());
        await journalStore.CreateAsync(run, cancellationToken);
        await journalStore.UpdateRunAsync(
            run.Id,
            AudiobookExecutionRunStatus.Running,
            0,
            0,
            null,
            DateTime.UtcNow,
            null,
            cancellationToken);

        var completed = new List<PreparedOperation>();
        PreparedOperation? current = null;
        try
        {
            for (var index = 0; index < prepared.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                current = prepared[index];
                Revalidate(current);
                progress?.Report(new AudiobookExecutionProgress(
                    index,
                    prepared.Count,
                    current.Entry.SourceRelativePath,
                    $"Updating metadata and organising file {index + 1:N0} of {prepared.Count:N0}"));
                await journalStore.UpdateOperationAsync(
                    current.Entry.Id,
                    AudiobookExecutionOperationStatus.Running,
                    null,
                    cancellationToken);

                var destinationDirectory = Path.GetDirectoryName(current.DestinationFullPath);
                if (string.IsNullOrWhiteSpace(destinationDirectory))
                {
                    throw new InvalidOperationException("The destination folder could not be resolved.");
                }

                fileOperator.CreateDirectory(destinationDirectory);
                current.MetadataWriteAttempted = true;
                metadataWriter.Write(current.SourceFullPath, current.DesiredMetadata);
                if (current.Entry.Kind != AudiobookFileOperationKind.UpdateMetadata)
                {
                    fileOperator.Move(current.SourceFullPath, current.DestinationFullPath);
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
                    null,
                    DateTime.UtcNow,
                    null,
                    CancellationToken.None);
                progress?.Report(new AudiobookExecutionProgress(
                    completed.Count,
                    prepared.Count,
                    current.Entry.DestinationRelativePath,
                    $"Updated and organised {completed.Count:N0} of {prepared.Count:N0} files"));
                current = null;
            }

            var completedAtUtc = DateTime.UtcNow;
            await journalStore.UpdateRunAsync(
                run.Id,
                AudiobookExecutionRunStatus.Completed,
                completed.Count,
                0,
                null,
                completedAtUtc,
                completedAtUtc,
                CancellationToken.None);
            return new AudiobookExecutionResult(
                run.Id,
                AudiobookExecutionRunStatus.Completed,
                prepared.Count + preparation.AlreadyCompletedCount,
                completed.Count + preparation.AlreadyCompletedCount,
                0,
                preparation.AlreadyCompletedCount == 0
                    ? $"Execution complete: {completed.Count:N0} file{(completed.Count == 1 ? string.Empty : "s")} updated and organised safely."
                    : $"Execution complete: {completed.Count:N0} file{(completed.Count == 1 ? string.Empty : "s")} updated and organised safely; {preparation.AlreadyCompletedCount:N0} had already reached their recorded destinations.");
        }
        catch (Exception exception)
        {
            var cancelled = exception is OperationCanceledException;
            if (current is not null && !completed.Contains(current))
            {
                TryRestoreMetadata(current, current.SourceFullPath);
                await TryUpdateOperationAsync(
                    current.Entry.Id,
                    AudiobookExecutionOperationStatus.Failed,
                    exception.Message);
            }

            var rollback = await RollBackAsync(completed, progress);
            var status = rollback.FailedCount > 0
                ? AudiobookExecutionRunStatus.FailedNeedsRecovery
                : cancelled
                    ? AudiobookExecutionRunStatus.CancelledRolledBack
                    : AudiobookExecutionRunStatus.FailedRolledBack;
            var message = rollback.FailedCount > 0
                ? $"Execution stopped and {rollback.FailedCount:N0} move{(rollback.FailedCount == 1 ? string.Empty : "s")} could not be recovered automatically."
                : cancelled
                    ? $"Execution cancelled; {rollback.RolledBackCount:N0} completed move{(rollback.RolledBackCount == 1 ? string.Empty : "s")} rolled back."
                    : $"Execution stopped safely; {rollback.RolledBackCount:N0} completed move{(rollback.RolledBackCount == 1 ? string.Empty : "s")} rolled back.";
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
                prepared.Count,
                completed.Count,
                rollback.RolledBackCount,
                $"{message} {exception.Message}");
        }
    }

    public async Task<AudiobookExecutionResult> RecoverInterruptedAsync(
        Guid librarySourceId,
        string libraryRoot,
        IProgress<AudiobookExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        var run = await journalStore.LoadLatestAsync(librarySourceId, cancellationToken);
        if (run?.Status is not (AudiobookExecutionRunStatus.Prepared or
            AudiobookExecutionRunStatus.Running or
            AudiobookExecutionRunStatus.FailedNeedsRecovery))
        {
            return new AudiobookExecutionResult(
                run?.Id, run?.Status, run?.PlannedOperationCount ?? 0,
                run?.CompletedOperationCount ?? 0, run?.RolledBackOperationCount ?? 0,
                "There is no interrupted execution to recover.");
        }

        var root = Path.GetFullPath(libraryRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rolledBack = 0;
        var failed = 0;
        var operations = run.Operations.OrderByDescending(operation => operation.SortOrder).ToList();
        for (var index = 0; index < operations.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = operations[index];
            var source = ResolveInsideRoot(root, operation.SourceRelativePath, "source");
            var destination = ResolveInsideRoot(root, operation.DestinationRelativePath, "destination");
            progress?.Report(new AudiobookExecutionProgress(
                index,
                operations.Count,
                operation.SourceRelativePath,
                $"Recovering operation {index + 1:N0} of {operations.Count:N0}"));
            try
            {
                var sourceExists = fileOperator.FileExists(source);
                var destinationExists = fileOperator.FileExists(destination);
                if (operation.Kind == AudiobookFileOperationKind.UpdateMetadata)
                {
                    if (!sourceExists)
                    {
                        throw new FileNotFoundException(
                            "The file recorded for metadata recovery is unavailable.",
                            source);
                    }

                    RestoreJournalledMetadata(operation, source);
                    rolledBack++;
                }
                else if (destinationExists && !sourceExists)
                {
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
                    RestoreJournalledMetadata(operation, source);
                }
                else
                {
                    throw new IOException(sourceExists
                        ? $"Recovery will not overwrite either existing path for {operation.SourceRelativePath}."
                        : $"Neither recorded path exists for {operation.SourceRelativePath}.");
                }

                await TryUpdateOperationAsync(
                    operation.Id,
                    AudiobookExecutionOperationStatus.RolledBack,
                    null);
            }
            catch (Exception exception)
            {
                failed++;
                await TryUpdateOperationAsync(
                    operation.Id,
                    AudiobookExecutionOperationStatus.RollbackFailed,
                    exception.Message);
            }
        }

        var status = failed == 0
            ? AudiobookExecutionRunStatus.FailedRolledBack
            : AudiobookExecutionRunStatus.FailedNeedsRecovery;
        var message = failed == 0
            ? $"Interrupted execution recovered safely; {rolledBack:N0} file operation{(rolledBack == 1 ? string.Empty : "s")} restored, including original metadata."
            : $"Recovery needs attention: {failed:N0} operation{(failed == 1 ? string.Empty : "s")} could not be restored automatically.";
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

    public Task<AudiobookExecutionRunEntry?> LoadLatestAsync(
        Guid librarySourceId,
        CancellationToken cancellationToken = default) =>
        journalStore.LoadLatestAsync(librarySourceId, cancellationToken);

    private PreparationResult PrepareOperations(
        string libraryRoot,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        AudiobookExecutionRunEntry? latestRun)
    {
        var root = Path.GetFullPath(libraryRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!fileOperator.DirectoryExists(root))
        {
            throw new DirectoryNotFoundException($"The library folder is unavailable: {root}");
        }

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
        var alreadyCompletedCount = 0;
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
            var item = operations[index];
            if (string.IsNullOrWhiteSpace(item.Plan.InputSignature))
            {
                throw new InvalidOperationException($"Plan {item.Plan.CanonicalDisplay} has no safety signature.");
            }

            var source = ResolveInsideRoot(root, item.Operation.SourceRelativePath, "source");
            var destination = ResolveInsideRoot(root, item.Operation.DestinationRelativePath, "destination");
            if (!fileOperator.FileExists(source))
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
                    fileOperator.FileExists(destination) &&
                    fileOperator.GetSnapshot(destination) == new AudiobookFileSnapshot(
                        completedOperation.SourceSizeBytes,
                        completedOperation.SourceModifiedAtUtc))
                {
                    alreadyCompletedCount++;
                    continue;
                }

                throw new FileNotFoundException("An approved source file is no longer available.", source);
            }

            if (item.Operation.Kind != AudiobookFileOperationKind.UpdateMetadata &&
                fileOperator.FileExists(destination))
            {
                throw new IOException($"Metaroq will not overwrite the existing destination: {destination}");
            }

            var snapshot = fileOperator.GetSnapshot(source);
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

            var originalMetadata = metadataWriter.Read(source);
            var desiredMetadata = CreateMetadataUpdate(
                proposal,
                part,
                trackIndex,
                planOperations.Count);
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

        return new PreparationResult(prepared, alreadyCompletedCount);
    }

    private void Revalidate(PreparedOperation operation)
    {
        if (!fileOperator.FileExists(operation.SourceFullPath))
        {
            throw new FileNotFoundException(
                "A source file disappeared after the execution preflight.",
                operation.SourceFullPath);
        }

        if (operation.Entry.Kind != AudiobookFileOperationKind.UpdateMetadata &&
            fileOperator.FileExists(operation.DestinationFullPath))
        {
            throw new IOException($"Metaroq will not overwrite the existing destination: {operation.DestinationFullPath}");
        }

        var current = fileOperator.GetSnapshot(operation.SourceFullPath);
        if (current != operation.Snapshot)
        {
            throw new IOException($"The source file changed after preflight: {operation.SourceFullPath}");
        }
    }

    private async Task<RollbackResult> RollBackAsync(
        IReadOnlyList<PreparedOperation> completed,
        IProgress<AudiobookExecutionProgress>? progress)
    {
        var rolledBack = 0;
        var failed = 0;
        foreach (var operation in completed.Reverse())
        {
            try
            {
                if (operation.Entry.Kind == AudiobookFileOperationKind.UpdateMetadata)
                {
                    if (!fileOperator.FileExists(operation.SourceFullPath))
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
                    if (fileOperator.FileExists(operation.SourceFullPath))
                    {
                        throw new IOException($"Rollback would overwrite the restored source: {operation.SourceFullPath}");
                    }

                    if (!fileOperator.FileExists(operation.DestinationFullPath))
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

                await TryUpdateOperationAsync(
                    operation.Entry.Id,
                    AudiobookExecutionOperationStatus.RolledBack,
                    null);
                progress?.Report(new AudiobookExecutionProgress(
                    rolledBack,
                    completed.Count,
                    operation.Entry.SourceRelativePath,
                    $"Rolling back {rolledBack:N0} of {completed.Count:N0} completed moves"));
            }
            catch (Exception rollbackException)
            {
                failed++;
                await TryUpdateOperationAsync(
                    operation.Entry.Id,
                    AudiobookExecutionOperationStatus.RollbackFailed,
                    rollbackException.Message);
            }
        }

        return new RollbackResult(rolledBack, failed);
    }

    private async Task TryUpdateOperationAsync(
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
        }
        catch
        {
            // The run-level journal update still captures the recovery outcome.
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

    private static string ResolveInsideRoot(string root, string relativePath, string pathRole)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException($"The {pathRole} path must be relative to the selected library.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootPrefix = root + Path.DirectorySeparatorChar;
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
        int trackCount)
    {
        var title = trackCount == 1
            ? proposal.CanonicalTitle
            : $"{proposal.CanonicalTitle} - Track {trackNumber:000}";
        return new AudiobookTagUpdate(
            title,
            proposal.CanonicalAuthor,
            proposal.CanonicalTitle,
            proposal.GenreCategory,
            proposal.FirstPublishedYear is null ? null : (uint)proposal.FirstPublishedYear.Value,
            (uint)trackNumber,
            (uint)trackCount,
            proposal.SeriesName);
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
    }

    private sealed record PreparationResult(
        List<PreparedOperation> Operations,
        int AlreadyCompletedCount);

    private sealed record RollbackResult(int RolledBackCount, int FailedCount);
}
