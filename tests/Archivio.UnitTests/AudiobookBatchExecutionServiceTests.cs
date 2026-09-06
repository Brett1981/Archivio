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
    public async Task ExecuteApproved_RefusesToOverwriteDestinationBeforeCreatingJournal()
    {
        var fixture = CreateFixture(("Incoming\\Book.mp3", "Author\\Book\\Author - Book.mp3"));
        fixture.Files.AddFile(Path.Combine(fixture.Root, "Author\\Book\\Author - Book.mp3"));

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            fixture.Service.ExecuteApprovedAsync(fixture.SourceId, fixture.Root, [fixture.Candidate]));

        Assert.Contains("will not overwrite", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(fixture.Journal.Latest);
        Assert.True(fixture.Files.FileExists(Path.Combine(fixture.Root, "Incoming\\Book.mp3")));
    }

    [Fact]
    public async Task ExecuteApproved_RollsBackEarlierMovesWhenLaterMoveFails()
    {
        var fixture = CreateFixture(
            ("Incoming\\Part 1.mp3", "Author\\Book\\001 - Book.mp3"),
            ("Incoming\\Part 2.mp3", "Author\\Book\\002 - Book.mp3"));
        fixture.Files.FailMoveNumber = 2;

        var result = await fixture.Service.ExecuteApprovedAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate]);

        Assert.Equal(AudiobookExecutionRunStatus.FailedRolledBack, result.Status);
        Assert.Equal(1, result.RolledBackOperationCount);
        Assert.True(fixture.Files.FileExists(Path.Combine(fixture.Root, "Incoming\\Part 1.mp3")));
        Assert.True(fixture.Files.FileExists(Path.Combine(fixture.Root, "Incoming\\Part 2.mp3")));
        Assert.False(fixture.Files.FileExists(Path.Combine(fixture.Root, "Author\\Book\\001 - Book.mp3")));
        Assert.Equal(2, fixture.Metadata.WriteCount);
        Assert.Equal(2, fixture.Metadata.RestoreCount);
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
    public async Task RecoverInterrupted_RestoresMovedDestinationsWithoutOverwrite()
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

        Assert.Equal(AudiobookExecutionRunStatus.FailedRolledBack, result.Status);
        Assert.True(fixture.Files.FileExists(source));
        Assert.False(fixture.Files.FileExists(destination));
        Assert.Equal(AudiobookExecutionOperationStatus.RolledBack, fixture.Journal.Latest.Operations[0].Status);
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
        public int WriteCount { get; private set; }
        public int RestoreCount { get; private set; }
        public AudiobookTagUpdate? LastUpdate { get; private set; }

        public AudiobookTagState Read(string path) => new(
            "Original title",
            ["Original author"],
            ["Original author"],
            "Original album",
            ["Original genre"],
            2000,
            1,
            1,
            null);

        public void Write(string path, AudiobookTagUpdate update)
        {
            WriteCount++;
            LastUpdate = update;
        }

        public void Restore(string path, AudiobookTagState state) => RestoreCount++;
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
        private int _moveCount;

        public int? FailMoveNumber { get; set; }
        public int MoveCount => _moveCount;
        public int SidecarWriteCount { get; private set; }

        public void AddFile(string path) => _files[path] = new AudiobookFileSnapshot(100, ModifiedAtUtc);
        public void ResetMoveCount() => _moveCount = 0;
        public bool FileExists(string path) => _files.ContainsKey(path);
        public bool DirectoryExists(string path) => string.Equals(path, root, StringComparison.OrdinalIgnoreCase);
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
            if (FailMoveNumber == _moveCount)
            {
                throw new IOException("Simulated network move failure.");
            }

            if (!_files.Remove(sourcePath, out var snapshot))
            {
                throw new FileNotFoundException("Source missing.", sourcePath);
            }

            if (!_files.TryAdd(destinationPath, snapshot))
            {
                _files[sourcePath] = snapshot;
                throw new IOException("Destination already exists.");
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
