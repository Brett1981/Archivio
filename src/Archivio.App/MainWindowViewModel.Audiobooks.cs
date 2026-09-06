using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using Archivio.Application.Abstractions;
using Archivio.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Archivio.App;

public sealed partial class MainWindowViewModel
{
    private static readonly TimeSpan AudiobookExecutionProgressInterval = TimeSpan.FromMilliseconds(125);

    private readonly IAudiobookAnalysisService _audiobookAnalysisService;
    private readonly IOnlineMetadataLookupService _onlineMetadataLookupService;
    private readonly IAudiobookOrganisationService _audiobookOrganisationService;
    private readonly IAudiobookBatchPlanningService _audiobookBatchPlanningService;
    private readonly IAudiobookBatchExecutionService _audiobookBatchExecutionService;
    private readonly IAudiobookExecutionConfirmationService _audiobookExecutionConfirmationService;
    private CancellationTokenSource? _audiobookAnalysisCancellation;
    private CancellationTokenSource? _audiobookExecutionCancellation;
    private ICollectionView? _audiobookCandidatesView;

    [ObservableProperty]
    private ObservableCollection<AudiobookCandidateGroup> _audiobookCandidates = [];

    public ICollectionView AudiobookCandidatesView
    {
        get
        {
            if (_audiobookCandidatesView is null)
            {
                _audiobookCandidatesView = CollectionViewSource.GetDefaultView(AudiobookCandidates);
                _audiobookCandidatesView.Filter = FilterAudiobookCandidate;
                _audiobookCandidatesView.CollectionChanged += (_, _) =>
                {
                    OnPropertyChanged(nameof(VisibleAudiobookCandidateCount));
                    OnPropertyChanged(nameof(VisibleAudiobookReviewItemCount));
                    OnPropertyChanged(nameof(AudiobookReviewCountLabel));
                };
            }

            return _audiobookCandidatesView;
        }
    }

