using System.Collections.ObjectModel;
using Archivio.Application.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Archivio.App;

public sealed partial class MainWindowViewModel
{
    private readonly IAudiobookAnalysisService _audiobookAnalysisService;

    public ObservableCollection<AudiobookCandidateGroup> AudiobookCandidates { get; } = [];

    public int AudiobookCandidateCount => AudiobookCandidates.Count;
    public int MultipartAudiobookCount => AudiobookCandidates.Count(candidate => candidate.IsMultipart);
    public int SingleFileAudiobookCount => AudiobookCandidates.Count(candidate => !candidate.IsMultipart);
    public int AudiobooksNeedingReviewCount => AudiobookCandidates.Count(candidate => candidate.NeedsReview);

    [ObservableProperty]
    private AudiobookCandidateGroup? _selectedAudiobookCandidate;

    [RelayCommand]
    private void AnalyseAudiobooks()
    {
        if (IsBusy || IsScanRunning)
        {
            Status = "Audiobook analysis is unavailable while another operation is running";
            return;
        }

        if (MediaItems.Count == 0)
        {
            ResetAudiobookAnalysis();
            Status = SelectedSource is null
                ? "Select a library source before analysing audiobooks"
                : "The selected source has no indexed media to analyse";
            return;
        }

        var groups = _audiobookAnalysisService.Analyse(MediaItems);
        AudiobookCandidates.Clear();
        foreach (var group in groups)
        {
            AudiobookCandidates.Add(group);
        }

        SelectedAudiobookCandidate = AudiobookCandidates.FirstOrDefault();
        NotifyAudiobookSummaryChanged();
        Status = groups.Count == 0
            ? "No audiobook candidates found in the selected source"
            : $"Found {groups.Count} audiobook candidate{(groups.Count == 1 ? string.Empty : "s")}";
    }

    private void ResetAudiobookAnalysis()
    {
        AudiobookCandidates.Clear();
        SelectedAudiobookCandidate = null;
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
