using System.Collections.ObjectModel;
using Archivio.Application.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Archivio.App;

public sealed partial class MainWindowViewModel
{
    public ObservableCollection<AudiobookCandidateGroup> AudiobookCandidates { get; } = [];

    public int AudiobookCandidateCount => AudiobookCandidates.Count;
    public int MultipartAudiobookCount => AudiobookCandidates.Count(candidate => candidate.IsMultipart);

    [ObservableProperty]
    private AudiobookCandidateGroup? _selectedAudiobookCandidate;

    private bool CanAnalyseAudiobooks() => !IsBusy && !IsScanRunning && MediaItems.Count > 0;

    [RelayCommand(CanExecute = nameof(CanAnalyseAudiobooks))]
    private void AnalyseAudiobooks()
    {
        var groups = _audiobookAnalysisService.Analyse(MediaItems);
        AudiobookCandidates.Clear();
        foreach (var group in groups)
        {
            AudiobookCandidates.Add(group);
        }

        SelectedAudiobookCandidate = AudiobookCandidates.FirstOrDefault();
        OnPropertyChanged(nameof(AudiobookCandidateCount));
        OnPropertyChanged(nameof(MultipartAudiobookCount));
        Status = groups.Count == 0
            ? "No audiobook candidates found in the selected source"
            : $"Found {groups.Count} audiobook candidate{(groups.Count == 1 ? string.Empty : "s")}";
    }

    private void ResetAudiobookAnalysis()
    {
        AudiobookCandidates.Clear();
        SelectedAudiobookCandidate = null;
        OnPropertyChanged(nameof(AudiobookCandidateCount));
        OnPropertyChanged(nameof(MultipartAudiobookCount));
        AnalyseAudiobooksCommand.NotifyCanExecuteChanged();
    }
}
