using Archivio.App;
using Archivio.Application.Abstractions;
using Archivio.Application.Configuration;
using Archivio.Domain;
using Microsoft.Extensions.Options;

namespace Archivio.App.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void DefaultProductName_UsesMetaroqBrand()
    {
        var viewModel = CreateViewModel(new CountingBatchPlanningService());

        Assert.Equal("Metaroq", viewModel.Title);
    }

    [Fact]
    public void ReviewFocusCommands_ExpandAndRestoreTheReviewWorkspace()
    {
        var viewModel = CreateViewModel(new CountingBatchPlanningService());

        viewModel.EnterReviewFocusCommand.Execute(null);

        Assert.True(viewModel.IsReviewFocusMode);

        viewModel.ExitReviewFocusCommand.Execute(null);

        Assert.False(viewModel.IsReviewFocusMode);
    }

    [Fact]
    public void ItemFirstReview_CollapsesRelatedCandidatesIntoOneAudiobookPlan()
    {
        var viewModel = CreateViewModel(new CountingBatchPlanningService());
        var primary = CreateCandidateWithOrganisationPlan(isPrimary: true);
        var related = CreateCandidateWithOrganisationPlan(isPrimary: false);

        viewModel.AudiobookCandidates = [primary, related];

        var visible = viewModel.AudiobookCandidatesView
            .Cast<AudiobookCandidateGroup>()
            .ToList();

        Assert.Equal(2, viewModel.AudiobookCandidateCount);
        Assert.Equal(1, viewModel.AudiobookReviewItemCount);
        Assert.Equal("1 of 1 audiobook plans", viewModel.AudiobookReviewCountLabel);
        Assert.Same(primary, Assert.Single(visible));
        Assert.Equal("Roald Dahl", primary.ReviewAuthorDisplay);
        Assert.Equal("The BFG", primary.ReviewTitle);
        Assert.Equal("1 audiobook · 35 source files", primary.ReviewItemSummary);
        Assert.Equal("One folder with 35 ordered tracks", primary.ReviewPlannedResult);
        Assert.Equal("Show 35 source files", primary.SourceFilesDisclosureLabel);
    }

    [Fact]
    public async Task LoadingSavedAnalysis_PreparesBatchPreviewOnlyOnce()
    {
        var source = new LibrarySource(
            "Audiobooks",
            Path.Combine(Path.GetTempPath(), "Archivio.App.Tests"),
            LibrarySourceType.Audiobooks);
        var batchPlanningService = new CountingBatchPlanningService();
        var viewModel = CreateViewModel(batchPlanningService);

        viewModel.SelectedSource = source;

        await WaitUntilAsync(
            () => viewModel.AudiobookAnalysisStatus == "Saved analysis loaded",
            TimeSpan.FromSeconds(2));

        Assert.Equal(1, batchPlanningService.PrepareCount);
        Assert.StartsWith("Dry run prepared:", viewModel.BatchPlanningStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectedPlanActions_SaveApproveDeferAndResetDecisions()
    {
        var source = new LibrarySource(
            "Audiobooks",
            Path.Combine(Path.GetTempPath(), "Archivio.App.Tests"),
            LibrarySourceType.Audiobooks);
        var batchPlanningService = new CountingBatchPlanningService();
        var viewModel = CreateViewModel(batchPlanningService);
        viewModel.SelectedSource = source;
        await WaitUntilAsync(
            () => viewModel.AudiobookAnalysisStatus == "Saved analysis loaded",
            TimeSpan.FromSeconds(2));
        var candidate = CreateCandidateWithBatchPlan();
        viewModel.AudiobookCandidates.Add(candidate);
        viewModel.SelectedAudiobookCandidate = candidate;

        await viewModel.ApproveSelectedBatchCommand.ExecuteAsync(null);
        await viewModel.DeferSelectedBatchCommand.ExecuteAsync(null);
        await viewModel.ResetSelectedBatchDecisionCommand.ExecuteAsync(null);

        Assert.Equal(
            [AudiobookBatchDecision.Approved, AudiobookBatchDecision.Deferred, AudiobookBatchDecision.Pending],
            batchPlanningService.Decisions);
        Assert.All(batchPlanningService.PlanKeys, keys => Assert.Equal("plan-1", Assert.Single(keys)));
        Assert.Equal(
            AudiobookBatchDecision.Pending,
            viewModel.SelectedAudiobookCandidate?.BatchPlan?.Decision);
    }

    [Fact]
    public void ReviewRequiredSelectedPlan_CannotBeApprovedButCanBeDeferred()
    {
        var viewModel = CreateViewModel(new CountingBatchPlanningService());
        viewModel.SelectedAudiobookCandidate = CreateCandidateWithBatchPlan(
            AudiobookBatchValidationStatus.ReviewRequired);

        Assert.False(viewModel.ApproveSelectedBatchCommand.CanExecute(null));
        Assert.True(viewModel.DeferSelectedBatchCommand.CanExecute(null));
        Assert.False(viewModel.ResetSelectedBatchDecisionCommand.CanExecute(null));
    }

    [Fact]
    public async Task ExecuteApproved_RequiresConfirmationAndRunsGuardedExecutionOffUiContext()
    {
        var source = new LibrarySource(
            "Audiobooks",
            Path.Combine(Path.GetTempPath(), "Metaroq.App.Tests"),
            LibrarySourceType.Audiobooks);
        var executionService = new RecordingExecutionService();
        var confirmation = new RecordingConfirmationService();
        var viewModel = CreateViewModel(
            new CountingBatchPlanningService(),
            executionService,
            confirmation);
        viewModel.SelectedSource = source;
        await WaitUntilAsync(
            () => viewModel.AudiobookAnalysisStatus == "Saved analysis loaded",
            TimeSpan.FromSeconds(2));
        viewModel.AudiobookCandidates.Add(CreateCandidateWithBatchPlan(
            AudiobookBatchValidationStatus.Ready,
            AudiobookBatchDecision.Approved,
            includeOperation: true));

        Assert.True(viewModel.ExecuteApprovedBatchCommand.CanExecute(null));
        var previousContext = SynchronizationContext.Current;
        var uiContext = new InlineSynchronizationContext();
        try
        {
            SynchronizationContext.SetSynchronizationContext(uiContext);
            await viewModel.ExecuteApprovedBatchCommand.ExecuteAsync(null);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        Assert.Equal(1, confirmation.ExecutionConfirmationCount);
        Assert.Equal(1, executionService.ExecutionCount);
        Assert.NotSame(uiContext, executionService.ExecutionSynchronizationContext);
        Assert.Contains("Execution complete", viewModel.AudiobookExecutionStatus, StringComparison.Ordinal);
    }

    private static MainWindowViewModel CreateViewModel(
        IAudiobookBatchPlanningService batchPlanningService,
        IAudiobookBatchExecutionService? executionService = null,
        IAudiobookExecutionConfirmationService? confirmationService = null) =>
        new(
            Options.Create(new ArchivioOptions()),
            new StubLibrarySourceService(),
            new StubFolderPickerService(),
            new StubBackgroundScanService(),
            new EmptyMediaCatalogueService(),
            new SavedAnalysisService(),
            new PassthroughOnlineMetadataLookupService(),
            new PassthroughOrganisationService(),
            batchPlanningService,
            executionService ?? new RecordingExecutionService(),
            confirmationService ?? new RecordingConfirmationService());

    private static AudiobookCandidateGroup CreateCandidateWithBatchPlan(
        AudiobookBatchValidationStatus validationStatus = AudiobookBatchValidationStatus.Ready,
        AudiobookBatchDecision decision = AudiobookBatchDecision.Pending,
        bool includeOperation = false) =>
        new(
            "Author - Book",
            "Author",
            "Book",
            MetadataValueSource.EmbeddedTag,
            MetadataValueSource.EmbeddedTag,
            true,
            [],
            1m,
            [])
        {
            BatchPlan = new AudiobookBatchPlan(
                "plan-1",
                "signature",
                "Author - Book",
                validationStatus,
                decision,
                includeOperation
                    ? [new AudiobookFileOperation(
                        Guid.NewGuid(),
                        "Incoming\\Book.mp3",
                        "Author\\Book\\Author - Book.mp3",
                        AudiobookFileOperationKind.MoveAndRename)]
                    : [],
                [],
                DateTime.UtcNow)
        };

    private static AudiobookCandidateGroup CreateCandidateWithOrganisationPlan(bool isPrimary) =>
        new(
            isPrimary ? "Chapter Five" : "Chapter Six",
            "Roald Dahl",
            isPrimary ? "Chapter Five" : "Chapter Six",
            MetadataValueSource.EmbeddedTag,
            MetadataValueSource.EmbeddedTag,
            true,
            [],
            1m,
            [])
        {
            OrganisationProposal = new AudiobookOrganisationProposal(
                "the-bfg-plan",
                "Roald Dahl",
                "The BFG",
                1982,
                "Children & Young Adult",
                "Roald Dahl\\The BFG",
                "{sequence} - The BFG{original extension}",
                AudiobookOrganisationAction.ConsolidateCandidates,
                35,
                35,
                isPrimary,
                false,
                1m,
                false,
                true,
                [],
                [],
                DateTime.UtcNow),
            BatchPlan = new AudiobookBatchPlan(
                "the-bfg-plan",
                "signature",
                "Roald Dahl - The BFG",
                AudiobookBatchValidationStatus.ReviewRequired,
                AudiobookBatchDecision.Pending,
                [],
                ["Review source grouping"],
                DateTime.UtcNow)
        };

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
        {
            await Task.Delay(10, cancellation.Token);
        }
    }

    private sealed class CountingBatchPlanningService : IAudiobookBatchPlanningService
    {
        public int PrepareCount { get; private set; }
        public List<AudiobookBatchDecision> Decisions { get; } = [];
        public List<IReadOnlyCollection<string>> PlanKeys { get; } = [];

        public Task<IReadOnlyList<AudiobookCandidateGroup>> PrepareBatchAsync(
            Guid librarySourceId,
            string libraryRoot,
            IReadOnlyList<AudiobookCandidateGroup> candidates,
            IReadOnlyList<MediaItem> indexedMedia,
            CancellationToken cancellationToken = default)
        {
            PrepareCount++;
            if (PrepareCount > 1)
            {
                throw new InvalidOperationException("Batch preview was prepared more than once.");
            }

            return Task.FromResult(candidates);
        }

        public Task<IReadOnlyList<AudiobookCandidateGroup>> SetDecisionAsync(
            Guid librarySourceId,
            IReadOnlyList<AudiobookCandidateGroup> candidates,
            IReadOnlyCollection<string> planKeys,
            AudiobookBatchDecision decision,
            CancellationToken cancellationToken = default)
        {
            Decisions.Add(decision);
            PlanKeys.Add(planKeys);
            var updated = candidates
                .Select(candidate => candidate.BatchPlan is not null && planKeys.Contains(candidate.BatchPlan.PlanKey)
                    ? candidate with { BatchPlan = candidate.BatchPlan with { Decision = decision } }
                    : candidate)
                .ToList();
            return Task.FromResult<IReadOnlyList<AudiobookCandidateGroup>>(updated);
        }
    }

    private sealed class SavedAnalysisService : IAudiobookAnalysisService
    {
        public IReadOnlyList<AudiobookCandidateGroup> Analyse(IEnumerable<MediaItem> mediaItems) => [];

        public Task<IReadOnlyList<AudiobookCandidateGroup>> AnalyseAsync(
            IEnumerable<MediaItem> mediaItems,
            IProgress<AudiobookAnalysisProgress>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AudiobookCandidateGroup>>([]);

        public Task<SavedAudiobookAnalysis?> LoadSavedAnalysisAsync(
            IEnumerable<MediaItem> mediaItems,
            CancellationToken cancellationToken = default) => Task.FromResult<SavedAudiobookAnalysis?>(
                new SavedAudiobookAnalysis(DateTime.UtcNow, 0, []));

        public Task<AudiobookCandidateGroup> EnrichMetadataAsync(
            AudiobookCandidateGroup candidate,
            IProgress<AudiobookAnalysisProgress>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(candidate);
    }

    private sealed class PassthroughOrganisationService : IAudiobookOrganisationService
    {
        public Task<IReadOnlyList<AudiobookCandidateGroup>> PrepareProposalsAsync(
            Guid librarySourceId,
            IReadOnlyList<AudiobookCandidateGroup> candidates,
            CancellationToken cancellationToken = default) => Task.FromResult(candidates);
    }

    private sealed class PassthroughOnlineMetadataLookupService : IOnlineMetadataLookupService
    {
        public Task<IReadOnlyList<AudiobookCandidateGroup>> EnrichCandidatesAsync(
            Guid librarySourceId,
            IReadOnlyList<AudiobookCandidateGroup> candidates,
            IProgress<OnlineMetadataLookupProgress>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(candidates);
    }

    private sealed class EmptyMediaCatalogueService : IMediaCatalogueService
    {
        public Task<IReadOnlyList<MediaItem>> GetByLibrarySourceAsync(
            Guid librarySourceId,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MediaItem>>([]);
    }

    private sealed class StubLibrarySourceService : ILibrarySourceService
    {
        public Task<IReadOnlyList<LibrarySource>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LibrarySource>>([]);

        public Task<LibrarySource> CreateAsync(
            string name,
            string path,
            LibrarySourceType type,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<LibrarySource> UpdateAsync(
            Guid id,
            string name,
            string path,
            LibrarySourceType type,
            bool isEnabled,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubFolderPickerService : IFolderPickerService
    {
        public string? PickFolder(string? initialDirectory = null) => null;
    }

    private sealed class StubBackgroundScanService : IBackgroundScanService
    {
        public event Action<LibraryScanProgress>? ProgressChanged
        {
            add { }
            remove { }
        }

        public event Action<LibraryScanResult>? ScanCompleted
        {
            add { }
            remove { }
        }

        public event Action<Exception>? ScanFailed
        {
            add { }
            remove { }
        }

        public bool IsRunning => false;

        public Task<bool> QueueScanAsync(Guid librarySourceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public void Cancel()
        {
        }
    }

    private sealed class RecordingExecutionService : IAudiobookBatchExecutionService
    {
        public int ExecutionCount { get; private set; }
        public SynchronizationContext? ExecutionSynchronizationContext { get; private set; }

        public Task<AudiobookExecutionResult> ExecuteApprovedAsync(
            Guid librarySourceId,
            string libraryRoot,
            IReadOnlyList<AudiobookCandidateGroup> candidates,
            IProgress<AudiobookExecutionProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            ExecutionSynchronizationContext = SynchronizationContext.Current;
            return Task.FromResult(new AudiobookExecutionResult(
                Guid.NewGuid(),
                AudiobookExecutionRunStatus.Completed,
                1,
                1,
                0,
                "Execution complete: 1 file moved safely."));
        }

        public Task<AudiobookExecutionResult> RecoverInterruptedAsync(
            Guid librarySourceId,
            string libraryRoot,
            IProgress<AudiobookExecutionProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AudiobookExecutionResult(
                Guid.NewGuid(),
                AudiobookExecutionRunStatus.FailedRolledBack,
                1,
                1,
                1,
                "Interrupted execution recovered safely."));

        public Task<AudiobookExecutionRunEntry?> LoadLatestAsync(
            Guid librarySourceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AudiobookExecutionRunEntry?>(null);
    }

    private sealed class RecordingConfirmationService : IAudiobookExecutionConfirmationService
    {
        public int ExecutionConfirmationCount { get; private set; }

        public bool ConfirmExecution(int planCount, int operationCount)
        {
            ExecutionConfirmationCount++;
            return true;
        }

        public bool ConfirmRecovery(int operationCount) => true;
    }

    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => callback(state);
    }
}
