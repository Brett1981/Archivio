using System.Collections.ObjectModel;
using Archivio.Application.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Archivio.App;

public sealed partial class MainWindowViewModel
{
    private readonly IAudiobookAnalysisService _audiobookAnalysisService;
    private CancellationTokenSource? _audiobookAnalysisCancellation;

    [ObservableProperty]
    private ObservableCollection<AudiobookCandidateGroup> _audiobookCandidates = [];

    public int AudiobookCandidateCount => AudiobookCandidates.Count;
    public int MultipartAudiobookCount => AudiobookCandidates.Count(candidate => candidate.IsMultipart);
    public int SingleFileAudiobookCount => AudiobookCandidates.Count(candidate => !candidate.IsMultipart);
    public int AudiobooksNeedingReviewCount => AudiobookCandidates.Count(candidate => candidate.NeedsReview);
    public int AudiobookAnalysisProgressMaximum => Math.Max(1, AudiobookAnalysisTotalCount);

    [ObservableProperty]
    private AudiobookCandidateGroup? _selectedAudiobookCandidate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyseAudiobooksCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelAudiobookAnalysisCommand))]
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

    partial void OnAudiobookAnalysisTotalCountChanged(int value) =>
        OnPropertyChanged(nameof(AudiobookAnalysisProgressMaximum));

    private bool CanAnalyseAudiobooks() => !IsBusy && !IsScanRunning && !IsAudiobookAnalysisRunning;

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
            SelectedAudiobookCandidate = AudiobookCandidates.FirstOrDefault();
            NotifyAudiobookSummaryChanged();
            AudiobookAnalysisStatus = groups.Count == 0
                ? "No audiobook candidates found"
                : AudiobookAnalysisWarningCount == 0
                    ? "Analysis complete"
                    : $"Analysis complete with {AudiobookAnalysisWarningCount:N0} metadata warning{(AudiobookAnalysisWarningCount == 1 ? string.Empty : "s")}";
            AudiobookAnalysisCurrentPath = string.Empty;
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

    private void ResetAudiobookAnalysis()
    {
        _audiobookAnalysisCancellation?.Cancel();
        AudiobookCandidates = [];
        SelectedAudiobookCandidate = null;
        AudiobookAnalysisProcessedCount = 0;
        AudiobookAnalysisTotalCount = 0;
        AudiobookAnalysisWarningCount = 0;
        AudiobookAnalysisCurrentPath = string.Empty;
        AudiobookAnalysisStatus = "Ready to analyse indexed audio";
        NotifyAudiobookSummaryChanged();
    }

    private void NotifyAudiobookSummaryChanged()
    {
        OnPropertyChanged(nameof(AudiobookCandidateCount));
        OnPropertyChanged(nameof(MultipartAudiobookCount));
        OnPropertyChanged(nameof(SingleFileAudiobookCount));
        OnPropertyChanged(nameof(AudiobooksNeedingReviewCount));
    }
}
