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
    public void PerfectEmbeddedSingleFileIdentity_IsPresentedAsAutomaticallyAccepted()
    {
        var viewModel = CreateViewModel(new CountingBatchPlanningService());

        viewModel.SelectedAudiobookCandidate = CreateCandidateWithBatchPlan();

        Assert.Contains(
            "accepted automatically",
            viewModel.AudiobookIdentityCorrectionStatus,
            StringComparison.OrdinalIgnoreCase);
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
    public async Task BulkApproval_OnlyApprovesCleanPerfectSingleFilePlans()
    {
        var source = new LibrarySource(
            "Audiobooks",
            Path.Combine(Path.GetTempPath(), "Metaroq.App.Tests"),
            LibrarySourceType.Audiobooks);
        var batchPlanningService = new CountingBatchPlanningService();
        var viewModel = CreateViewModel(batchPlanningService);
        viewModel.SelectedSource = source;
        await WaitUntilAsync(
            () => viewModel.AudiobookAnalysisStatus == "Saved analysis loaded",
            TimeSpan.FromSeconds(2));

        var eligible = CreateBulkApprovalCandidate("eligible", 1, 1m);
        var eligibleNoChange = CreateBulkApprovalCandidate(
            "eligible-no-change",
            1,
            1m,
            AudiobookBatchValidationStatus.NoChange);
        var multiFile = CreateBulkApprovalCandidate("multi", 7, 1m);
        var lessThanPerfect = CreateBulkApprovalCandidate("not-perfect", 1, 0.99m);
        var reviewRequired = CreateBulkApprovalCandidate(
            "review-required",
            1,
            1m,
            AudiobookBatchValidationStatus.ReviewRequired);
        viewModel.AudiobookCandidates =
            [eligible, eligibleNoChange, multiFile, lessThanPerfect, reviewRequired];

        Assert.Equal(2, viewModel.BulkApprovalEligibleCount);
        Assert.Equal(1, viewModel.IndividualApprovalRequiredCount);
        Assert.True(multiFile.RequiresIndividualApproval);
        Assert.True(multiFile.ReviewItemNeedsReview);
        Assert.Equal("Ready for individual approval", multiFile.BatchApprovalValidationLabel);

        await viewModel.ApproveSafeBatchCommand.ExecuteAsync(null);

        Assert.Equal(AudiobookBatchDecision.Approved, Assert.Single(batchPlanningService.Decisions));
        Assert.Equal(
            ["eligible", "eligible-no-change"],
            Assert.Single(batchPlanningService.PlanKeys).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(0, viewModel.BulkApprovalEligibleCount);
        Assert.Equal(
            AudiobookBatchDecision.Pending,
            viewModel.AudiobookCandidates.Single(candidate => candidate.BatchPlan?.PlanKey == "multi").BatchPlan?.Decision);

        viewModel.SelectedAudiobookCandidate = viewModel.AudiobookCandidates.Single(
            candidate => candidate.BatchPlan?.PlanKey == "multi");
        Assert.True(viewModel.ApproveSelectedBatchCommand.CanExecute(null));

        await viewModel.ApproveSelectedBatchCommand.ExecuteAsync(null);

        Assert.Equal("multi", Assert.Single(batchPlanningService.PlanKeys[1]));
        var approvedMultiFile = viewModel.AudiobookCandidates.Single(
            candidate => candidate.BatchPlan?.PlanKey == "multi");
        Assert.False(approvedMultiFile.IsIndividualApprovalPending);
        Assert.False(approvedMultiFile.ReviewItemNeedsReview);
        Assert.Equal("Individually approved", approvedMultiFile.BatchApprovalValidationLabel);
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
        var batchPlanningService = new CountingBatchPlanningService();
        var viewModel = CreateViewModel(
            batchPlanningService,
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
        Assert.Equal(AudiobookBatchDecision.Pending, Assert.Single(batchPlanningService.Decisions));
        Assert.Contains("Execution complete", viewModel.AudiobookExecutionStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteApproved_StaleSourceResetsApprovalsAndRefreshesCatalogue()
    {
        var source = new LibrarySource(
            "Audiobooks",
            Path.Combine(Path.GetTempPath(), "Metaroq.App.Tests"),
            LibrarySourceType.Audiobooks);
        var batchPlanningService = new CountingBatchPlanningService();
        var scanService = new RecordingBackgroundScanService();
        var missingRelativePath = Path.Combine("Author", "Missing Book.mp3");
        var viewModel = CreateViewModel(
            batchPlanningService,
            new StaleSourceExecutionService(Path.Combine(source.Path, missingRelativePath)),
            new RecordingConfirmationService(),
            scanService);
        viewModel.SelectedSource = source;
        await WaitUntilAsync(
            () => viewModel.AudiobookAnalysisStatus == "Saved analysis loaded",
            TimeSpan.FromSeconds(2));
        viewModel.AudiobookCandidates.Add(CreateCandidateWithBatchPlan(
            AudiobookBatchValidationStatus.Ready,
            AudiobookBatchDecision.Approved,
            includeOperation: true));

        await viewModel.ExecuteApprovedBatchCommand.ExecuteAsync(null);

        Assert.Equal(1, scanService.QueueCount);
        Assert.Equal(source.Id, scanService.LastLibrarySourceId);
        Assert.Equal(AudiobookBatchDecision.Pending, Assert.Single(batchPlanningService.Decisions));
        Assert.Empty(viewModel.AudiobookCandidates);
        Assert.Contains("before any files were changed", viewModel.AudiobookExecutionStatus, StringComparison.Ordinal);
        Assert.Contains(missingRelativePath, viewModel.AudiobookExecutionStatus, StringComparison.Ordinal);
        Assert.Contains("catalogue refresh has started", viewModel.AudiobookExecutionStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmGenre_RevalidatesSelectedPlanAndUpdatesOutcomeCounts()
    {
        var source = new LibrarySource(
            "Audiobooks",
            Path.Combine(Path.GetTempPath(), "Metaroq.App.Tests"),
            LibrarySourceType.Audiobooks);
        var batchPlanningService = new GenreCorrectionBatchPlanningService();
        var organisationService = new RecordingGenreCorrectionService();
        var viewModel = CreateViewModel(
            batchPlanningService,
            organisationService: organisationService);
        viewModel.SelectedSource = source;
        await WaitUntilAsync(
            () => viewModel.AudiobookAnalysisStatus == "Saved analysis loaded",
            TimeSpan.FromSeconds(2));
        var initialPrepareCount = batchPlanningService.PrepareCount;
        var candidate = CreateCandidateWithOrganisationPlan(isPrimary: true);
        viewModel.AudiobookCandidates.Add(candidate);
        viewModel.SelectedAudiobookCandidate = candidate;
        viewModel.SelectedAudiobookGenreOverride = "Science Fiction";

        await viewModel.ApplySelectedGenreOverrideCommand.ExecuteAsync(null);

        Assert.Equal(1, organisationService.SetCount);
        Assert.Equal("Science Fiction", organisationService.LastGenreCategory);
        Assert.Equal(initialPrepareCount + 1, batchPlanningService.PrepareCount);
        Assert.Equal(1, viewModel.BatchReadyCount);
        Assert.Equal(0, viewModel.MetadataReviewPlanCount);
        Assert.True(viewModel.SelectedAudiobookCandidate?.OrganisationProposal?.UsesManualGenre);
        Assert.Contains("plan has been revalidated", viewModel.AudiobookReviewCorrectionStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmIdentity_RevalidatesSelectedPlanAndUpdatesDisplayedIdentity()
    {
        var source = new LibrarySource(
            "Audiobooks",
            Path.Combine(Path.GetTempPath(), "Metaroq.App.Tests"),
            LibrarySourceType.Audiobooks);
        var batchPlanningService = new GenreCorrectionBatchPlanningService();
        var organisationService = new RecordingIdentityCorrectionService();
        var viewModel = CreateViewModel(
            batchPlanningService,
            organisationService: organisationService);
        viewModel.SelectedSource = source;
        await WaitUntilAsync(
            () => viewModel.AudiobookAnalysisStatus == "Saved analysis loaded",
            TimeSpan.FromSeconds(2));
        var initialPrepareCount = batchPlanningService.PrepareCount;
        var candidate = CreateCandidateWithOrganisationPlan(isPrimary: true);
        viewModel.AudiobookCandidates.Add(candidate);
        viewModel.SelectedAudiobookCandidate = candidate;
        viewModel.SelectedAudiobookAuthorOverride = "Roald Dahl";
        viewModel.SelectedAudiobookTitleOverride = "The BFG";

        await viewModel.ApplySelectedIdentityOverrideCommand.ExecuteAsync(null);

        Assert.Equal(1, organisationService.SetCount);
        Assert.Equal("Roald Dahl", organisationService.LastAuthor);
        Assert.Equal("The BFG", organisationService.LastTitle);
        Assert.Equal(initialPrepareCount + 1, batchPlanningService.PrepareCount);
        Assert.Equal("Roald Dahl", viewModel.SelectedAudiobookCandidate?.ReviewAuthorDisplay);
        Assert.Equal("The BFG", viewModel.SelectedAudiobookCandidate?.ReviewTitle);
        Assert.True(viewModel.SelectedAudiobookCandidate?.OrganisationProposal?.UsesManualAuthor);
        Assert.Contains("plan has been revalidated", viewModel.AudiobookIdentityCorrectionStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceEvidence_CombinesRelatedPlanFilesAndPlaysSelectedFile()
    {
        var audioPreviewService = new RecordingAudioPreviewService();
        var viewModel = CreateViewModel(
            new CountingBatchPlanningService(),
            audioPreviewService: audioPreviewService);
        var primary = CreateCandidateWithOrganisationPlanAndEvidence(
            isPrimary: true,
            "Chapter 02.mp3",
            2,
            "Chapter Two");
        var related = CreateCandidateWithOrganisationPlanAndEvidence(
            isPrimary: false,
            "Chapter 01.mp3",
            1,
            "Chapter One");
        viewModel.AudiobookCandidates = [primary, related];

        viewModel.SelectedAudiobookCandidate = primary;

        Assert.Equal(2, viewModel.SelectedAudiobookSourceEvidence.Count);
        Assert.Equal("Chapter 01.mp3", viewModel.SelectedAudiobookSourceEvidence[0].MediaItem.FileName);
        Assert.Equal("Chapter One", viewModel.SelectedAudiobookSourceEvidence[0].Metadata.TitleDisplay);
        Assert.Equal("Chapter 01.mp3", viewModel.SelectedAudiobookSourcePart?.MediaItem.FileName);
        Assert.True(viewModel.PlaySelectedAudiobookSourceCommand.CanExecute(null));

        await viewModel.PlaySelectedAudiobookSourceCommand.ExecuteAsync(null);

        Assert.Equal(1, audioPreviewService.PlayCount);
        Assert.Equal(
            viewModel.SelectedAudiobookSourcePart?.MediaItem.FullPath,
            audioPreviewService.LastPath);
        Assert.Contains("Opened Chapter 01.mp3", viewModel.AudiobookPreviewStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeparateCollection_SavesSeriesChoiceAndRevalidatesIndependentPlans()
    {
        var source = new LibrarySource(
            "Audiobooks",
            Path.Combine(Path.GetTempPath(), "Metaroq.App.Tests"),
            LibrarySourceType.Audiobooks);
        var organisationService = new RecordingCollectionCorrectionService();
        var viewModel = CreateViewModel(
            new GenreCorrectionBatchPlanningService(),
            organisationService: organisationService);
        viewModel.SelectedSource = source;
        await WaitUntilAsync(
            () => viewModel.AudiobookAnalysisStatus == "Saved analysis loaded",
            TimeSpan.FromSeconds(2));
        var candidate = CreateCandidateWithOrganisationPlanAndEvidence(
            true,
            "The 12th Planet Earth Chronicles Series, Book 1.m4b",
            1,
            "The 12th Planet Earth Chronicles Series, Book 1") with
        {
            OrganisationProposal = CreateCandidateWithOrganisationPlan(true).OrganisationProposal! with
            {
                SourceFileCount = 9
            }
        };
        viewModel.AudiobookCandidates.Add(candidate);
        viewModel.SelectedAudiobookCandidate = candidate;
        viewModel.SelectedAudiobookAuthorOverride = "Zecharia Sitchin";
        viewModel.SelectedAudiobookSeriesName = "Earth Chronicles";

        Assert.True(viewModel.SeparateSelectedCollectionCommand.CanExecute(null));
        await viewModel.SeparateSelectedCollectionCommand.ExecuteAsync(null);

        Assert.Equal(1, organisationService.SetCollectionCount);
        Assert.Equal(AudiobookCollectionHandling.SeparateBooks, organisationService.LastHandling);
        Assert.Equal("Zecharia Sitchin", organisationService.LastAuthor);
        Assert.Equal("Earth Chronicles", organisationService.LastSeriesName);
        Assert.Contains("independently", viewModel.AudiobookCollectionCorrectionStatus, StringComparison.Ordinal);
    }

    private static MainWindowViewModel CreateViewModel(
        IAudiobookBatchPlanningService batchPlanningService,
        IAudiobookBatchExecutionService? executionService = null,
        IAudiobookExecutionConfirmationService? confirmationService = null,
        IBackgroundScanService? backgroundScanService = null,
        IAudiobookOrganisationService? organisationService = null,
        IAudioPreviewService? audioPreviewService = null) =>
        new(
            Options.Create(new ArchivioOptions()),
            new StubLibrarySourceService(),
            new StubFolderPickerService(),
            backgroundScanService ?? new StubBackgroundScanService(),
            new EmptyMediaCatalogueService(),
            new SavedAnalysisService(),
            new PassthroughOnlineMetadataLookupService(),
            organisationService ?? new PassthroughOrganisationService(),
            batchPlanningService,
            executionService ?? new RecordingExecutionService(),
            confirmationService ?? new RecordingConfirmationService(),
            audioPreviewService ?? new RecordingAudioPreviewService());

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

    private static AudiobookCandidateGroup CreateBulkApprovalCandidate(
        string planKey,
        int sourceFileCount,
        decimal confidence,
        AudiobookBatchValidationStatus validationStatus = AudiobookBatchValidationStatus.Ready)
    {
        var template = CreateCandidateWithOrganisationPlan(isPrimary: true);
        return template with
        {
            DisplayName = planKey,
            Title = planKey,
            Confidence = confidence,
            Warnings = [],
            OrganisationProposal = template.OrganisationProposal! with
            {
                PlanKey = planKey,
                CanonicalTitle = planKey,
                RelatedCandidateCount = 1,
                SourceFileCount = sourceFileCount,
                Confidence = confidence,
                ReadyForAutomaticHandling = validationStatus is
                    AudiobookBatchValidationStatus.Ready or AudiobookBatchValidationStatus.NoChange,
                Warnings = []
            },
            BatchPlan = template.BatchPlan! with
            {
                PlanKey = planKey,
                CanonicalDisplay = $"Author - {planKey}",
                ValidationStatus = validationStatus,
                Warnings = []
            }
        };
    }

    private static AudiobookCandidateGroup CreateCandidateWithOrganisationPlanAndEvidence(
        bool isPrimary,
        string fileName,
        uint trackNumber,
        string embeddedTitle)
    {
        var sourceId = Guid.NewGuid();
        var relativePath = Path.Combine("Roald Dahl", "The BFG", fileName);
        var fullPath = Path.Combine(Path.GetTempPath(), "Metaroq.App.Tests", relativePath);
        var now = DateTime.UtcNow;
        var mediaItem = new MediaItem(sourceId, fullPath, relativePath, 1024, now, now, now);
        var metadata = new LocalMediaMetadata(
            fullPath,
            new MetadataValue(embeddedTitle, MetadataValueSource.EmbeddedTag),
            new MetadataValue("Roald Dahl", MetadataValueSource.EmbeddedTag),
            new MetadataValue("The BFG", MetadataValueSource.EmbeddedTag),
            new MetadataValue("Children & Young Adult", MetadataValueSource.EmbeddedTag),
            1982,
            trackNumber,
            TimeSpan.FromMinutes(8),
            128,
            44100,
            2,
            "MPEG Audio",
            false,
            [],
            []);
        var part = new AudiobookCandidatePart(mediaItem, (int)trackNumber, true, metadata);

        return CreateCandidateWithOrganisationPlan(isPrimary) with
        {
            Parts = [part]
        };
    }

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

        public Task<IReadOnlyList<AudiobookCandidateGroup>> SetGenreOverrideAsync(
            Guid librarySourceId,
            IReadOnlyList<AudiobookCandidateGroup> candidates,
            string planKey,
            string? genreCategory,
            CancellationToken cancellationToken = default) => Task.FromResult(candidates);

        public Task<IReadOnlyList<AudiobookCandidateGroup>> SetIdentityOverrideAsync(
            Guid librarySourceId,
            IReadOnlyList<AudiobookCandidateGroup> candidates,
            string planKey,
            string? canonicalAuthor,
            string? canonicalTitle,
            CancellationToken cancellationToken = default) => Task.FromResult(candidates);
    }

    private sealed class GenreCorrectionBatchPlanningService : IAudiobookBatchPlanningService
    {
        public int PrepareCount { get; private set; }

        public Task<IReadOnlyList<AudiobookCandidateGroup>> PrepareBatchAsync(
            Guid librarySourceId,
            string libraryRoot,
            IReadOnlyList<AudiobookCandidateGroup> candidates,
            IReadOnlyList<MediaItem> indexedMedia,
            CancellationToken cancellationToken = default)
        {
            PrepareCount++;
            return Task.FromResult(candidates);
        }

        public Task<IReadOnlyList<AudiobookCandidateGroup>> SetDecisionAsync(
            Guid librarySourceId,
            IReadOnlyList<AudiobookCandidateGroup> candidates,
            IReadOnlyCollection<string> planKeys,
            AudiobookBatchDecision decision,
            CancellationToken cancellationToken = default) => Task.FromResult(candidates);
    }

    private sealed class RecordingGenreCorrectionService : IAudiobookOrganisationService
    {
        public int SetCount { get; private set; }
        public string? LastGenreCategory { get; private set; }

        public Task<IReadOnlyList<AudiobookCandidateGroup>> PrepareProposalsAsync(
            Guid librarySourceId,
            IReadOnlyList<AudiobookCandidateGroup> candidates,
            CancellationToken cancellationToken = default) => Task.FromResult(candidates);

        public Task<IReadOnlyList<AudiobookCandidateGroup>> SetGenreOverrideAsync(
            Guid librarySourceId,
            IReadOnlyList<AudiobookCandidateGroup> candidates,
            string planKey,
            string? genreCategory,
            CancellationToken cancellationToken = default)
        {
            SetCount++;
            LastGenreCategory = genreCategory;
            var updated = candidates.Select(candidate =>
            {
                if (candidate.OrganisationProposal?.PlanKey != planKey)
                {
                    return candidate;
                }

                return candidate with
                {
                    OrganisationProposal = candidate.OrganisationProposal with
                    {
                        GenreCategory = genreCategory ?? "Uncategorised",
                        ReadyForAutomaticHandling = genreCategory is not null,
                        UsesManualGenre = genreCategory is not null
                    },
                    BatchPlan = candidate.BatchPlan is null
                        ? null
                        : candidate.BatchPlan with
                        {
                            ValidationStatus = genreCategory is null
                                ? AudiobookBatchValidationStatus.ReviewRequired
                                : AudiobookBatchValidationStatus.Ready
                        }
                };
            }).ToList();
            return Task.FromResult<IReadOnlyList<AudiobookCandidateGroup>>(updated);
        }

        public Task<IReadOnlyList<AudiobookCandidateGroup>> SetIdentityOverrideAsync(
            Guid librarySourceId,
            IReadOnlyList<AudiobookCandidateGroup> candidates,
            string planKey,
            string? canonicalAuthor,
            string? canonicalTitle,
            CancellationToken cancellationToken = default) => Task.FromResult(candidates);
    }

    private sealed class RecordingIdentityCorrectionService : IAudiobookOrganisationService
    {
        public int SetCount { get; private set; }
        public string? LastAuthor { get; private set; }
        public string? LastTitle { get; private set; }

        public Task<IReadOnlyList<AudiobookCandidateGroup>> PrepareProposalsAsync(
            Guid librarySourceId,
            IReadOnlyList<AudiobookCandidateGroup> candidates,
            CancellationToken cancellationToken = default) => Task.FromResult(candidates);

        public Task<IReadOnlyList<AudiobookCandidateGroup>> SetGenreOverrideAsync(
            Guid librarySourceId,
            IReadOnlyList<AudiobookCandidateGroup> candidates,
            string planKey,
            string? genreCategory,
            CancellationToken cancellationToken = default) => Task.FromResult(candidates);

        public Task<IReadOnlyList<AudiobookCandidateGroup>> SetIdentityOverrideAsync(
            Guid librarySourceId,
            IReadOnlyList<AudiobookCandidateGroup> candidates,
            string planKey,
            string? canonicalAuthor,
            string? canonicalTitle,
            CancellationToken cancellationToken = default)
        {
            SetCount++;
            LastAuthor = canonicalAuthor;
            LastTitle = canonicalTitle;
            var updated = candidates.Select(candidate =>
            {
                if (candidate.OrganisationProposal?.PlanKey != planKey)
                {
                    return candidate;
                }

                return candidate with
                {
                    OrganisationProposal = candidate.OrganisationProposal with
                    {
                        CanonicalAuthor = canonicalAuthor ?? "Unknown Author",
                        CanonicalTitle = canonicalTitle ?? candidate.Title,
                        UsesManualAuthor = canonicalAuthor is not null,
                        UsesManualTitle = canonicalTitle is not null
                    }
                };
            }).ToList();
            return Task.FromResult<IReadOnlyList<AudiobookCandidateGroup>>(updated);
        }
    }

    private sealed class RecordingCollectionCorrectionService : IAudiobookOrganisationService
    {
        public int SetCollectionCount { get; private set; }
        public AudiobookCollectionHandling LastHandling { get; private set; }
        public string? LastAuthor { get; private set; }
        public string? LastSeriesName { get; private set; }

        public Task<IReadOnlyList<AudiobookCandidateGroup>> PrepareProposalsAsync(
            Guid librarySourceId,
            IReadOnlyList<AudiobookCandidateGroup> candidates,
            CancellationToken cancellationToken = default) => Task.FromResult(candidates);

        public Task<IReadOnlyList<AudiobookCandidateGroup>> SetGenreOverrideAsync(
            Guid librarySourceId,
            IReadOnlyList<AudiobookCandidateGroup> candidates,
            string planKey,
            string? genreCategory,
            CancellationToken cancellationToken = default) => Task.FromResult(candidates);

        public Task<IReadOnlyList<AudiobookCandidateGroup>> SetIdentityOverrideAsync(
            Guid librarySourceId,
            IReadOnlyList<AudiobookCandidateGroup> candidates,
            string planKey,
            string? canonicalAuthor,
            string? canonicalTitle,
            CancellationToken cancellationToken = default) => Task.FromResult(candidates);

        public Task<IReadOnlyList<AudiobookCandidateGroup>> SetCollectionOverrideAsync(
            Guid librarySourceId,
            IReadOnlyList<AudiobookCandidateGroup> candidates,
            string planKey,
            AudiobookCollectionHandling collectionHandling,
            string? canonicalAuthor,
            string? seriesName,
            CancellationToken cancellationToken = default)
        {
            SetCollectionCount++;
            LastHandling = collectionHandling;
            LastAuthor = canonicalAuthor;
            LastSeriesName = seriesName;
            var updated = candidates.Select(candidate => candidate.OrganisationProposal?.PlanKey == planKey
                ? candidate with
                {
                    OrganisationProposal = candidate.OrganisationProposal with
                    {
                        SourceFileCount = 1,
                        CollectionHandling = AudiobookCollectionHandling.SeparateBooks,
                        SeriesName = seriesName,
                        CollectionPlanKey = planKey
                    }
                }
                : candidate).ToList();
            return Task.FromResult<IReadOnlyList<AudiobookCandidateGroup>>(updated);
        }
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

    private sealed class StaleSourceExecutionService(string missingPath) : IAudiobookBatchExecutionService
    {
        public Task<AudiobookExecutionResult> ExecuteApprovedAsync(
            Guid librarySourceId,
            string libraryRoot,
            IReadOnlyList<AudiobookCandidateGroup> candidates,
            IProgress<AudiobookExecutionProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<AudiobookExecutionResult>(
                new FileNotFoundException("An approved source file is no longer available.", missingPath));

        public Task<AudiobookExecutionResult> RecoverInterruptedAsync(
            Guid librarySourceId,
            string libraryRoot,
            IProgress<AudiobookExecutionProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AudiobookExecutionRunEntry?> LoadLatestAsync(
            Guid librarySourceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AudiobookExecutionRunEntry?>(null);
    }

    private sealed class RecordingBackgroundScanService : IBackgroundScanService
    {
        public int QueueCount { get; private set; }
        public Guid? LastLibrarySourceId { get; private set; }

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
        public bool IsRunning => QueueCount > 0;

        public Task<bool> QueueScanAsync(
            Guid librarySourceId,
            CancellationToken cancellationToken = default)
        {
            QueueCount++;
            LastLibrarySourceId = librarySourceId;
            return Task.FromResult(true);
        }

        public void Cancel()
        {
        }
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

    private sealed class RecordingAudioPreviewService : IAudioPreviewService
    {
        public int PlayCount { get; private set; }
        public string? LastPath { get; private set; }

        public Task PlayAsync(string filePath, CancellationToken cancellationToken = default)
        {
            PlayCount++;
            LastPath = filePath;
            return Task.CompletedTask;
        }
    }

    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => callback(state);
    }
}
