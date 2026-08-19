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
        var operations = paths.Select((path, index) =>
        {
            files.AddFile(Path.Combine(root, path.Source));
            return new AudiobookFileOperation(
                Guid.NewGuid(),
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
        var candidate = new AudiobookCandidateGroup(
            "Author - Book", "Author", "Book",
            MetadataValueSource.EmbeddedTag,
            MetadataValueSource.EmbeddedTag,
            paths.Length > 1,
            [],
            1m,
            [])
        {
            BatchPlan = plan
        };
        var journal = new TestJournalStore();
        return new ExecutionFixture(
            sourceId,
            root,
            files,
            journal,
            candidate,
            new AudiobookBatchExecutionService(journal, files));
    }

    private sealed record ExecutionFixture(
        Guid SourceId,
        string Root,
        TestFileOperator Files,
        TestJournalStore Journal,
        AudiobookCandidateGroup Candidate,
        AudiobookBatchExecutionService Service);

    private sealed class TestFileOperator(string root) : IAudiobookFileOperator
    {
        public static readonly DateTime ModifiedAtUtc = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);
        private readonly Dictionary<string, AudiobookFileSnapshot> _files =
            new(StringComparer.OrdinalIgnoreCase);
        private int _moveCount;

        public int? FailMoveNumber { get; set; }

        public void AddFile(string path) => _files[path] = new AudiobookFileSnapshot(100, ModifiedAtUtc);
        public bool FileExists(string path) => _files.ContainsKey(path);
        public bool DirectoryExists(string path) => string.Equals(path, root, StringComparison.OrdinalIgnoreCase);
        public AudiobookFileSnapshot GetSnapshot(string path) => _files[path];
        public void CreateDirectory(string path) { }

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
