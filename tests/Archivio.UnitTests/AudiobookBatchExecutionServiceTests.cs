using Archivio.Application.Abstractions;
using Archivio.Application.Services;

namespace Archivio.UnitTests;

public sealed class AudiobookBatchExecutionServiceTests
{
    [Fact]
    public async Task ExecuteApproved_MovesFileAndCompletesJournal()
    {
        var fixture = CreateFixture(("Incoming\\Book.mp3", "Author\\Book\\Author - Book.mp3"));

        var result = await fixture.Service.ExecuteApprovedAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate]);

        Assert.True(result.Succeeded);
        Assert.False(fixture.Files.FileExists(Path.Combine(fixture.Root, "Incoming\\Book.mp3")));
        Assert.True(fixture.Files.FileExists(Path.Combine(fixture.Root, "Author\\Book\\Author - Book.mp3")));
        Assert.Equal(AudiobookExecutionRunStatus.Completed, fixture.Journal.Latest?.Status);
        Assert.Equal(
            AudiobookExecutionOperationStatus.Completed,
            Assert.Single(fixture.Journal.Latest!.Operations).Status);
        Assert.Equal(1, fixture.Metadata.WriteCount);
        Assert.Equal(1, fixture.Backups.BackupCount);
        Assert.False(string.IsNullOrWhiteSpace(
            Assert.Single(fixture.Journal.Latest.Operations).OriginalMetadataJson));
    }

    [Fact]
    public async Task ExecuteApproved_MovesFileToSeparateDestinationAndJournalsBothRoots()
    {
        var fixture = CreateFixture(("Incoming\\Book.mp3", "Author\\Book\\Author - Book.mp3"));
        var destinationRoot = Path.Combine(
            Path.GetTempPath(),
            "Metaroq.Execution.Destination.Tests",
            Guid.NewGuid().ToString("N"));
        fixture.Files.AddDirectory(destinationRoot);

        var result = await fixture.Service.ExecuteApprovedAsync(
            fixture.SourceId,
            fixture.Root,
            destinationRoot,
            [fixture.Candidate]);

        Assert.True(result.Succeeded);
        Assert.False(fixture.Files.FileExists(Path.Combine(fixture.Root, "Incoming\\Book.mp3")));
        Assert.True(fixture.Files.FileExists(Path.Combine(
            destinationRoot,
            "Author\\Book\\Author - Book.mp3")));
        Assert.Equal(Path.GetFullPath(fixture.Root), fixture.Journal.Latest?.SourceRoot);
        Assert.Equal(Path.GetFullPath(destinationRoot), fixture.Journal.Latest?.DestinationRoot);
    }

    [Fact]
    public async Task ExecuteApproved_IgnoresApprovedNoChangePlans()
    {
        var fixture = CreateFixture(("Incoming\\Book.mp3", "Author\\Book\\Author - Book.mp3"));
        var noChangePlan = fixture.Candidate.BatchPlan! with
        {
            PlanKey = "already-organised",
            CanonicalDisplay = "Author - Existing Book",
            ValidationStatus = AudiobookBatchValidationStatus.NoChange,
            Operations =
            [
                new AudiobookFileOperation(
                    Guid.NewGuid(),
                    "Author\\Existing Book.mp3",
                    "Author\\Existing Book.mp3",
                    AudiobookFileOperationKind.NoChange)
            ]
        };
        var noChangeCandidate = fixture.Candidate with { BatchPlan = noChangePlan };

        var result = await fixture.Service.ExecuteApprovedAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate, noChangeCandidate]);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.CompletedOperationCount);
        Assert.True(fixture.Files.FileExists(Path.Combine(fixture.Root, "Author\\Book\\Author - Book.mp3")));
    }

    [Fact]
    public async Task ExecuteApproved_TreatsMatchingCompletedMoveAsAlreadyApplied()
    {
        var fixture = CreateFixture(("Incoming\\Book.mp3", "Author\\Book\\Author - Book.mp3"));
        var plan = fixture.Candidate.BatchPlan!;
        var plannedOperation = Assert.Single(plan.Operations);
        var source = Path.Combine(fixture.Root, plannedOperation.SourceRelativePath);
        var destination = Path.Combine(fixture.Root, plannedOperation.DestinationRelativePath);
        fixture.Files.Move(source, destination);
        fixture.Files.ResetMoveCount();
        var completedOperation = new AudiobookExecutionOperationEntry(
            Guid.NewGuid(),
            0,
            plan.PlanKey,
            plan.InputSignature,
            plannedOperation.MediaItemId,
            plannedOperation.SourceRelativePath,
            plannedOperation.DestinationRelativePath,
            plannedOperation.Kind,
            AudiobookExecutionOperationStatus.Completed,
            100,
            TestFileOperator.ModifiedAtUtc);
        fixture.Journal.Latest = new AudiobookExecutionRunEntry(
            Guid.NewGuid(),
            fixture.SourceId,
            AudiobookExecutionRunStatus.Completed,
            1,
            1,
            0,
            DateTime.UtcNow,
            DateTime.UtcNow,
            DateTime.UtcNow,
            null,
            [completedOperation]);

        var result = await fixture.Service.ExecuteApprovedAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate]);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.CompletedOperationCount);
        Assert.Contains("already complete", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Files.MoveCount);
        Assert.True(fixture.Files.FileExists(destination));
        Assert.False(fixture.Files.FileExists(source));
    }

    [Fact]
    public async Task ExecuteApproved_SkipsOccupiedDestinationWithoutOverwritingIt()
    {
        var fixture = CreateFixture(("Incoming\\Book.mp3", "Author\\Book\\Author - Book.mp3"));
        fixture.Files.AddFile(Path.Combine(fixture.Root, "Author\\Book\\Author - Book.mp3"));

        var result = await fixture.Service.ExecuteApprovedAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate]);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.CompletedOperationCount);
        Assert.Contains("skipped", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AudiobookExecutionRunStatus.Completed, fixture.Journal.Latest?.Status);
        Assert.Equal(
            AudiobookExecutionOperationStatus.Failed,
            Assert.Single(fixture.Journal.Latest!.Operations).Status);
        Assert.True(fixture.Files.FileExists(Path.Combine(fixture.Root, "Incoming\\Book.mp3")));
        Assert.True(fixture.Files.FileExists(Path.Combine(
            fixture.Root,
            "Author\\Book\\Author - Book.mp3")));
        Assert.Equal(0, fixture.Backups.BackupCount);
    }

    [Fact]
    public async Task ExecuteApproved_SkipsFailedFileAndKeepsProcessingSeparateDestination()
    {
        var fixture = CreateFixture(
            ("Incoming\\Part 1.mp3", "Author\\Book\\001 - Book.mp3"),
            ("Incoming\\Part 2.mp3", "Author\\Book\\002 - Book.mp3"),
            ("Incoming\\Part 3.mp3", "Author\\Book\\003 - Book.mp3"));
        var destinationRoot = Path.Combine(
            Path.GetTempPath(),
            "Metaroq.Execution.Destination.Tests",
            Guid.NewGuid().ToString("N"));
        fixture.Files.AddDirectory(destinationRoot);
        fixture.Files.PersistentSharingViolationSourcePath = Path.Combine(
            fixture.Root,
            "Incoming\\Part 2.mp3");

        var result = await fixture.Service.ExecuteApprovedAsync(
            fixture.SourceId,
            fixture.Root,
            destinationRoot,
            [fixture.Candidate]);

        Assert.True(result.Succeeded);
        Assert.Equal(AudiobookExecutionRunStatus.Completed, result.Status);
        Assert.Equal(2, result.CompletedOperationCount);
        Assert.Equal(0, result.RolledBackOperationCount);
        Assert.False(fixture.Files.FileExists(Path.Combine(fixture.Root, "Incoming\\Part 1.mp3")));
        Assert.True(fixture.Files.FileExists(Path.Combine(fixture.Root, "Incoming\\Part 2.mp3")));
        Assert.False(fixture.Files.FileExists(Path.Combine(fixture.Root, "Incoming\\Part 3.mp3")));
        Assert.True(fixture.Files.FileExists(Path.Combine(destinationRoot, "Author\\Book\\001 - Book.mp3")));
        Assert.False(fixture.Files.FileExists(Path.Combine(destinationRoot, "Author\\Book\\002 - Book.mp3")));
        Assert.True(fixture.Files.FileExists(Path.Combine(destinationRoot, "Author\\Book\\003 - Book.mp3")));
        Assert.Equal(
            [
                AudiobookExecutionOperationStatus.Completed,
                AudiobookExecutionOperationStatus.Failed,
                AudiobookExecutionOperationStatus.Completed
            ],
            fixture.Journal.Latest!.Operations.Select(operation => operation.Status));
        Assert.Contains("1 file", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("skipped", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, fixture.Metadata.WriteCount);
        Assert.Equal(1, fixture.Metadata.RestoreCount);
        Assert.Equal(5, fixture.Files.MoveCount);
    }

    [Fact]
    public async Task ExecuteApproved_SkipsFileThatCannotBeReadDuringPreflight()
    {
        var fixture = CreateFixture(
            ("Incoming\\Part 1.mp3", "Author\\Book\\001 - Book.mp3"),
            ("Incoming\\Part 2.mp3", "Author\\Book\\002 - Book.mp3"),
            ("Incoming\\Part 3.mp3", "Author\\Book\\003 - Book.mp3"));
        var destinationRoot = Path.Combine(
            Path.GetTempPath(),
            "Metaroq.Execution.Destination.Tests",
            Guid.NewGuid().ToString("N"));
        fixture.Files.AddDirectory(destinationRoot);
        fixture.Metadata.FailReadNumber = 2;

        var result = await fixture.Service.ExecuteApprovedAsync(
            fixture.SourceId,
            fixture.Root,
            destinationRoot,
            [fixture.Candidate]);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.CompletedOperationCount);
        Assert.False(fixture.Files.FileExists(Path.Combine(fixture.Root, "Incoming\\Part 1.mp3")));
        Assert.True(fixture.Files.FileExists(Path.Combine(fixture.Root, "Incoming\\Part 2.mp3")));
        Assert.False(fixture.Files.FileExists(Path.Combine(fixture.Root, "Incoming\\Part 3.mp3")));
        Assert.True(fixture.Files.FileExists(Path.Combine(
            destinationRoot,
            "Author\\Book\\001 - Book.mp3")));
        Assert.False(fixture.Files.FileExists(Path.Combine(
            destinationRoot,
            "Author\\Book\\002 - Book.mp3")));
        Assert.True(fixture.Files.FileExists(Path.Combine(
            destinationRoot,
            "Author\\Book\\003 - Book.mp3")));
        Assert.Equal(
            [
                AudiobookExecutionOperationStatus.Completed,
                AudiobookExecutionOperationStatus.Failed,
                AudiobookExecutionOperationStatus.Completed
            ],
            fixture.Journal.Latest!.Operations.Select(operation => operation.Status));
        Assert.Equal(2, fixture.Metadata.WriteCount);
        Assert.Equal(1, fixture.Backups.BackupCount);
    }

    [Fact]
    public async Task ExecuteApproved_RetriesTransientSharingViolationBeforeSkipping()
    {
        var fixture = CreateFixture(("Incoming\\Book.mp3", "Author\\Book\\Author - Book.mp3"));
        fixture.Files.SharingViolationMoveNumber = 1;

        var result = await fixture.Service.ExecuteApprovedAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate]);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.CompletedOperationCount);
        Assert.Equal(2, fixture.Files.MoveCount);
        Assert.Equal(0, fixture.Metadata.RestoreCount);
        Assert.True(fixture.Files.FileExists(Path.Combine(
            fixture.Root,
            "Author\\Book\\Author - Book.mp3")));
        Assert.False(fixture.Files.FileExists(Path.Combine(fixture.Root, "Incoming\\Book.mp3")));
        Assert.Equal(
            AudiobookExecutionOperationStatus.Completed,
            Assert.Single(fixture.Journal.Latest!.Operations).Status);
    }

    [Fact]
    public async Task RecoverInterrupted_RepairsSkippedFileWithoutUndoingSuccessfulFiles()
    {
        var fixture = CreateFixture(
            ("Incoming\\Part 1.mp3", "Author\\Book\\001 - Book.mp3"),
            ("Incoming\\Part 2.mp3", "Author\\Book\\002 - Book.mp3"));
        fixture.Files.PersistentSharingViolationSourcePath = Path.Combine(
            fixture.Root,
            "Incoming\\Part 2.mp3");
        fixture.Metadata.FailRestore = true;

        var execution = await fixture.Service.ExecuteApprovedAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate]);

        Assert.Equal(AudiobookExecutionRunStatus.CompletedNeedsRecovery, execution.Status);
        fixture.Metadata.FailRestore = false;

        var recovery = await fixture.Service.RecoverInterruptedAsync(
            fixture.SourceId,
            fixture.Root);

        Assert.Equal(AudiobookExecutionRunStatus.Completed, recovery.Status);
        Assert.False(recovery.NeedsRecovery);
        Assert.False(fixture.Files.FileExists(Path.Combine(fixture.Root, "Incoming\\Part 1.mp3")));
        Assert.True(fixture.Files.FileExists(Path.Combine(fixture.Root, "Incoming\\Part 2.mp3")));
        Assert.True(fixture.Files.FileExists(Path.Combine(
            fixture.Root,
            "Author\\Book\\001 - Book.mp3")));
        Assert.False(fixture.Files.FileExists(Path.Combine(
            fixture.Root,
            "Author\\Book\\002 - Book.mp3")));
        Assert.Equal(
            [
                AudiobookExecutionOperationStatus.Completed,
                AudiobookExecutionOperationStatus.Failed
            ],
            fixture.Journal.Latest!.Operations.Select(operation => operation.Status));
    }

    [Fact]
    public async Task ExecuteApproved_TreatsMoveThenThrowAsCompletedWhenDestinationIsVerified()
    {
        var fixture = CreateFixture(("Incoming\\Book.mp3", "Author\\Book\\Author - Book.mp3"));
        fixture.Files.MoveThenThrowNumber = 1;

        var result = await fixture.Service.ExecuteApprovedAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate]);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.CompletedOperationCount);
        Assert.False(fixture.Files.FileExists(Path.Combine(fixture.Root, "Incoming\\Book.mp3")));
        Assert.True(fixture.Files.FileExists(Path.Combine(
            fixture.Root,
            "Author\\Book\\Author - Book.mp3")));
        Assert.Equal(
            AudiobookExecutionOperationStatus.Completed,
            Assert.Single(fixture.Journal.Latest!.Operations).Status);
        Assert.Equal(0, fixture.Metadata.RestoreCount);
    }

    [Fact]
    public async Task ExecuteApproved_ReportsAttentionWhenOriginalMetadataCannotBeRestored()
    {
        var fixture = CreateFixture(("Incoming\\Book.mp3", "Author\\Book\\Author - Book.mp3"));
        fixture.Files.PersistentSharingViolationSourcePath = Path.Combine(
            fixture.Root,
            "Incoming\\Book.mp3");
        fixture.Metadata.FailRestore = true;

        var result = await fixture.Service.ExecuteApprovedAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate]);

        Assert.True(result.Succeeded);
        Assert.True(result.NeedsRecovery);
        Assert.Equal(AudiobookExecutionRunStatus.CompletedNeedsRecovery, result.Status);
        Assert.Equal(0, result.CompletedOperationCount);
        Assert.Contains("needs attention", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(fixture.Files.FileExists(Path.Combine(fixture.Root, "Incoming\\Book.mp3")));
        Assert.False(fixture.Files.FileExists(Path.Combine(
            fixture.Root,
            "Author\\Book\\Author - Book.mp3")));
        Assert.Equal(
            AudiobookExecutionOperationStatus.RollbackFailed,
            Assert.Single(fixture.Journal.Latest!.Operations).Status);
    }

    [Fact]
    public async Task ExecuteApproved_CancellationStillRollsBackCompletedMoves()
    {
        var fixture = CreateFixture(
            ("Incoming\\Part 1.mp3", "Author\\Book\\001 - Book.mp3"),
            ("Incoming\\Part 2.mp3", "Author\\Book\\002 - Book.mp3"));
        using var cancellation = new CancellationTokenSource();
        var progress = new SynchronousProgress<AudiobookExecutionProgress>(value =>
        {
            if (value.ProcessedCount == 1)
            {
                cancellation.Cancel();
            }
        });

        var result = await fixture.Service.ExecuteApprovedAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate],
            progress,
            cancellation.Token);

        Assert.Equal(AudiobookExecutionRunStatus.CancelledRolledBack, result.Status);
        Assert.Equal(1, result.RolledBackOperationCount);
        Assert.True(fixture.Files.FileExists(Path.Combine(fixture.Root, "Incoming\\Part 1.mp3")));
        Assert.True(fixture.Files.FileExists(Path.Combine(fixture.Root, "Incoming\\Part 2.mp3")));
        Assert.False(fixture.Files.FileExists(Path.Combine(
            fixture.Root,
            "Author\\Book\\001 - Book.mp3")));
    }

    [Fact]
    public async Task ExecuteApproved_CancellationDuringPartialMoveRequiresAttentionAndKeepsBothCopies()
    {
        var fixture = CreateFixture(
            ("Incoming\\Part 1.mp3", "Author\\Book\\001 - Book.mp3"),
            ("Incoming\\Part 2.mp3", "Author\\Book\\002 - Book.mp3"));
        using var cancellation = new CancellationTokenSource();
        fixture.Files.PartialMoveThenThrowNumber = 2;
        fixture.Files.PartialMoveThenThrowAction = cancellation.Cancel;

        var result = await fixture.Service.ExecuteApprovedAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate],
            cancellationToken: cancellation.Token);

        Assert.Equal(AudiobookExecutionRunStatus.FailedNeedsRecovery, result.Status);
        Assert.True(result.NeedsRecovery);
        Assert.Equal(1, result.RolledBackOperationCount);
        Assert.True(fixture.Files.FileExists(Path.Combine(fixture.Root, "Incoming\\Part 1.mp3")));
        Assert.True(fixture.Files.FileExists(Path.Combine(fixture.Root, "Incoming\\Part 2.mp3")));
        Assert.True(fixture.Files.FileExists(Path.Combine(
            fixture.Root,
            "Author\\Book\\002 - Book.mp3")));
        Assert.Equal(
            [
                AudiobookExecutionOperationStatus.RolledBack,
                AudiobookExecutionOperationStatus.NeedsAttention
            ],
            fixture.Journal.Latest!.Operations.Select(operation => operation.Status));

        var moveCountBeforeRecovery = fixture.Files.MoveCount;
        var recovery = await fixture.Service.RecoverInterruptedAsync(
            fixture.SourceId,
            fixture.Root);

        Assert.Equal(AudiobookExecutionRunStatus.FailedNeedsRecovery, recovery.Status);
        Assert.Equal(moveCountBeforeRecovery, fixture.Files.MoveCount);
        Assert.True(fixture.Files.FileExists(Path.Combine(fixture.Root, "Incoming\\Part 2.mp3")));
        Assert.True(fixture.Files.FileExists(Path.Combine(
            fixture.Root,
            "Author\\Book\\002 - Book.mp3")));
    }

    [Fact]
    public async Task ExecuteApproved_SystemStorageFailureStopsBatchAndRollsBackWorkingFiles()
    {
        var fixture = CreateFixture(
            ("Incoming\\Part 1.mp3", "Author\\Book\\001 - Book.mp3"),
            ("Incoming\\Part 2.mp3", "Author\\Book\\002 - Book.mp3"),
            ("Incoming\\Part 3.mp3", "Author\\Book\\003 - Book.mp3"));
        fixture.Files.FailMoveNumber = 2;

        var result = await fixture.Service.ExecuteApprovedAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate]);

        Assert.Equal(AudiobookExecutionRunStatus.FailedRolledBack, result.Status);
        Assert.False(result.Succeeded);
        Assert.Equal(1, result.RolledBackOperationCount);
        Assert.All(
            Enumerable.Range(1, 3),
            part => Assert.True(fixture.Files.FileExists(Path.Combine(
                fixture.Root,
                $"Incoming\\Part {part}.mp3"))));
        Assert.All(
            Enumerable.Range(1, 3),
            part => Assert.False(fixture.Files.FileExists(Path.Combine(
                fixture.Root,
                $"Author\\Book\\00{part} - Book.mp3"))));
        Assert.Equal(
            [
                AudiobookExecutionOperationStatus.RolledBack,
                AudiobookExecutionOperationStatus.Failed,
                AudiobookExecutionOperationStatus.Pending
            ],
            fixture.Journal.Latest!.Operations.Select(operation => operation.Status));
    }

    [Fact]
    public async Task ExecuteApproved_SystemFailureAfterVerifiedMoveRollsBackCurrentFile()
    {
        var fixture = CreateFixture(("Incoming\\Book.mp3", "Author\\Book\\Author - Book.mp3"));
        fixture.Files.SystemFailureAfterMoveNumber = 1;

        var result = await fixture.Service.ExecuteApprovedAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate]);

        Assert.Equal(AudiobookExecutionRunStatus.FailedRolledBack, result.Status);
        Assert.Equal(1, result.RolledBackOperationCount);
        Assert.True(fixture.Files.FileExists(Path.Combine(fixture.Root, "Incoming\\Book.mp3")));
        Assert.False(fixture.Files.FileExists(Path.Combine(
            fixture.Root,
            "Author\\Book\\Author - Book.mp3")));
        Assert.Equal(
            AudiobookExecutionOperationStatus.RolledBack,
            Assert.Single(fixture.Journal.Latest!.Operations).Status);
    }

    [Fact]
    public async Task ExecuteApproved_RootDisappearingAfterMissingProbeRequiresRecovery()
    {
        var fixture = CreateFixture(("Incoming\\Book.mp3", "Author\\Book\\Author - Book.mp3"));
        fixture.Files.MissingFileAndRemoveRootOnFileExistsNumber = 3;

        var result = await fixture.Service.ExecuteApprovedAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate]);

        Assert.Equal(AudiobookExecutionRunStatus.FailedNeedsRecovery, result.Status);
        Assert.True(result.NeedsRecovery);
        Assert.Equal(
            AudiobookExecutionOperationStatus.NeedsAttention,
            Assert.Single(fixture.Journal.Latest!.Operations).Status);
        Assert.Contains("source folder is unavailable", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteApproved_InaccessibleDestinationProbeIsNeverTreatedAsMissing()
    {
        var fixture = CreateFixture(("Incoming\\Book.mp3", "Author\\Book\\Author - Book.mp3"));
        var destination = Path.Combine(fixture.Root, "Author\\Book\\Author - Book.mp3");
        fixture.Files.FailMoveNumber = 1;
        fixture.Files.BeforeFailMoveAction = () => fixture.Files.FileExistsFailurePath = destination;

        var result = await fixture.Service.ExecuteApprovedAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate]);

        Assert.Equal(AudiobookExecutionRunStatus.FailedNeedsRecovery, result.Status);
        Assert.True(result.NeedsRecovery);
        Assert.Equal(
            AudiobookExecutionOperationStatus.NeedsAttention,
            Assert.Single(fixture.Journal.Latest!.Operations).Status);
        Assert.Contains("inaccessible file state", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteApproved_SystemFailureWithMetadataRestoreFailureRemainsRecoverable()
    {
        var fixture = CreateFixture(("Incoming\\Book.mp3", "Author\\Book\\Author - Book.mp3"));
        fixture.Files.FailMoveNumber = 1;
        fixture.Metadata.FailRestore = true;

        var result = await fixture.Service.ExecuteApprovedAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate]);

        Assert.Equal(AudiobookExecutionRunStatus.FailedNeedsRecovery, result.Status);
        Assert.True(result.NeedsRecovery);
        Assert.Equal(
            AudiobookExecutionOperationStatus.RollbackFailed,
            Assert.Single(fixture.Journal.Latest!.Operations).Status);

        fixture.Metadata.FailRestore = false;
        var recovery = await fixture.Service.RecoverInterruptedAsync(
            fixture.SourceId,
            fixture.Root);

        Assert.Equal(AudiobookExecutionRunStatus.FailedRolledBack, recovery.Status);
        Assert.False(recovery.NeedsRecovery);
        Assert.Equal(
            AudiobookExecutionOperationStatus.RolledBack,
            Assert.Single(fixture.Journal.Latest!.Operations).Status);
    }

    [Fact]
    public async Task RecoverInterrupted_NeverMovesAnUnverifiedDestinationAutomatically()
    {
        var fixture = CreateFixture(("Incoming\\Book.mp3", "Author\\Book\\Author - Book.mp3"));
        var source = Path.Combine(fixture.Root, "Incoming\\Book.mp3");
        var destination = Path.Combine(fixture.Root, "Author\\Book\\Author - Book.mp3");
        fixture.Files.CorruptDestinationThenThrowNumber = 1;

        var execution = await fixture.Service.ExecuteApprovedAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate]);

        Assert.Equal(AudiobookExecutionRunStatus.CompletedNeedsRecovery, execution.Status);
        Assert.Equal(
            AudiobookExecutionOperationStatus.NeedsAttention,
            Assert.Single(fixture.Journal.Latest!.Operations).Status);
        Assert.False(fixture.Files.FileExists(source));
        Assert.True(fixture.Files.FileExists(destination));
        var moveCountBeforeRecovery = fixture.Files.MoveCount;

        var unresolved = await fixture.Service.RecoverInterruptedAsync(
            fixture.SourceId,
            fixture.Root);

        Assert.Equal(AudiobookExecutionRunStatus.CompletedNeedsRecovery, unresolved.Status);
        Assert.Equal(moveCountBeforeRecovery, fixture.Files.MoveCount);
        Assert.False(fixture.Files.FileExists(source));
        Assert.True(fixture.Files.FileExists(destination));
        Assert.Equal(
            AudiobookExecutionOperationStatus.NeedsAttention,
            Assert.Single(fixture.Journal.Latest!.Operations).Status);

        fixture.Files.Move(destination, source);
        fixture.Files.ResetMoveCount();
        var resolved = await fixture.Service.RecoverInterruptedAsync(
            fixture.SourceId,
            fixture.Root);

        Assert.Equal(AudiobookExecutionRunStatus.Completed, resolved.Status);
        Assert.False(resolved.NeedsRecovery);
        Assert.Equal(0, fixture.Files.MoveCount);
        Assert.True(fixture.Files.FileExists(source));
        Assert.False(fixture.Files.FileExists(destination));
        Assert.Equal(
            AudiobookExecutionOperationStatus.Failed,
            Assert.Single(fixture.Journal.Latest!.Operations).Status);
    }

    [Fact]
    public async Task RecoverInterrupted_RestoresNeedsAttentionMetadataOnlyOperation()
    {
        var relativePath = "Author\\Book\\Author - Book.mp3";
        var fixture = CreateFixture((relativePath, relativePath));
        var planned = Assert.Single(fixture.Candidate.BatchPlan!.Operations);
        var source = Path.Combine(fixture.Root, relativePath);
        var originalMetadata = fixture.Metadata.Read(source);
        var operation = new AudiobookExecutionOperationEntry(
            Guid.NewGuid(),
            0,
            fixture.Candidate.BatchPlan.PlanKey,
            fixture.Candidate.BatchPlan.InputSignature,
            planned.MediaItemId,
            relativePath,
            relativePath,
            AudiobookFileOperationKind.UpdateMetadata,
            AudiobookExecutionOperationStatus.NeedsAttention,
            100,
            TestFileOperator.ModifiedAtUtc,
            OriginalMetadataJson: System.Text.Json.JsonSerializer.Serialize(originalMetadata));
        fixture.Journal.Latest = new AudiobookExecutionRunEntry(
            Guid.NewGuid(),
            fixture.SourceId,
            AudiobookExecutionRunStatus.CompletedNeedsRecovery,
            1,
            0,
            0,
            DateTime.UtcNow,
            DateTime.UtcNow,
            DateTime.UtcNow,
            "Manual review required.",
            [operation],
            fixture.Root,
            fixture.Root);

        var recovery = await fixture.Service.RecoverInterruptedAsync(
            fixture.SourceId,
            fixture.Root);

        Assert.Equal(AudiobookExecutionRunStatus.Completed, recovery.Status);
        Assert.Equal(1, fixture.Metadata.RestoreCount);
        Assert.Equal(
            AudiobookExecutionOperationStatus.Failed,
            Assert.Single(fixture.Journal.Latest.Operations).Status);
    }

    [Fact]
    public async Task ExecuteApproved_RollbackProgressKeepsOriginalBatchTotals()
    {
        var fixture = CreateFixture(
            ("Incoming\\Part 1.mp3", "Author\\Book\\001 - Book.mp3"),
            ("Incoming\\Part 2.mp3", "Author\\Book\\002 - Book.mp3"),
            ("Incoming\\Part 3.mp3", "Author\\Book\\003 - Book.mp3"),
            ("Incoming\\Part 4.mp3", "Author\\Book\\004 - Book.mp3"),
            ("Incoming\\Part 5.mp3", "Author\\Book\\005 - Book.mp3"));
        fixture.Files.FailMoveNumber = 3;
        fixture.Files.SecondaryFailMoveNumber = 4;
        var reports = new List<AudiobookExecutionProgress>();
        var progress = new SynchronousProgress<AudiobookExecutionProgress>(reports.Add);

        await fixture.Service.ExecuteApprovedAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate],
            progress);

        var rollbackReports = reports
            .Where(report => report.Status.StartsWith("Rollback attempt", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, rollbackReports.Count);
        Assert.All(rollbackReports, report => Assert.Equal(3, report.ProcessedCount));
        Assert.All(rollbackReports, report => Assert.Equal(5, report.TotalCount));
        Assert.Contains("1 of 2", rollbackReports[0].Status, StringComparison.Ordinal);
        Assert.Contains("1 need recovery", rollbackReports[0].Status, StringComparison.Ordinal);
        Assert.Contains("2 of 2", rollbackReports[1].Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteApproved_UpdatesMetadataWithoutMovingAnAlreadyOrganisedFile()
    {
        var fixture = CreateFixture(("Author\\Book\\Author - Book.mp3", "Author\\Book\\Author - Book.mp3"));
        var operation = Assert.Single(fixture.Candidate.BatchPlan!.Operations) with
        {
            Kind = AudiobookFileOperationKind.UpdateMetadata
        };
        var candidate = fixture.Candidate with
        {
            BatchPlan = fixture.Candidate.BatchPlan with { Operations = [operation] }
        };

        var result = await fixture.Service.ExecuteApprovedAsync(
            fixture.SourceId,
            fixture.Root,
            [candidate]);

        Assert.True(result.Succeeded);
        Assert.Equal(1, fixture.Metadata.WriteCount);
        Assert.Equal(0, fixture.Files.MoveCount);
        Assert.True(fixture.Files.FileExists(Path.Combine(
            fixture.Root,
            "Author\\Book\\Author - Book.mp3")));
    }

    [Fact]
    public async Task ExecuteApproved_LeavesUnavailableOptionalMetadataUnset()
    {
        var fixture = CreateFixture(("Incoming\\Book.mp3", "Author\\Book\\Author - Book.mp3"));
        var candidate = fixture.Candidate with
        {
            OrganisationProposal = fixture.Candidate.OrganisationProposal! with
            {
                FirstPublishedYear = null,
                GenreCategory = "Uncategorised",
                SeriesName = null
            }
        };

        var result = await fixture.Service.ExecuteApprovedAsync(
            fixture.SourceId,
            fixture.Root,
            [candidate]);

        Assert.True(result.Succeeded);
        Assert.Null(fixture.Metadata.LastUpdate?.Genre);
        Assert.Null(fixture.Metadata.LastUpdate?.Year);
        Assert.Null(fixture.Metadata.LastUpdate?.SeriesName);
    }

    [Fact]
    public async Task ExecuteApproved_EmbedsArtworkAndCreatesPlexCoverWithoutOverwriting()
    {
        var fixture = CreateFixture(("Incoming\\Book.mp3", "Author\\Book\\Author - Book.mp3"));
        var artwork = new AudiobookArtwork("image/jpeg", [0xFF, 0xD8, 0xFF, 0xD9]);
        fixture.CoverProvider.Artwork = artwork;
        var candidate = fixture.Candidate with
        {
            OrganisationProposal = fixture.Candidate.OrganisationProposal! with
            {
                CoverUrl = "https://covers.openlibrary.org/b/id/123-M.jpg"
            }
        };

        var result = await fixture.Service.ExecuteApprovedAsync(
            fixture.SourceId,
            fixture.Root,
            [candidate]);

        Assert.True(result.Succeeded);
        Assert.Same(artwork, fixture.Metadata.LastUpdate?.CoverArtwork);
        Assert.True(fixture.Files.FileExists(Path.Combine(fixture.Root, "Author\\Book\\cover.jpg")));
        Assert.Contains("Plex-compatible cover file", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteApproved_PreservesExistingCoverFile()
    {
        var fixture = CreateFixture(("Incoming\\Book.mp3", "Author\\Book\\Author - Book.mp3"));
        fixture.Files.AddFile(Path.Combine(fixture.Root, "Author\\Book\\cover.jpg"));
        fixture.CoverProvider.Artwork = new AudiobookArtwork("image/jpeg", [0xFF, 0xD8, 0xFF, 0xD9]);
        var candidate = fixture.Candidate with
        {
            OrganisationProposal = fixture.Candidate.OrganisationProposal! with
            {
                CoverUrl = "https://covers.openlibrary.org/b/id/123-M.jpg"
            }
        };

        var result = await fixture.Service.ExecuteApprovedAsync(
            fixture.SourceId,
            fixture.Root,
            [candidate]);

        Assert.True(result.Succeeded);
        Assert.Equal(0, fixture.Files.SidecarWriteCount);
        Assert.Contains("existing cover file", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecoverInterrupted_DoesNotMoveUnverifiedRunningDestination()
    {
        var fixture = CreateFixture(("Incoming\\Book.mp3", "Author\\Book\\Author - Book.mp3"));
        var source = Path.Combine(fixture.Root, "Incoming\\Book.mp3");
        var destination = Path.Combine(fixture.Root, "Author\\Book\\Author - Book.mp3");
        fixture.Files.Move(source, destination);
        var operation = new AudiobookExecutionOperationEntry(
            Guid.NewGuid(), 0, "plan-1", "signature", Guid.NewGuid(),
            "Incoming\\Book.mp3", "Author\\Book\\Author - Book.mp3",
            AudiobookFileOperationKind.MoveAndRename,
            AudiobookExecutionOperationStatus.Running,
            100,
            TestFileOperator.ModifiedAtUtc);
        fixture.Journal.Latest = new AudiobookExecutionRunEntry(
            Guid.NewGuid(), fixture.SourceId, AudiobookExecutionRunStatus.Running,
            1, 0, 0, DateTime.UtcNow, DateTime.UtcNow, null, null, [operation]);

        var result = await fixture.Service.RecoverInterruptedAsync(fixture.SourceId, fixture.Root);

        Assert.Equal(AudiobookExecutionRunStatus.FailedNeedsRecovery, result.Status);
        Assert.False(fixture.Files.FileExists(source));
        Assert.True(fixture.Files.FileExists(destination));
        Assert.Equal(
            AudiobookExecutionOperationStatus.NeedsAttention,
            fixture.Journal.Latest.Operations[0].Status);
    }

    [Fact]
    public async Task RecoverInterrupted_UsesRootsRecordedBeforeConfigurationChanged()
    {
        var fixture = CreateFixture(("Incoming\\Book.mp3", "Author\\Book\\Author - Book.mp3"));
        var destinationRoot = Path.Combine(
            Path.GetTempPath(),
            "Metaroq.Recorded.Destination.Tests",
            Guid.NewGuid().ToString("N"));
        fixture.Files.AddDirectory(destinationRoot);
        var source = Path.Combine(fixture.Root, "Incoming\\Book.mp3");
        var destination = Path.Combine(destinationRoot, "Author\\Book\\Author - Book.mp3");
        fixture.Files.Move(source, destination);
        var operation = new AudiobookExecutionOperationEntry(
            Guid.NewGuid(), 0, "plan-1", "signature", Guid.NewGuid(),
            "Incoming\\Book.mp3", "Author\\Book\\Author - Book.mp3",
            AudiobookFileOperationKind.MoveAndRename,
            AudiobookExecutionOperationStatus.Completed,
            100,
            TestFileOperator.ModifiedAtUtc);
        fixture.Journal.Latest = new AudiobookExecutionRunEntry(
            Guid.NewGuid(), fixture.SourceId, AudiobookExecutionRunStatus.Running,
            1, 0, 0, DateTime.UtcNow, DateTime.UtcNow, null, null, [operation],
            fixture.Root, destinationRoot);

        var result = await fixture.Service.RecoverInterruptedAsync(
            fixture.SourceId,
            Path.Combine(Path.GetTempPath(), "Changed source"),
            Path.Combine(Path.GetTempPath(), "Changed destination"));

        Assert.Equal(AudiobookExecutionRunStatus.FailedRolledBack, result.Status);
        Assert.True(fixture.Files.FileExists(source));
        Assert.False(fixture.Files.FileExists(destination));
    }

    [Fact]
    public async Task RecoverInterrupted_IgnoresSkippedAndPendingOperations()
    {
        var fixture = CreateFixture(
            ("Incoming\\Part 1.mp3", "Author\\Book\\001 - Book.mp3"),
            ("Incoming\\Part 2.mp3", "Author\\Book\\002 - Book.mp3"),
            ("Incoming\\Part 3.mp3", "Author\\Book\\003 - Book.mp3"));
        var plannedOperations = fixture.Candidate.BatchPlan!.Operations;
        var firstSource = Path.Combine(fixture.Root, plannedOperations[0].SourceRelativePath);
        var firstDestination = Path.Combine(fixture.Root, plannedOperations[0].DestinationRelativePath);
        fixture.Files.Move(firstSource, firstDestination);
        fixture.Files.ResetMoveCount();
        var statuses = new[]
        {
            AudiobookExecutionOperationStatus.Completed,
            AudiobookExecutionOperationStatus.Failed,
            AudiobookExecutionOperationStatus.Pending
        };
        var journalOperations = plannedOperations
            .Select((operation, index) => new AudiobookExecutionOperationEntry(
                Guid.NewGuid(),
                index,
                "plan-1",
                "signature",
                operation.MediaItemId,
                operation.SourceRelativePath,
                operation.DestinationRelativePath,
                operation.Kind,
                statuses[index],
                100,
                TestFileOperator.ModifiedAtUtc))
            .ToList();
        fixture.Journal.Latest = new AudiobookExecutionRunEntry(
            Guid.NewGuid(),
            fixture.SourceId,
            AudiobookExecutionRunStatus.Running,
            3,
            1,
            0,
            DateTime.UtcNow,
            DateTime.UtcNow,
            null,
            null,
            journalOperations);

        var result = await fixture.Service.RecoverInterruptedAsync(
            fixture.SourceId,
            fixture.Root);

        Assert.Equal(AudiobookExecutionRunStatus.FailedRolledBack, result.Status);
        Assert.True(fixture.Files.FileExists(firstSource));
        Assert.False(fixture.Files.FileExists(firstDestination));
        Assert.Equal(
            [
                AudiobookExecutionOperationStatus.RolledBack,
                AudiobookExecutionOperationStatus.Failed,
                AudiobookExecutionOperationStatus.Pending
            ],
            fixture.Journal.Latest!.Operations.Select(operation => operation.Status));
        Assert.Equal(1, fixture.Files.MoveCount);
    }

    [Fact]
    public async Task ExecuteApproved_RejectsDestinationOutsideLibrary()
    {
        var fixture = CreateFixture(("Incoming\\Book.mp3", "..\\Outside.mp3"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ExecuteApprovedAsync(fixture.SourceId, fixture.Root, [fixture.Candidate]));

        Assert.Null(fixture.Journal.Latest);
    }

    private static ExecutionFixture CreateFixture(params (string Source, string Destination)[] paths)
    {
        var sourceId = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "Metaroq.Execution.Tests", Guid.NewGuid().ToString("N"));
        var files = new TestFileOperator(root);
        var parts = new List<AudiobookCandidatePart>();
        var operations = paths.Select((path, index) =>
        {
            var fullPath = Path.Combine(root, path.Source);
            files.AddFile(fullPath);
            var mediaItem = new Archivio.Domain.MediaItem(
                sourceId,
                fullPath,
                path.Source,
                100,
                TestFileOperator.ModifiedAtUtc,
                TestFileOperator.ModifiedAtUtc,
                TestFileOperator.ModifiedAtUtc);
            var metadata = new LocalMediaMetadata(
                fullPath,
                new MetadataValue("Original title", MetadataValueSource.EmbeddedTag),
                new MetadataValue("Original author", MetadataValueSource.EmbeddedTag),
                new MetadataValue("Original album", MetadataValueSource.EmbeddedTag),
                new MetadataValue("Original genre", MetadataValueSource.EmbeddedTag),
                2000,
                (uint)(index + 1),
                null,
                null,
                null,
                null,
                null,
                false,
                [],
                []);
            parts.Add(new AudiobookCandidatePart(mediaItem, index + 1, true, metadata));
            return new AudiobookFileOperation(
                mediaItem.Id,
                path.Source,
                path.Destination,
                AudiobookFileOperationKind.MoveAndRename);
        }).ToList();
        var plan = new AudiobookBatchPlan(
            "plan-1", "signature", "Author - Book",
            AudiobookBatchValidationStatus.Ready,
            AudiobookBatchDecision.Approved,
            operations,
            [],
            DateTime.UtcNow);
        var proposal = new AudiobookOrganisationProposal(
            plan.PlanKey,
            "Author",
            "Book",
            2026,
            "Fiction",
            Path.Combine("Author", "Book"),
            paths.Length == 1 ? "Author - Book.mp3" : "001 - Book{original extension}",
            paths.Length == 1
                ? AudiobookOrganisationAction.MoveAndRename
                : AudiobookOrganisationAction.OrganiseMultipart,
            1,
            paths.Length,
            true,
            false,
            1m,
            true,
            paths.Length > 1,
            [],
            [],
            DateTime.UtcNow);
        var candidate = new AudiobookCandidateGroup(
            "Author - Book", "Author", "Book",
            MetadataValueSource.EmbeddedTag,
            MetadataValueSource.EmbeddedTag,
            paths.Length > 1,
            parts,
            1m,
            [])
        {
            OrganisationProposal = proposal,
            BatchPlan = plan
        };
        var journal = new TestJournalStore();
        var metadata = new TestMetadataWriter();
        var backups = new TestDatabaseBackupService();
        var coverProvider = new TestCoverArtworkProvider();
        return new ExecutionFixture(
            sourceId,
            root,
            files,
            journal,
            candidate,
            metadata,
            coverProvider,
            backups,
            new AudiobookBatchExecutionService(
                journal,
                files,
                metadata,
                coverProvider,
                backups));
    }

    private sealed record ExecutionFixture(
        Guid SourceId,
        string Root,
        TestFileOperator Files,
        TestJournalStore Journal,
        AudiobookCandidateGroup Candidate,
        TestMetadataWriter Metadata,
        TestCoverArtworkProvider CoverProvider,
        TestDatabaseBackupService Backups,
        AudiobookBatchExecutionService Service);

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private sealed class TestDatabaseBackupService : IDatabaseBackupService
    {
        public int BackupCount { get; private set; }

        public Task<string?> CreateBackupAsync(
            string reason,
            CancellationToken cancellationToken = default)
        {
            BackupCount++;
            return Task.FromResult<string?>("test-backup.db");
        }
    }

    private sealed class TestMetadataWriter : IAudiobookMetadataWriter
    {
        private int _readCount;

        public int? FailReadNumber { get; set; }
        public bool FailRestore { get; set; }
        public int WriteCount { get; private set; }
        public int RestoreCount { get; private set; }
        public AudiobookTagUpdate? LastUpdate { get; private set; }

        public AudiobookTagState Read(string path)
        {
            _readCount++;
            if (FailReadNumber == _readCount)
            {
                throw new InvalidDataException("Simulated corrupt metadata.");
            }

            return new AudiobookTagState(
                "Original title",
                ["Original author"],
                ["Original author"],
                "Original album",
                ["Original genre"],
                2000,
                1,
                1,
                null);
        }

        public void Write(string path, AudiobookTagUpdate update)
        {
            WriteCount++;
            LastUpdate = update;
        }

        public void Restore(string path, AudiobookTagState state)
        {
            RestoreCount++;
            if (FailRestore)
            {
                throw new IOException(
                    "Simulated metadata restore sharing violation.",
                    unchecked((int)0x80070020));
            }
        }
    }

    private sealed class TestCoverArtworkProvider : IAudiobookCoverArtworkProvider
    {
        public AudiobookArtwork? Artwork { get; set; }

        public Task<AudiobookArtwork?> FetchAsync(
            string coverUrl,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Artwork);
    }

    private sealed class TestFileOperator(string root) : IAudiobookFileOperator
    {
        public static readonly DateTime ModifiedAtUtc = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);
        private readonly Dictionary<string, AudiobookFileSnapshot> _files =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase)
        {
            root
        };
        private int _fileExistsCount;
        private int _moveCount;

        public int? FailMoveNumber { get; set; }
        public int? SecondaryFailMoveNumber { get; set; }
        public int? MoveThenThrowNumber { get; set; }
        public int? SystemFailureAfterMoveNumber { get; set; }
        public int? PartialMoveThenThrowNumber { get; set; }
        public int? CorruptDestinationThenThrowNumber { get; set; }
        public int? MissingFileAndRemoveRootOnFileExistsNumber { get; set; }
        public int? SharingViolationMoveNumber { get; set; }
        public string? PersistentSharingViolationSourcePath { get; set; }
        public string? FileExistsFailurePath { get; set; }
        public Action? BeforeFailMoveAction { get; set; }
        public Action? PartialMoveThenThrowAction { get; set; }
        public int MoveCount => _moveCount;
        public int SidecarWriteCount { get; private set; }

        public void AddFile(string path) => _files[path] = new AudiobookFileSnapshot(100, ModifiedAtUtc);
        public void AddDirectory(string path) => _directories.Add(path);
        public void ResetMoveCount() => _moveCount = 0;
        public bool FileExists(string path)
        {
            _fileExistsCount++;
            if (string.Equals(FileExistsFailurePath, path, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("Simulated inaccessible file state.");
            }

            if (MissingFileAndRemoveRootOnFileExistsNumber == _fileExistsCount)
            {
                _directories.Remove(root);
                return false;
            }

            return _files.ContainsKey(path);
        }
        public bool DirectoryExists(string path) => _directories.Contains(path);
        public AudiobookFileSnapshot GetSnapshot(string path) => _files[path];
        public void CreateDirectory(string path) { }

        public void WriteAllBytesNew(string path, ReadOnlySpan<byte> data)
        {
            if (!_files.TryAdd(path, new AudiobookFileSnapshot(data.Length, ModifiedAtUtc)))
            {
                throw new IOException("Destination already exists.");
            }

            SidecarWriteCount++;
        }

        public void Move(string sourcePath, string destinationPath)
        {
            _moveCount++;
            if (FailMoveNumber == _moveCount ||
                SecondaryFailMoveNumber == _moveCount)
            {
                BeforeFailMoveAction?.Invoke();
                throw new IOException("Simulated network move failure.");
            }

            if (SharingViolationMoveNumber == _moveCount)
            {
                throw new IOException(
                    "The process cannot access the file because it is being used by another process.",
                    unchecked((int)0x80070020));
            }

            if (string.Equals(
                    PersistentSharingViolationSourcePath,
                    sourcePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    "The process cannot access the file because it is being used by another process.",
                    unchecked((int)0x80070020));
            }

            if (PartialMoveThenThrowNumber == _moveCount &&
                _files.TryGetValue(sourcePath, out var partialSnapshot))
            {
                _files[destinationPath] = partialSnapshot;
                PartialMoveThenThrowAction?.Invoke();
                throw new IOException(
                    "Simulated partial cross-volume move.",
                    unchecked((int)0x80070020));
            }

            if (!_files.Remove(sourcePath, out var snapshot))
            {
                throw new FileNotFoundException("Source missing.", sourcePath);
            }

            if (!_files.TryAdd(destinationPath, snapshot))
            {
                _files[sourcePath] = snapshot;
                throw new IOException(
                    "Destination already exists.",
                    unchecked((int)0x80070050));
            }

            if (MoveThenThrowNumber == _moveCount)
            {
                throw new IOException(
                    "Simulated move completion sharing warning.",
                    unchecked((int)0x80070020));
            }

            if (SystemFailureAfterMoveNumber == _moveCount)
            {
                throw new IOException(
                    "Simulated destination storage failure after move.",
                    unchecked((int)0x80070070));
            }

            if (CorruptDestinationThenThrowNumber == _moveCount)
            {
                _files[destinationPath] = snapshot with { SizeBytes = snapshot.SizeBytes + 1 };
                throw new IOException(
                    "Simulated unverified move completion.",
                    unchecked((int)0x80070020));
            }
        }
    }

    private sealed class TestJournalStore : IAudiobookExecutionJournalStore
    {
        public AudiobookExecutionRunEntry? Latest { get; set; }

        public Task CreateAsync(AudiobookExecutionRunEntry run, CancellationToken cancellationToken = default)
        {
            Latest = run;
            return Task.CompletedTask;
        }

        public Task UpdateRunAsync(
            Guid runId,
            AudiobookExecutionRunStatus status,
            int completedOperationCount,
            int rolledBackOperationCount,
            string? errorMessage,
            DateTime updatedAtUtc,
            DateTime? completedAtUtc,
            CancellationToken cancellationToken = default)
        {
            Latest = Latest! with
            {
                Status = status,
                CompletedOperationCount = completedOperationCount,
                RolledBackOperationCount = rolledBackOperationCount,
                ErrorMessage = errorMessage,
                UpdatedAtUtc = updatedAtUtc,
                CompletedAtUtc = completedAtUtc
            };
            return Task.CompletedTask;
        }

        public Task UpdateOperationAsync(
            Guid operationId,
            AudiobookExecutionOperationStatus status,
            string? errorMessage,
            CancellationToken cancellationToken = default)
        {
            Latest = Latest! with
            {
                Operations = Latest.Operations
                    .Select(operation => operation.Id == operationId
                        ? operation with { Status = status, ErrorMessage = errorMessage }
                        : operation)
                    .ToList()
            };
            return Task.CompletedTask;
        }

        public Task<AudiobookExecutionRunEntry?> LoadLatestAsync(
            Guid librarySourceId,
            CancellationToken cancellationToken = default) => Task.FromResult(Latest);
    }
}