    public int AudiobookCandidateCount => AudiobookCandidates.Count;
    public int VisibleAudiobookCandidateCount => AudiobookCandidatesView.Cast<object>().Count();
    public bool HasAudiobookOrganisationPlans => OrganisationPlanCount > 0;
    public int AudiobookReviewItemCount => HasAudiobookOrganisationPlans
        ? OrganisationPlanCount
        : AudiobookCandidateCount;
    public int VisibleAudiobookReviewItemCount => AudiobookCandidatesView.Cast<object>().Count();
    public string AudiobookReviewCountLabel => HasAudiobookOrganisationPlans
        ? $"{VisibleAudiobookReviewItemCount:N0} of {AudiobookReviewItemCount:N0} audiobook plans"
        : $"{VisibleAudiobookReviewItemCount:N0} of {AudiobookReviewItemCount:N0} audiobook candidates";
    public int MultipartAudiobookCount => AudiobookCandidates.Count(candidate => candidate.IsMultipart);
    public int SingleFileAudiobookCount => AudiobookCandidates.Count(candidate => !candidate.IsMultipart);
    public int AudiobooksNeedingReviewCount => HasAudiobookOrganisationPlans
        ? AudiobookCandidates.Count(candidate => candidate.IsPrimaryOrganisationPlan && candidate.ReviewItemNeedsReview)
        : AudiobookCandidates.Count(candidate => candidate.NeedsReview);
    public int OnlineSuggestionCount => AudiobookCandidates.Count(candidate => candidate.HasOnlineSuggestion);
    public int OrganisationPlanCount => AudiobookCandidates.Count(candidate => candidate.IsPrimaryOrganisationPlan);
    public int AutomaticReadyPlanCount => AudiobookCandidates.Count(candidate =>
        candidate.IsPrimaryOrganisationPlan &&
        candidate.OrganisationProposal?.ReadyForAutomaticHandling == true);
    public int GroupedOrganisationPlanCount => AudiobookCandidates.Count(candidate =>
        candidate.IsPrimaryOrganisationPlan &&
        (candidate.OrganisationProposal?.RelatedCandidateCount > 1 ||
         candidate.OrganisationProposal?.SourceFileCount > 1));
    public int BatchPlanCount => AudiobookCandidates.Count(candidate =>
        candidate.IsPrimaryOrganisationPlan && candidate.BatchPlan is not null);
    public int BatchReadyCount => AudiobookCandidates.Count(candidate =>
        candidate.IsPrimaryOrganisationPlan &&
        candidate.BatchPlan?.ValidationStatus == AudiobookBatchValidationStatus.Ready);
    public int BulkApprovalEligibleCount => AudiobookCandidates.Count(candidate => candidate.CanBulkApprove);
    public int IndividualApprovalRequiredCount => AudiobookCandidates.Count(candidate =>
        candidate.IsIndividualApprovalPending);
    public string ApproveSafeBatchLabel => $"Bulk approve safe single files ({BulkApprovalEligibleCount:N0})";
    public int BatchApprovedCount => AudiobookCandidates.Count(candidate =>
        candidate.IsPrimaryOrganisationPlan &&
        candidate.BatchPlan?.Decision == AudiobookBatchDecision.Approved);
    public int BatchBlockedCount => AudiobookCandidates.Count(candidate =>
        candidate.IsPrimaryOrganisationPlan && candidate.BatchPlan?.IsBlocked == true);
    public int AlreadyOrganisedPlanCount => AudiobookCandidates.Count(candidate =>
        candidate.IsPrimaryOrganisationPlan &&
        candidate.BatchPlan?.ValidationStatus == AudiobookBatchValidationStatus.NoChange);
    public int MetadataReviewPlanCount => AudiobookCandidates.Count(candidate =>
        candidate.IsPrimaryOrganisationPlan &&
        candidate.BatchPlan?.ValidationStatus == AudiobookBatchValidationStatus.ReviewRequired);
    public int BatchConflictCount => AudiobookCandidates.Count(candidate =>
        candidate.IsPrimaryOrganisationPlan &&
        candidate.BatchPlan?.ValidationStatus == AudiobookBatchValidationStatus.Conflict);
    public IReadOnlyList<string> AudiobookGenreCategories =>
        global::Archivio.Application.Abstractions.AudiobookGenreCategories.All;
    public int BatchExecutableOperationCount => AudiobookCandidates
        .Select(candidate => candidate.BatchPlan)
        .Where(plan => plan is
        {
            ValidationStatus: AudiobookBatchValidationStatus.Ready,
            Decision: AudiobookBatchDecision.Approved
        })
        .Select(plan => plan!)
        .GroupBy(plan => plan.PlanKey, StringComparer.Ordinal)
        .Select(group => group.First())
        .Sum(plan => plan.Operations.Count(operation => operation.Kind != AudiobookFileOperationKind.NoChange));
    public int AudiobookAnalysisProgressMaximum => Math.Max(1, AudiobookAnalysisTotalCount);
    public int AudiobookExecutionProgressMaximum => Math.Max(1, AudiobookExecutionTotalCount);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApproveSelectedBatchCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeferSelectedBatchCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetSelectedBatchDecisionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplySelectedGenreOverrideCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetSelectedGenreOverrideCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplySelectedIdentityOverrideCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetSelectedIdentityOverrideCommand))]
    [NotifyCanExecuteChangedFor(nameof(SeparateSelectedCollectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplySelectedSeriesOverrideCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetSelectedSeriesOverrideCommand))]
    private AudiobookCandidateGroup? _selectedAudiobookCandidate;

    [ObservableProperty]
    private IReadOnlyList<AudiobookCandidatePart> _selectedAudiobookSourceEvidence = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlaySelectedAudiobookSourceCommand))]
    private AudiobookCandidatePart? _selectedAudiobookSourcePart;

    [ObservableProperty]
    private string _audiobookPreviewStatus =
        "Select a source file to inspect its filename and embedded metadata.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplySelectedIdentityOverrideCommand))]
    private string _selectedAudiobookAuthorOverride = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplySelectedIdentityOverrideCommand))]
    private string _selectedAudiobookTitleOverride = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplySelectedGenreOverrideCommand))]
    private string? _selectedAudiobookGenreOverride;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplySelectedSeriesOverrideCommand))]
    private string _selectedAudiobookSeriesName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplySelectedSeriesOverrideCommand))]
    private string _selectedAudiobookSeriesPosition = string.Empty;

    [ObservableProperty]
    private string _audiobookIdentityCorrectionStatus =
        "Confirm the complete author and audiobook title; no files will be changed.";

    [ObservableProperty]
    private string _audiobookReviewCorrectionStatus =
        "Choose a genre to correct this audiobook plan; no files will be changed.";

    [ObservableProperty]
    private string _audiobookCollectionCorrectionStatus =
        "Choose separate books when each source file is a complete audiobook.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplySelectedGenreOverrideCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetSelectedGenreOverrideCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplySelectedIdentityOverrideCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetSelectedIdentityOverrideCommand))]
    [NotifyCanExecuteChangedFor(nameof(SeparateSelectedCollectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplySelectedSeriesOverrideCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetSelectedSeriesOverrideCommand))]
    private bool _isAudiobookReviewCorrectionRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyseAudiobooksCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelAudiobookAnalysisCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApproveSafeBatchCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApproveSelectedBatchCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeferSelectedBatchCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetSelectedBatchDecisionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplySelectedGenreOverrideCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetSelectedGenreOverrideCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplySelectedIdentityOverrideCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetSelectedIdentityOverrideCommand))]
    [NotifyCanExecuteChangedFor(nameof(SeparateSelectedCollectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplySelectedSeriesOverrideCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetSelectedSeriesOverrideCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteApprovedBatchCommand))]
    private bool _isAudiobookAnalysisRunning;

    [ObservableProperty]
    private string _audiobookAnalysisStatus = "Ready to analyse indexed audio";

    [ObservableProperty]
    private string _audiobookAnalysisCurrentPath = string.Empty;

    [ObservableProperty]
    private int _audiobookAnalysisProcessedCount;

    [ObservableProperty]
    private int _audiobookAnalysisTotalCount;

    [ObservableProperty]
    private int _audiobookAnalysisWarningCount;

    [ObservableProperty]
    private string _audiobookAnalysisStorageStatus = "Analysis has not been saved";

    [ObservableProperty]
    private string _onlineMetadataStatus = "Online fallback has not run";

    [ObservableProperty]
    private string _organisationStatus = "Organisation plans have not been prepared";

    [ObservableProperty]
    private string _batchPlanningStatus = "Batch dry run has not been prepared";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteApprovedBatchCommand))]
    [NotifyCanExecuteChangedFor(nameof(RecoverInterruptedExecutionCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelAudiobookExecutionCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnalyseAudiobooksCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplySelectedGenreOverrideCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetSelectedGenreOverrideCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplySelectedIdentityOverrideCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetSelectedIdentityOverrideCommand))]
    [NotifyCanExecuteChangedFor(nameof(SeparateSelectedCollectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplySelectedSeriesOverrideCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetSelectedSeriesOverrideCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApproveSafeBatchCommand))]
    private bool _isAudiobookExecutionRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecoverInterruptedExecutionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteApprovedBatchCommand))]
    private bool _hasInterruptedAudiobookExecution;

    [ObservableProperty]
    private string _audiobookExecutionStatus = "No batch execution has run";

    [ObservableProperty]
    private string _audiobookExecutionCurrentPath = string.Empty;

    [ObservableProperty]
    private int _audiobookExecutionProcessedCount;

    [ObservableProperty]
    private int _audiobookExecutionTotalCount;

    [ObservableProperty]
    private bool _isReviewFocusMode;

    [ObservableProperty]
    private string _audiobookSearchText = string.Empty;

    [ObservableProperty]
    private bool _showAudiobooksNeedingReviewOnly;

    [ObservableProperty]
    private bool _showAudiobookMetadataWarningsOnly;

    [ObservableProperty]
    private bool _showMultipartAudiobooksOnly;

    [ObservableProperty]
    private bool _showOnlineSuggestionsOnly;

    [ObservableProperty]
    private bool _showOrganisationPlansOnly;

    [ObservableProperty]
    private bool _showAutomaticReadyPlansOnly;

    partial void OnAudiobookCandidatesChanged(ObservableCollection<AudiobookCandidateGroup> value)
    {
        _audiobookCandidatesView = null;
        OnPropertyChanged(nameof(AudiobookCandidatesView));
        NotifyAudiobookSummaryChanged();
    }

    partial void OnSelectedAudiobookCandidateChanged(AudiobookCandidateGroup? value)
    {
        var proposal = value?.OrganisationProposal;
        SelectedAudiobookSourceEvidence = proposal is null
            ? value?.Parts ?? []
            : AudiobookCandidates
                .Where(candidate => string.Equals(
                    candidate.OrganisationProposal?.PlanKey,
                    proposal.PlanKey,
                    StringComparison.Ordinal))
                .SelectMany(candidate => candidate.Parts)
                .GroupBy(part => part.MediaItem.Id)
                .Select(group => group.First())
                .OrderBy(part => part.Metadata.TrackNumber ?? uint.MaxValue)
                .ThenBy(part => part.Sequence)
                .ThenBy(part => part.MediaItem.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        SelectedAudiobookSourcePart = SelectedAudiobookSourceEvidence.FirstOrDefault();
        AudiobookPreviewStatus = SelectedAudiobookSourcePart is null
            ? "No source-file evidence is available for this plan."
            : "Review the original evidence below or play the selected source file.";
        SelectedAudiobookAuthorOverride = proposal?.CanonicalAuthor == "Unknown Author"
            ? string.Empty
            : proposal?.CanonicalAuthor ?? string.Empty;
        SelectedAudiobookTitleOverride = proposal?.CanonicalTitle ?? string.Empty;
        SelectedAudiobookGenreOverride = proposal?.GenreCategory == "Uncategorised"
            ? null
            : proposal?.GenreCategory;
        SelectedAudiobookSeriesName = proposal?.SeriesName ?? string.Empty;
        SelectedAudiobookSeriesPosition = proposal?.SeriesPosition?.ToString() ?? string.Empty;
        AudiobookIdentityCorrectionStatus = proposal?.UsesManualAuthor == true ||
                                            proposal?.UsesManualTitle == true
            ? $"Author and title confirmed by you as {proposal.CanonicalAuthor} — {proposal.CanonicalTitle}."
            : "Confirm the complete author and audiobook title; no files will be changed.";
        AudiobookReviewCorrectionStatus = proposal?.UsesManualGenre == true
            ? $"Genre confirmed by you as {proposal.GenreCategory}."
            : "Choose a genre to correct this audiobook plan; no files will be changed.";
        AudiobookCollectionCorrectionStatus = proposal?.IsSeparateBookPlan == true
            ? $"This is one book{(string.IsNullOrWhiteSpace(proposal.SeriesName) ? string.Empty : $" in {proposal.SeriesDisplay}")}."
            : "Choose separate books when each source file is a complete audiobook.";
        ApplySelectedIdentityOverrideCommand.NotifyCanExecuteChanged();
        ResetSelectedIdentityOverrideCommand.NotifyCanExecuteChanged();
        ApplySelectedGenreOverrideCommand.NotifyCanExecuteChanged();
        ResetSelectedGenreOverrideCommand.NotifyCanExecuteChanged();
        SeparateSelectedCollectionCommand.NotifyCanExecuteChanged();
        ApplySelectedSeriesOverrideCommand.NotifyCanExecuteChanged();
        ResetSelectedSeriesOverrideCommand.NotifyCanExecuteChanged();
    }

    partial void OnAudiobookSearchTextChanged(string value) => RefreshAudiobookCandidatesView();
    partial void OnShowAudiobooksNeedingReviewOnlyChanged(bool value)
    {
        if (value)
        {
            ShowOrganisationPlansOnly = false;
            ShowAutomaticReadyPlansOnly = false;
        }

        RefreshAudiobookCandidatesView();
    }

    partial void OnShowAudiobookMetadataWarningsOnlyChanged(bool value) => RefreshAudiobookCandidatesView();
    partial void OnShowMultipartAudiobooksOnlyChanged(bool value) => RefreshAudiobookCandidatesView();
    partial void OnShowOnlineSuggestionsOnlyChanged(bool value) => RefreshAudiobookCandidatesView();

    partial void OnShowOrganisationPlansOnlyChanged(bool value)
    {
        if (value)
        {
            ShowAudiobooksNeedingReviewOnly = false;
            ShowAutomaticReadyPlansOnly = false;
        }

        RefreshAudiobookCandidatesView();
    }

    partial void OnShowAutomaticReadyPlansOnlyChanged(bool value)
    {
        if (value)
        {
            ShowAudiobooksNeedingReviewOnly = false;
            ShowOrganisationPlansOnly = false;
        }

        RefreshAudiobookCandidatesView();
    }

    [RelayCommand]
    private void EnterReviewFocus() => IsReviewFocusMode = true;

    [RelayCommand]
    private void ExitReviewFocus() => IsReviewFocusMode = false;

    private bool CanPlaySelectedAudiobookSource() =>
        SelectedAudiobookSourcePart?.MediaItem.IsMissing == false;

    [RelayCommand(CanExecute = nameof(CanPlaySelectedAudiobookSource))]
    private async Task PlaySelectedAudiobookSourceAsync()
    {
        var part = SelectedAudiobookSourcePart;
        if (part is null)
        {
            return;
        }

        AudiobookPreviewStatus = $"Opening {part.MediaItem.FileName} in your default audio player...";
        try
        {
            await _audioPreviewService.PlayAsync(part.MediaItem.FullPath);
            AudiobookPreviewStatus = $"Opened {part.MediaItem.FileName} in your default audio player.";
        }
        catch (Exception exception)
        {
            AudiobookPreviewStatus = $"The source file could not be played: {exception.Message}";
        }
    }

    private bool CanSeparateSelectedCollection() =>
        !IsAudiobookAnalysisRunning &&
        !IsAudiobookExecutionRunning &&
        !IsAudiobookReviewCorrectionRunning &&
        SelectedSource is not null &&
        SelectedAudiobookCandidate?.IsPrimaryOrganisationPlan == true &&
        SelectedAudiobookCandidate.OrganisationProposal is
        {
            SourceFileCount: > 1,
            IsSeparateBookPlan: false
        };

    [RelayCommand(CanExecute = nameof(CanSeparateSelectedCollection))]
    private async Task SeparateSelectedCollectionAsync()
    {
        var source = SelectedSource;
        var selected = SelectedAudiobookCandidate;
        var plan = selected?.OrganisationProposal;
        if (source is null || selected is null || plan is null)
        {
            return;
        }

        var selectedMediaItemId = SelectedAudiobookSourcePart?.MediaItem.Id ??
                                  selected.Parts.FirstOrDefault()?.MediaItem.Id;
        IsAudiobookReviewCorrectionRunning = true;
        AudiobookCollectionCorrectionStatus =
            $"Separating {plan.SourceFileCount:N0} source files into independent audiobook plans and checking each title online...";
        try
        {
            var updated = await _audiobookOrganisationService.SetCollectionOverrideAsync(
                source.Id,
                AudiobookCandidates.ToList(),
                plan.PlanKey,
                AudiobookCollectionHandling.SeparateBooks,
                SelectedAudiobookAuthorOverride,
                SelectedAudiobookSeriesName);
            updated = await EnrichWithOnlineMetadataAsync(source.Id, updated, CancellationToken.None);
            updated = await _audiobookOrganisationService.PrepareProposalsAsync(
                source.Id,
                updated,
                CancellationToken.None);
            updated = await _audiobookBatchPlanningService.PrepareBatchAsync(
                source.Id,
                source.Path,
                updated,
                MediaItems.ToList());
            if (SelectedSource?.Id != source.Id)
            {
                return;
            }

            AudiobookCandidates = new ObservableCollection<AudiobookCandidateGroup>(updated);
            SelectedAudiobookCandidate = AudiobookCandidates.FirstOrDefault(candidate =>
                selectedMediaItemId is not null &&
                candidate.Parts.Any(part => part.MediaItem.Id == selectedMediaItemId)) ??
                FirstAudiobookReviewItem();
            NotifyAudiobookSummaryChanged();
            UpdateBatchPlanningStatus(updated, "Collection separated and revalidated");
            AudiobookCollectionCorrectionStatus =
                $"Separated into {plan.SourceFileCount:N0} audiobook plans; each title was checked independently.";
            OrganisationStatus = AudiobookCollectionCorrectionStatus;
        }
        catch (Exception exception)
        {
            AudiobookCollectionCorrectionStatus = $"The collection could not be separated: {exception.Message}";
        }
        finally
        {
            IsAudiobookReviewCorrectionRunning = false;
        }
    }

    private bool CanApplySelectedSeriesOverride() =>
        !IsAudiobookAnalysisRunning &&
        !IsAudiobookExecutionRunning &&
        !IsAudiobookReviewCorrectionRunning &&
        SelectedSource is not null &&
        SelectedAudiobookCandidate?.IsPrimaryOrganisationPlan == true &&
        !string.IsNullOrWhiteSpace(SelectedAudiobookSeriesName) &&
        (string.IsNullOrWhiteSpace(SelectedAudiobookSeriesPosition) ||
         int.TryParse(SelectedAudiobookSeriesPosition, out var position) && position > 0);

    [RelayCommand(CanExecute = nameof(CanApplySelectedSeriesOverride))]
    private Task ApplySelectedSeriesOverrideAsync() => SetSelectedSeriesOverrideAsync(
        SelectedAudiobookSeriesName,
        int.TryParse(SelectedAudiobookSeriesPosition, out var position) ? position : null);

    private bool CanResetSelectedSeriesOverride() =>
        !IsAudiobookAnalysisRunning &&
        !IsAudiobookExecutionRunning &&
        !IsAudiobookReviewCorrectionRunning &&
        SelectedSource is not null &&
        SelectedAudiobookCandidate?.OrganisationProposal?.SeriesName is not null;

    [RelayCommand(CanExecute = nameof(CanResetSelectedSeriesOverride))]
    private Task ResetSelectedSeriesOverrideAsync() => SetSelectedSeriesOverrideAsync(null, null);

    private async Task SetSelectedSeriesOverrideAsync(string? seriesName, int? seriesPosition)
    {
        var source = SelectedSource;
        var selected = SelectedAudiobookCandidate;
        var plan = selected?.OrganisationProposal;
        if (source is null || selected is null || plan is null)
        {
            return;
        }

        var selectedCandidateKey = selected.CandidateKey;
        IsAudiobookReviewCorrectionRunning = true;
        AudiobookCollectionCorrectionStatus = seriesName is null
            ? "Clearing the series details and revalidating the plan..."
            : "Saving the series details and revalidating the plan...";
        try
        {
            var updated = await _audiobookOrganisationService.SetSeriesOverrideAsync(
                source.Id,
                AudiobookCandidates.ToList(),
                plan.PlanKey,
                seriesName,
                seriesPosition);
            updated = await _audiobookBatchPlanningService.PrepareBatchAsync(
                source.Id,
                source.Path,
                updated,
                MediaItems.ToList());
            AudiobookCandidates = new ObservableCollection<AudiobookCandidateGroup>(updated);
            SelectedAudiobookCandidate = AudiobookCandidates.FirstOrDefault(candidate =>
                candidate.CandidateKey == selectedCandidateKey);
            NotifyAudiobookSummaryChanged();
            UpdateBatchPlanningStatus(updated, "Series metadata updated");
            AudiobookCollectionCorrectionStatus = seriesName is null
                ? "Series details cleared; the plan has been revalidated."
                : $"Series saved as {seriesName}{(seriesPosition is null ? string.Empty : $", book {seriesPosition}")}; the plan has been revalidated.";
            OrganisationStatus = AudiobookCollectionCorrectionStatus;
        }
        catch (Exception exception)
        {
            AudiobookCollectionCorrectionStatus = $"Series details could not be saved: {exception.Message}";
        }
        finally
        {
            IsAudiobookReviewCorrectionRunning = false;
        }
    }

    private bool CanApplySelectedIdentityOverride() =>
        !IsAudiobookAnalysisRunning &&
        !IsAudiobookExecutionRunning &&
        !IsAudiobookReviewCorrectionRunning &&
        SelectedSource is not null &&
        SelectedAudiobookCandidate?.IsPrimaryOrganisationPlan == true &&
        SelectedAudiobookCandidate.OrganisationProposal is not null &&
        !string.IsNullOrWhiteSpace(SelectedAudiobookAuthorOverride) &&
        !string.IsNullOrWhiteSpace(SelectedAudiobookTitleOverride);

    [RelayCommand(CanExecute = nameof(CanApplySelectedIdentityOverride))]
    private Task ApplySelectedIdentityOverrideAsync() => SetSelectedIdentityOverrideAsync(
        SelectedAudiobookAuthorOverride,
        SelectedAudiobookTitleOverride);

    private bool CanResetSelectedIdentityOverride() =>
        !IsAudiobookAnalysisRunning &&
        !IsAudiobookExecutionRunning &&
        !IsAudiobookReviewCorrectionRunning &&
        SelectedSource is not null &&
        SelectedAudiobookCandidate?.OrganisationProposal is
    { UsesManualAuthor: true } or
    { UsesManualTitle: true };

    [RelayCommand(CanExecute = nameof(CanResetSelectedIdentityOverride))]
    private Task ResetSelectedIdentityOverrideAsync() => SetSelectedIdentityOverrideAsync(null, null);

    private async Task SetSelectedIdentityOverrideAsync(string? canonicalAuthor, string? canonicalTitle)
    {
        var source = SelectedSource;
        var selected = SelectedAudiobookCandidate;
        var plan = selected?.OrganisationProposal;
        if (source is null || selected is null || plan is null)
        {
            return;
        }

        var selectedCandidateKey = selected.CandidateKey;
        IsAudiobookReviewCorrectionRunning = true;
        AudiobookIdentityCorrectionStatus = canonicalAuthor is null && canonicalTitle is null
            ? "Restoring Metaroq's suggested author and title..."
            : "Confirming the author and title and revalidating the plan...";
        try
        {
            var updated = await _audiobookOrganisationService.SetIdentityOverrideAsync(
                source.Id,
                AudiobookCandidates.ToList(),
                plan.PlanKey,
                canonicalAuthor,
                canonicalTitle);
            updated = await _audiobookBatchPlanningService.PrepareBatchAsync(
                source.Id,
                source.Path,
                updated,
                MediaItems.ToList());
            if (SelectedSource?.Id != source.Id)
            {
                return;
            }

            AudiobookCandidates = new ObservableCollection<AudiobookCandidateGroup>(updated);
            SelectedAudiobookCandidate = AudiobookCandidates.FirstOrDefault(candidate =>
                candidate.CandidateKey == selectedCandidateKey);
            NotifyAudiobookSummaryChanged();
            UpdateBatchPlanningStatus(updated, "Review correction applied");
            AudiobookIdentityCorrectionStatus = canonicalAuthor is null && canonicalTitle is null
                ? "Metaroq's suggested author and title have been restored and the plan revalidated."
                : $"Author and title confirmed as {canonicalAuthor?.Trim()} — {canonicalTitle?.Trim()}; the plan has been revalidated.";
            OrganisationStatus = AudiobookIdentityCorrectionStatus;
        }
        catch (Exception exception)
        {
            AudiobookIdentityCorrectionStatus = $"Author and title could not be saved: {exception.Message}";
        }
        finally
        {
            IsAudiobookReviewCorrectionRunning = false;
        }
    }

    private bool CanApplySelectedGenreOverride() =>
        !IsAudiobookAnalysisRunning &&
        !IsAudiobookExecutionRunning &&
        !IsAudiobookReviewCorrectionRunning &&
        SelectedSource is not null &&
        SelectedAudiobookCandidate?.IsPrimaryOrganisationPlan == true &&
        SelectedAudiobookCandidate.OrganisationProposal is not null &&
        SelectedAudiobookGenreOverride is not null &&
        global::Archivio.Application.Abstractions.AudiobookGenreCategories.Contains(
            SelectedAudiobookGenreOverride);

    [RelayCommand(CanExecute = nameof(CanApplySelectedGenreOverride))]
    private Task ApplySelectedGenreOverrideAsync() =>
        SetSelectedGenreOverrideAsync(SelectedAudiobookGenreOverride);

    private bool CanResetSelectedGenreOverride() =>
        !IsAudiobookAnalysisRunning &&
        !IsAudiobookExecutionRunning &&
        !IsAudiobookReviewCorrectionRunning &&
        SelectedSource is not null &&
        SelectedAudiobookCandidate?.OrganisationProposal?.UsesManualGenre == true;

    [RelayCommand(CanExecute = nameof(CanResetSelectedGenreOverride))]
    private Task ResetSelectedGenreOverrideAsync() => SetSelectedGenreOverrideAsync(null);

    private async Task SetSelectedGenreOverrideAsync(string? genreCategory)
    {
        var source = SelectedSource;
        var selected = SelectedAudiobookCandidate;
        var plan = selected?.OrganisationProposal;
        if (source is null || selected is null || plan is null)
        {
            return;
        }

        var selectedCandidateKey = selected.CandidateKey;
        IsAudiobookReviewCorrectionRunning = true;
        AudiobookReviewCorrectionStatus = genreCategory is null
            ? "Restoring Metaroq's suggested genre..."
            : $"Confirming {genreCategory} and revalidating the plan...";
        try
        {
            var updated = await _audiobookOrganisationService.SetGenreOverrideAsync(
                source.Id,
                AudiobookCandidates.ToList(),
                plan.PlanKey,
                genreCategory);
            updated = await _audiobookBatchPlanningService.PrepareBatchAsync(
                source.Id,
                source.Path,
                updated,
                MediaItems.ToList());
            if (SelectedSource?.Id != source.Id)
            {
                return;
            }

            AudiobookCandidates = new ObservableCollection<AudiobookCandidateGroup>(updated);
            SelectedAudiobookCandidate = AudiobookCandidates.FirstOrDefault(candidate =>
                candidate.CandidateKey == selectedCandidateKey);
            NotifyAudiobookSummaryChanged();
            UpdateBatchPlanningStatus(updated, "Review correction applied");
            AudiobookReviewCorrectionStatus = genreCategory is null
                ? "Metaroq's suggested genre has been restored and the plan revalidated."
                : $"Genre confirmed as {genreCategory}; the plan has been revalidated.";
            OrganisationStatus = AudiobookReviewCorrectionStatus;
        }
        catch (Exception exception)
        {
            AudiobookReviewCorrectionStatus = $"Genre correction could not be saved: {exception.Message}";
        }
        finally
        {
            IsAudiobookReviewCorrectionRunning = false;
        }
    }

    private bool CanApproveSelectedBatch() =>
        !IsAudiobookAnalysisRunning &&
        SelectedAudiobookCandidate?.BatchPlan is
        {
            CanApprove: true,
            Decision: not AudiobookBatchDecision.Approved
        };

    [RelayCommand(CanExecute = nameof(CanApproveSelectedBatch))]
    private async Task ApproveSelectedBatchAsync()
    {
        var plan = SelectedAudiobookCandidate?.BatchPlan;
        if (plan is null)
        {
            return;
        }

        await ApplyBatchDecisionAsync(
            [plan.PlanKey],
            AudiobookBatchDecision.Approved,
            $"Approved {plan.CanonicalDisplay}");
    }

    private bool CanDeferSelectedBatch() =>
        !IsAudiobookAnalysisRunning &&
        SelectedAudiobookCandidate?.BatchPlan is
        {
            Decision: not AudiobookBatchDecision.Deferred
        };

    [RelayCommand(CanExecute = nameof(CanDeferSelectedBatch))]
    private async Task DeferSelectedBatchAsync()
    {
        var plan = SelectedAudiobookCandidate?.BatchPlan;
        if (plan is null)
        {
            return;
        }

        await ApplyBatchDecisionAsync(
            [plan.PlanKey],
            AudiobookBatchDecision.Deferred,
            $"Deferred {plan.CanonicalDisplay}");
    }

    private bool CanResetSelectedBatchDecision() =>
        !IsAudiobookAnalysisRunning &&
        SelectedAudiobookCandidate?.BatchPlan is
        {
            Decision: not AudiobookBatchDecision.Pending
        };

    [RelayCommand(CanExecute = nameof(CanResetSelectedBatchDecision))]
    private async Task ResetSelectedBatchDecisionAsync()
    {
        var plan = SelectedAudiobookCandidate?.BatchPlan;
        if (plan is null)
        {
            return;
        }

        await ApplyBatchDecisionAsync(
            [plan.PlanKey],
            AudiobookBatchDecision.Pending,
            $"Reset {plan.CanonicalDisplay}");
    }

    private bool CanExecuteApprovedBatch() =>
        !IsBusy &&
        !IsScanRunning &&
        !IsAudiobookAnalysisRunning &&
        !IsAudiobookExecutionRunning &&
        !HasInterruptedAudiobookExecution &&
        SelectedSource is { IsEnabled: true } &&
        BatchExecutableOperationCount > 0;

    [RelayCommand(CanExecute = nameof(CanExecuteApprovedBatch))]
    private async Task ExecuteApprovedBatchAsync()
    {
        if (SelectedSource is null)
        {
            return;
        }

        var plans = AudiobookCandidates
            .Select(candidate => candidate.BatchPlan)
            .Where(plan => plan is
            {
                ValidationStatus: AudiobookBatchValidationStatus.Ready,
                Decision: AudiobookBatchDecision.Approved
            })
            .Select(plan => plan!)
            .GroupBy(plan => plan.PlanKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        var operationCount = plans.Sum(plan =>
            plan.Operations.Count(operation => operation.Kind != AudiobookFileOperationKind.NoChange));
        if (operationCount == 0 ||
            !_audiobookExecutionConfirmationService.ConfirmExecution(plans.Count, operationCount))
        {
            return;
        }

        var source = SelectedSource;
        _audiobookExecutionCancellation?.Dispose();
        _audiobookExecutionCancellation = new CancellationTokenSource();
        IsAudiobookExecutionRunning = true;
        AudiobookExecutionProcessedCount = 0;
        AudiobookExecutionTotalCount = operationCount;
        AudiobookExecutionCurrentPath = string.Empty;
        AudiobookExecutionStatus = $"Revalidating {operationCount:N0} approved file operation{(operationCount == 1 ? string.Empty : "s")}...";
        try
        {
            var progress = CreateAudiobookExecutionProgress();
            var candidates = AudiobookCandidates.ToList();
            var cancellationToken = _audiobookExecutionCancellation.Token;
            var result = await Task.Run(
                () => _audiobookBatchExecutionService.ExecuteApprovedAsync(
                    source.Id,
                    source.Path,
                    candidates,
                    progress,
                    cancellationToken),
                cancellationToken);
            AudiobookExecutionStatus = result.Message;
            HasInterruptedAudiobookExecution = result.NeedsRecovery;
            if (!result.Succeeded)
            {
                Status = result.Message;
                return;
            }

            var approvedPlanKeys = plans
                .Select(plan => plan.PlanKey)
                .ToList();
            await ApplyBatchDecisionAsync(
                approvedPlanKeys,
                AudiobookBatchDecision.Pending,
                "Cleared approvals after successful execution");

            IsAudiobookExecutionRunning = false;
            ResetAudiobookAnalysis();
            AudiobookExecutionStatus = $"{result.Message} Rescanning the catalogue...";
            IsScanRunning = await _backgroundScanService.QueueScanAsync(source.Id);
            Status = IsScanRunning
                ? "Approved file operations completed; refreshing the catalogue"
                : "Approved file operations completed; start a scan to refresh the catalogue";
        }
        catch (FileNotFoundException exception)
        {
            var approvedPlanKeys = plans
                .Select(plan => plan.PlanKey)
                .ToList();
            await ApplyBatchDecisionAsync(
                approvedPlanKeys,
                AudiobookBatchDecision.Pending,
                "Reset approvals after stale catalogue preflight");

            IsAudiobookExecutionRunning = false;
            ResetAudiobookAnalysis();
            ResetScanProgress();
            IsScanRunning = await _backgroundScanService.QueueScanAsync(source.Id);

            var unavailablePath = GetExecutionDisplayPath(source.Path, exception.FileName);
            AudiobookExecutionStatus = IsScanRunning
                ? $"Execution stopped safely before any files were changed. The catalogue referenced an unavailable source: {unavailablePath}. All approvals were reset and a catalogue refresh has started. Analyse again when the scan finishes."
                : $"Execution stopped safely before any files were changed. The catalogue referenced an unavailable source: {unavailablePath}. All approvals were reset. Scan the catalogue, then analyse again.";
            Status = AudiobookExecutionStatus;
        }
        catch (Exception exception)
        {
            AudiobookExecutionStatus = $"Execution did not start: {exception.Message}";
            Status = AudiobookExecutionStatus;
        }
        finally
        {
            IsAudiobookExecutionRunning = false;
            _audiobookExecutionCancellation?.Dispose();
            _audiobookExecutionCancellation = null;
        }
    }

    private static string GetExecutionDisplayPath(string libraryRoot, string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return "an unknown indexed path";
        }

        try
        {
            var relativePath = Path.GetRelativePath(libraryRoot, fullPath);
            return relativePath.StartsWith("..", StringComparison.Ordinal)
                ? fullPath
                : relativePath;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return fullPath;
        }
    }

    private bool CanRecoverInterruptedExecution() =>
        HasInterruptedAudiobookExecution &&
        !IsBusy &&
        !IsScanRunning &&
        !IsAudiobookAnalysisRunning &&
        !IsAudiobookExecutionRunning &&
        SelectedSource is not null;

    [RelayCommand(CanExecute = nameof(CanRecoverInterruptedExecution))]
    private async Task RecoverInterruptedExecutionAsync()
    {
        if (SelectedSource is null)
        {
            return;
        }

        var latest = await _audiobookBatchExecutionService.LoadLatestAsync(SelectedSource.Id);
        if (latest is null ||
            !_audiobookExecutionConfirmationService.ConfirmRecovery(latest.PlannedOperationCount))
        {
            return;
        }

        _audiobookExecutionCancellation?.Dispose();
        _audiobookExecutionCancellation = new CancellationTokenSource();
        IsAudiobookExecutionRunning = true;
        AudiobookExecutionProcessedCount = 0;
        AudiobookExecutionTotalCount = latest.PlannedOperationCount;
        AudiobookExecutionStatus = "Recovering the interrupted execution journal...";
        try
        {
            var progress = CreateAudiobookExecutionProgress();
            var source = SelectedSource;
            var cancellationToken = _audiobookExecutionCancellation.Token;
            var result = await Task.Run(
                () => _audiobookBatchExecutionService.RecoverInterruptedAsync(
                    source.Id,
                    source.Path,
                    progress,
                    cancellationToken),
                cancellationToken);
            AudiobookExecutionStatus = result.Message;
            HasInterruptedAudiobookExecution = result.NeedsRecovery;
            Status = result.Message;
        }
        catch (OperationCanceledException)
        {
            AudiobookExecutionStatus = "Recovery cancelled; the execution journal remains available.";
        }
        catch (Exception exception)
        {
            AudiobookExecutionStatus = $"Recovery could not complete: {exception.Message}";
        }
        finally
        {
            IsAudiobookExecutionRunning = false;
            _audiobookExecutionCancellation?.Dispose();
            _audiobookExecutionCancellation = null;
        }
    }

    private bool CanCancelAudiobookExecution() => IsAudiobookExecutionRunning;

    [RelayCommand(CanExecute = nameof(CanCancelAudiobookExecution))]
    private void CancelAudiobookExecution()
    {
        AudiobookExecutionStatus = "Cancelling execution and rolling back completed moves...";
        _audiobookExecutionCancellation?.Cancel();
    }

    private void UpdateAudiobookExecutionProgress(AudiobookExecutionProgress progress)
    {
        AudiobookExecutionProcessedCount = progress.ProcessedCount;
        AudiobookExecutionTotalCount = progress.TotalCount;
        AudiobookExecutionCurrentPath = progress.CurrentPath;
        AudiobookExecutionStatus = progress.Status;
    }

    private IProgress<AudiobookExecutionProgress> CreateAudiobookExecutionProgress()
    {
        var uiProgress = new Progress<AudiobookExecutionProgress>(UpdateAudiobookExecutionProgress);
        return new ThrottledProgress<AudiobookExecutionProgress>(
            uiProgress,
            AudiobookExecutionProgressInterval,
            progress => progress.TotalCount > 0 && progress.ProcessedCount >= progress.TotalCount);
    }

    partial void OnAudiobookAnalysisTotalCountChanged(int value) =>
        OnPropertyChanged(nameof(AudiobookAnalysisProgressMaximum));

    partial void OnAudiobookExecutionTotalCountChanged(int value) =>
        OnPropertyChanged(nameof(AudiobookExecutionProgressMaximum));

    private bool CanAnalyseAudiobooks() =>
        !IsBusy && !IsScanRunning && !IsAudiobookAnalysisRunning && !IsAudiobookExecutionRunning;

    [RelayCommand(CanExecute = nameof(CanAnalyseAudiobooks))]
    private async Task AnalyseAudiobooksAsync()
    {
        if (MediaItems.Count == 0)
        {
            ResetAudiobookAnalysis();
            Status = SelectedSource is null
                ? "Select a library source before analysing audiobooks"
                : "The selected source has no indexed media to analyse";
            return;
        }

        ResetAudiobookAnalysis();
        var sourceId = SelectedSource?.Id;
        var cancellation = new CancellationTokenSource();
        _audiobookAnalysisCancellation = cancellation;
        IsAudiobookAnalysisRunning = true;
        AudiobookAnalysisStatus = "Grouping audiobook files...";
        Status = "Analysing indexed audio in the background";

        var progress = new Progress<AudiobookAnalysisProgress>(UpdateAudiobookAnalysisProgress);

        try
        {
            var groups = await _audiobookAnalysisService.AnalyseAsync(
                MediaItems.ToList(),
                progress,
                cancellation.Token);

            if (SelectedSource?.Id != sourceId)
            {
                return;
            }

            AudiobookCandidates = new ObservableCollection<AudiobookCandidateGroup>(groups);
            SelectedAudiobookCandidate = FirstAudiobookReviewItem();
            NotifyAudiobookSummaryChanged();
            if (sourceId is not null && groups.Count > 0)
            {
                var lookupGroups = await PrepareOnlineLookupProposalsAsync(
                    sourceId.Value,
                    groups,
                    cancellation.Token);
                var enrichedGroups = await EnrichWithOnlineMetadataAsync(
                    sourceId.Value,
                    lookupGroups,
                    cancellation.Token);
                if (SelectedSource?.Id != sourceId)
                {
                    return;
                }

                var proposedGroups = await PrepareOrganisationProposalsAsync(
                    sourceId.Value,
                    enrichedGroups,
                    cancellation.Token);
                AudiobookCandidates = new ObservableCollection<AudiobookCandidateGroup>(proposedGroups);
                SelectedAudiobookCandidate = FirstAudiobookReviewItem();
                NotifyAudiobookSummaryChanged();
            }

            AudiobookAnalysisStatus = groups.Count == 0
                ? "No audiobook candidates found"
                : AudiobookAnalysisWarningCount == 0
                    ? "Analysis complete"
                    : $"Analysis complete with {AudiobookAnalysisWarningCount:N0} metadata warning{(AudiobookAnalysisWarningCount == 1 ? string.Empty : "s")}";
            AudiobookAnalysisCurrentPath = string.Empty;
            AudiobookAnalysisStorageStatus = $"Saved {DateTime.Now:g}";
            Status = groups.Count == 0
                ? "No audiobook candidates found in the selected source"
                : $"Found {groups.Count} audiobook candidate{(groups.Count == 1 ? string.Empty : "s")}";
        }
        catch (OperationCanceledException)
        {
            AudiobookAnalysisStatus = "Analysis cancelled";
            AudiobookAnalysisCurrentPath = string.Empty;
            Status = "Audiobook analysis cancelled";
        }
        catch (Exception exception)
        {
            AudiobookAnalysisStatus = $"Analysis failed at file {AudiobookAnalysisProcessedCount + 1:N0}";
            Status = $"{exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(_audiobookAnalysisCancellation, cancellation))
            {
                _audiobookAnalysisCancellation = null;
                IsAudiobookAnalysisRunning = false;
            }

            cancellation.Dispose();
        }
    }

    private bool CanCancelAudiobookAnalysis() => IsAudiobookAnalysisRunning;

    [RelayCommand(CanExecute = nameof(CanCancelAudiobookAnalysis))]
    private void CancelAudiobookAnalysis()
    {
        _audiobookAnalysisCancellation?.Cancel();
        AudiobookAnalysisStatus = "Cancelling analysis...";
        Status = "Cancelling audiobook analysis...";
    }

    private void UpdateAudiobookAnalysisProgress(AudiobookAnalysisProgress progress)
    {
        AudiobookAnalysisStatus = progress.Status;
        AudiobookAnalysisCurrentPath = progress.CurrentPath ?? string.Empty;
        AudiobookAnalysisProcessedCount = progress.ProcessedCount;
        AudiobookAnalysisTotalCount = progress.TotalCount;
        AudiobookAnalysisWarningCount = progress.WarningCount;
    }

    private async Task<IReadOnlyList<AudiobookCandidateGroup>> EnrichWithOnlineMetadataAsync(
        Guid librarySourceId,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        CancellationToken cancellationToken)
    {
        var progress = new Progress<OnlineMetadataLookupProgress>(value =>
        {
            OnlineMetadataStatus = value.Status;
            AudiobookAnalysisCurrentPath = value.CurrentTitle ?? string.Empty;
        });

        try
        {
            var result = await _onlineMetadataLookupService.EnrichCandidatesAsync(
                librarySourceId,
                candidates,
                progress,
                cancellationToken);
            await Task.Yield();
            var count = result.Count(candidate => candidate.HasOnlineSuggestion);
            OnlineMetadataStatus = count == 0
                ? "Online fallback found no confident matches"
                : $"Online fallback found {count:N0} confident suggestion{(count == 1 ? string.Empty : "s")}";
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            OnlineMetadataStatus = $"Local analysis saved; online fallback unavailable: {exception.Message}";
            return candidates;
        }
    }

    private async Task<IReadOnlyList<AudiobookCandidateGroup>> PrepareBatchPreviewAsync(
        Guid librarySourceId,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        CancellationToken cancellationToken)
    {
        if (SelectedSource?.Id != librarySourceId)
        {
            return candidates;
        }

        BatchPlanningStatus = "Preparing whole-library dry run...";
        try
        {
            var result = await _audiobookBatchPlanningService.PrepareBatchAsync(
                librarySourceId,
                SelectedSource.Path,
                candidates,
                MediaItems.ToList(),
                cancellationToken);
            UpdateBatchPlanningStatus(result, "Dry run prepared");
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            BatchPlanningStatus = $"Batch dry run unavailable: {exception.Message}";
            return candidates;
        }
    }

    private bool CanApproveSafeBatch() =>
        !IsAudiobookAnalysisRunning &&
        !IsAudiobookExecutionRunning &&
        SelectedSource is not null &&
        BulkApprovalEligibleCount > 0;

    [RelayCommand(CanExecute = nameof(CanApproveSafeBatch))]
    private async Task ApproveSafeBatchAsync()
    {
        var keys = AudiobookCandidates
            .Where(candidate => candidate.CanBulkApprove)
            .Select(candidate => candidate.BatchPlan!.PlanKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        await ApplyBatchDecisionAsync(
            keys,
            AudiobookBatchDecision.Approved,
            $"Bulk approved {keys.Count:N0} clean 100% single-file plan{(keys.Count == 1 ? string.Empty : "s")}");
    }

    [RelayCommand]
    private async Task DeferBlockedBatchAsync()
    {
        var keys = AudiobookCandidates
            .Where(candidate => candidate.IsPrimaryOrganisationPlan && candidate.BatchPlan?.IsBlocked == true)
            .Select(candidate => candidate.BatchPlan!.PlanKey)
            .ToList();
        await ApplyBatchDecisionAsync(keys, AudiobookBatchDecision.Deferred, "Deferred blocked plans");
    }

    [RelayCommand]
    private async Task ResetBatchDecisionsAsync()
    {
        var keys = AudiobookCandidates
            .Where(candidate => candidate.IsPrimaryOrganisationPlan && candidate.BatchPlan is not null)
            .Select(candidate => candidate.BatchPlan!.PlanKey)
            .ToList();
        await ApplyBatchDecisionAsync(keys, AudiobookBatchDecision.Pending, "Reset batch decisions");
    }

    private async Task ApplyBatchDecisionAsync(
        IReadOnlyCollection<string> planKeys,
        AudiobookBatchDecision decision,
        string statusPrefix)
    {
        if (IsAudiobookAnalysisRunning)
        {
            BatchPlanningStatus = "Wait for audiobook analysis and dry-run preparation to finish";
            return;
        }

        if (SelectedSource is null || planKeys.Count == 0)
        {
            UpdateBatchPlanningStatus(AudiobookCandidates, statusPrefix);
            return;
        }

        var selectedKey = SelectedAudiobookCandidate?.CandidateKey;
        try
        {
            var updated = await _audiobookBatchPlanningService.SetDecisionAsync(
                SelectedSource.Id,
                AudiobookCandidates.ToList(),
                planKeys,
                decision);
            AudiobookCandidates = new ObservableCollection<AudiobookCandidateGroup>(updated);
            SelectedAudiobookCandidate = selectedKey is null
                ? AudiobookCandidates.FirstOrDefault()
                : AudiobookCandidates.FirstOrDefault(candidate => candidate.CandidateKey == selectedKey);
            NotifyAudiobookSummaryChanged();
            UpdateBatchPlanningStatus(updated, statusPrefix);
        }
        catch (Exception exception)
        {
            BatchPlanningStatus = $"Batch decision could not be saved: {exception.Message}";
        }
    }

    private void UpdateBatchPlanningStatus(
        IReadOnlyCollection<AudiobookCandidateGroup> candidates,
        string prefix)
    {
        var primaryPlans = candidates
            .Where(candidate => candidate.IsPrimaryOrganisationPlan && candidate.BatchPlan is not null)
            .Select(candidate => candidate.BatchPlan!)
            .ToList();
        var ready = primaryPlans.Count(plan => plan.ValidationStatus == AudiobookBatchValidationStatus.Ready);
        var alreadyOrganised = primaryPlans.Count(plan =>
            plan.ValidationStatus == AudiobookBatchValidationStatus.NoChange);
        var metadataReview = primaryPlans.Count(plan =>
            plan.ValidationStatus == AudiobookBatchValidationStatus.ReviewRequired);
        var conflicts = primaryPlans.Count(plan =>
            plan.ValidationStatus == AudiobookBatchValidationStatus.Conflict);
        var approved = primaryPlans.Count(plan => plan.Decision == AudiobookBatchDecision.Approved);
        var bulkSafe = candidates.Count(candidate => candidate.CanBulkApprove);
        var individualReview = candidates.Count(candidate =>
            candidate.IsIndividualApprovalPending);
        BatchPlanningStatus = $"{prefix}: {bulkSafe:N0} bulk-safe single files · " +
                              $"{individualReview:N0} multi-file plans need individual approval · " +
                              $"{ready:N0} execution-valid · {alreadyOrganised:N0} already organised · " +
                              $"{metadataReview:N0} metadata review · {conflicts:N0} conflicts · " +
                              $"{approved:N0} approved · no files changed";
    }

    private async Task<IReadOnlyList<AudiobookCandidateGroup>> PrepareOnlineLookupProposalsAsync(
        Guid librarySourceId,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        CancellationToken cancellationToken)
    {
        OrganisationStatus = "Identifying logical audiobooks for online enrichment...";
        try
        {
            return await _audiobookOrganisationService.PrepareProposalsAsync(
                librarySourceId,
                candidates,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            OrganisationStatus = $"Logical-book grouping unavailable for online enrichment: {exception.Message}";
            return candidates;
        }
    }

    private async Task<IReadOnlyList<AudiobookCandidateGroup>> PrepareOrganisationProposalsAsync(
        Guid librarySourceId,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        CancellationToken cancellationToken)
    {
        OrganisationStatus = "Preparing read-only organisation plans...";
        try
        {
            var result = await _audiobookOrganisationService.PrepareProposalsAsync(
                librarySourceId,
                candidates,
                cancellationToken);
            var planCount = result.Count(candidate => candidate.IsPrimaryOrganisationPlan);
            var readyCount = result.Count(candidate =>
                candidate.IsPrimaryOrganisationPlan &&
                candidate.OrganisationProposal?.ReadyForAutomaticHandling == true);
            OrganisationStatus = $"Prepared {planCount:N0} read-only plans · {readyCount:N0} ready for future automation";
            return await PrepareBatchPreviewAsync(librarySourceId, result, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            OrganisationStatus = $"Organisation plans unavailable: {exception.Message}";
            return candidates;
        }
    }

    private async Task LoadSavedAudiobookAnalysisAsync(
        Guid librarySourceId,
        IReadOnlyList<MediaItem> mediaItems)
    {
        AudiobookAnalysisStatus = "Loading saved analysis...";
        try
        {
            var saved = await _audiobookAnalysisService.LoadSavedAnalysisAsync(mediaItems);
            var preparedCandidates = saved is null
                ? null
                : await PrepareOrganisationProposalsAsync(
                    librarySourceId,
                    saved.Candidates,
                    CancellationToken.None);
            RunOnUiThread(() =>
            {
                if (SelectedSource?.Id != librarySourceId)
                {
                    return;
                }

                if (saved is null)
                {
                    AudiobookAnalysisStatus = "Ready to analyse indexed audio";
                    AudiobookAnalysisStorageStatus = "No current saved analysis";
                    return;
                }

                AudiobookCandidates = new ObservableCollection<AudiobookCandidateGroup>(preparedCandidates!);
                SelectedAudiobookCandidate = FirstAudiobookReviewItem();
                AudiobookAnalysisProcessedCount = saved.Candidates.Sum(candidate => candidate.Parts.Count);
                AudiobookAnalysisTotalCount = AudiobookAnalysisProcessedCount;
                AudiobookAnalysisWarningCount = saved.WarningCount;
                AudiobookAnalysisStatus = saved.WarningCount == 0
                    ? "Saved analysis loaded"
                    : $"Saved analysis loaded with {saved.WarningCount:N0} metadata warning{(saved.WarningCount == 1 ? string.Empty : "s")}";
                AudiobookAnalysisStorageStatus = $"Saved {saved.CompletedAtUtc.ToLocalTime():g}";
                OnlineMetadataStatus = saved.Candidates.Any(candidate => candidate.HasOnlineSuggestion)
                    ? $"Loaded {saved.Candidates.Count(candidate => candidate.HasOnlineSuggestion):N0} saved online " +
                      $"suggestion{(saved.Candidates.Count(candidate => candidate.HasOnlineSuggestion) == 1 ? string.Empty : "s")}"
                    : "No saved online suggestions";
                NotifyAudiobookSummaryChanged();
                Status = $"Loaded {saved.Candidates.Count:N0} saved audiobook candidate{(saved.Candidates.Count == 1 ? string.Empty : "s")}";
            });
        }
        catch (Exception exception)
        {
            RunOnUiThread(() =>
            {
                if (SelectedSource?.Id == librarySourceId)
                {
                    AudiobookAnalysisStatus = "Saved analysis could not be loaded";
                    AudiobookAnalysisStorageStatus = exception.Message;
                }
            });
        }

        await LoadLatestExecutionStatusAsync(librarySourceId);
    }

    private async Task LoadLatestExecutionStatusAsync(Guid librarySourceId)
    {
        try
        {
            var latest = await _audiobookBatchExecutionService.LoadLatestAsync(librarySourceId);
            RunOnUiThread(() =>
            {
                if (SelectedSource?.Id != librarySourceId)
                {
                    return;
                }

                HasInterruptedAudiobookExecution = latest?.Status is
                    AudiobookExecutionRunStatus.Prepared or
                    AudiobookExecutionRunStatus.Running or
                    AudiobookExecutionRunStatus.FailedNeedsRecovery;
                AudiobookExecutionStatus = latest is null
                    ? "No batch execution has run"
                    : latest.Status switch
                    {
                        AudiobookExecutionRunStatus.Completed =>
                            $"Last execution completed {latest.CompletedAtUtc?.ToLocalTime():g}: {latest.CompletedOperationCount:N0} file updates completed",
                        AudiobookExecutionRunStatus.FailedRolledBack =>
                            $"Last execution stopped safely: {latest.RolledBackOperationCount:N0} moves rolled back",
                        AudiobookExecutionRunStatus.CancelledRolledBack =>
                            $"Last execution was cancelled: {latest.RolledBackOperationCount:N0} moves rolled back",
                        AudiobookExecutionRunStatus.FailedNeedsRecovery =>
                            "Previous execution needs recovery before another run can start",
                        _ => "Previous execution was interrupted; recovery is available"
                    };
                AudiobookExecutionProcessedCount = latest?.CompletedOperationCount ?? 0;
                AudiobookExecutionTotalCount = latest?.PlannedOperationCount ?? 0;
            });
        }
        catch (Exception exception)
        {
            RunOnUiThread(() => AudiobookExecutionStatus =
                $"Execution history could not be loaded: {exception.Message}");
        }
    }

    private void ResetAudiobookAnalysis()
    {
        _audiobookAnalysisCancellation?.Cancel();
        _audiobookExecutionCancellation?.Cancel();
        AudiobookCandidates = [];
        SelectedAudiobookCandidate = null;
        AudiobookAnalysisProcessedCount = 0;
        AudiobookAnalysisTotalCount = 0;
        AudiobookAnalysisWarningCount = 0;
        AudiobookAnalysisCurrentPath = string.Empty;
        AudiobookAnalysisStatus = "Ready to analyse indexed audio";
        AudiobookAnalysisStorageStatus = "Analysis has not been saved";
        OnlineMetadataStatus = "Online fallback has not run";
        OrganisationStatus = "Organisation plans have not been prepared";
        BatchPlanningStatus = "Batch dry run has not been prepared";
        NotifyAudiobookSummaryChanged();
    }

    private bool FilterAudiobookCandidate(object item)
    {
        if (item is not AudiobookCandidateGroup candidate)
        {
            return false;
        }

        if (HasAudiobookOrganisationPlans && !candidate.IsPrimaryOrganisationPlan)
        {
            return false;
        }

        if (ShowAudiobooksNeedingReviewOnly && !candidate.ReviewItemNeedsReview)
        {
            return false;
        }

        if (ShowAudiobookMetadataWarningsOnly &&
            !candidate.Parts.Any(part => part.Metadata.Warnings.Count > 0))
        {
            return false;
        }

        if (ShowMultipartAudiobooksOnly && !candidate.IsMultipart)
        {
            return false;
        }

        if (ShowOnlineSuggestionsOnly && !candidate.HasOnlineSuggestion)
        {
            return false;
        }

        if (ShowOrganisationPlansOnly && !candidate.IsReviewClearedOrganisationPlan)
        {
            return false;
        }

        if (ShowAutomaticReadyPlansOnly &&
            (!candidate.IsPrimaryOrganisationPlan ||
             candidate.OrganisationProposal?.ReadyForAutomaticHandling != true))
        {
            return false;
        }

        var search = AudiobookSearchText.Trim();
        return search.Length == 0 ||
            candidate.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            (candidate.OrganisationProposal?.CanonicalDisplay.Contains(
                search,
                StringComparison.OrdinalIgnoreCase) ?? false) ||
            (candidate.OrganisationProposal?.SuggestedRelativeFolder.Contains(
                search,
                StringComparison.OrdinalIgnoreCase) ?? false) ||
            (candidate.OrganisationProposal?.GenreCategory.Contains(
                search,
                StringComparison.OrdinalIgnoreCase) ?? false) ||
            candidate.Parts.Any(part =>
                part.MediaItem.FileName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                part.MediaItem.RelativePath.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshAudiobookCandidatesView()
    {
        AudiobookCandidatesView.Refresh();
        OnPropertyChanged(nameof(VisibleAudiobookCandidateCount));
        OnPropertyChanged(nameof(VisibleAudiobookReviewItemCount));
        OnPropertyChanged(nameof(AudiobookReviewCountLabel));
    }

    private AudiobookCandidateGroup? FirstAudiobookReviewItem() =>
        AudiobookCandidates.FirstOrDefault(candidate => candidate.IsPrimaryOrganisationPlan) ??
        AudiobookCandidates.FirstOrDefault();

    private void NotifyAudiobookSummaryChanged()
    {
        OnPropertyChanged(nameof(AudiobookCandidateCount));
        OnPropertyChanged(nameof(HasAudiobookOrganisationPlans));
        OnPropertyChanged(nameof(AudiobookReviewItemCount));
        OnPropertyChanged(nameof(VisibleAudiobookReviewItemCount));
        OnPropertyChanged(nameof(AudiobookReviewCountLabel));
        OnPropertyChanged(nameof(MultipartAudiobookCount));
        OnPropertyChanged(nameof(SingleFileAudiobookCount));
        OnPropertyChanged(nameof(AudiobooksNeedingReviewCount));
        OnPropertyChanged(nameof(OnlineSuggestionCount));
        OnPropertyChanged(nameof(OrganisationPlanCount));
        OnPropertyChanged(nameof(AutomaticReadyPlanCount));
        OnPropertyChanged(nameof(GroupedOrganisationPlanCount));
        OnPropertyChanged(nameof(BatchPlanCount));
        OnPropertyChanged(nameof(BatchReadyCount));
        OnPropertyChanged(nameof(BulkApprovalEligibleCount));
        OnPropertyChanged(nameof(IndividualApprovalRequiredCount));
        OnPropertyChanged(nameof(ApproveSafeBatchLabel));
        OnPropertyChanged(nameof(BatchApprovedCount));
        OnPropertyChanged(nameof(BatchBlockedCount));
        OnPropertyChanged(nameof(AlreadyOrganisedPlanCount));
        OnPropertyChanged(nameof(MetadataReviewPlanCount));
        OnPropertyChanged(nameof(BatchConflictCount));
        OnPropertyChanged(nameof(BatchExecutableOperationCount));
        ApproveSafeBatchCommand.NotifyCanExecuteChanged();
        ExecuteApprovedBatchCommand.NotifyCanExecuteChanged();
    }
}
