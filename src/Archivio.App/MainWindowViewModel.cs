using System.Collections.ObjectModel;
using System.Windows;
using Archivio.Application.Abstractions;
using Archivio.Application.Configuration;
using Archivio.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;

namespace Archivio.App;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ILibrarySourceService _librarySourceService;
    private readonly IFolderPickerService _folderPickerService;
    private readonly IBackgroundScanService _backgroundScanService;
    private readonly IMediaCatalogueService _mediaCatalogueService;

    public MainWindowViewModel(
        IOptions<ArchivioOptions> options,
        ILibrarySourceService librarySourceService,
        IFolderPickerService folderPickerService,
        IBackgroundScanService backgroundScanService,
        IMediaCatalogueService mediaCatalogueService,
        IAudiobookAnalysisService audiobookAnalysisService,
        IOnlineMetadataLookupService onlineMetadataLookupService)
    {
        _librarySourceService = librarySourceService;
        _folderPickerService = folderPickerService;
        _backgroundScanService = backgroundScanService;
        _mediaCatalogueService = mediaCatalogueService;
        _audiobookAnalysisService = audiobookAnalysisService;
        _onlineMetadataLookupService = onlineMetadataLookupService;
        _backgroundScanService.ProgressChanged += HandleScanProgress;
        _backgroundScanService.ScanCompleted += HandleScanCompleted;
        _backgroundScanService.ScanFailed += HandleScanFailed;
        Title = options.Value.ProductName;
        Version = options.Value.Version;
        SourceTypes = Enum.GetValues<LibrarySourceType>();
    }

    public string Title { get; }
    public string Version { get; }
    public IReadOnlyList<LibrarySourceType> SourceTypes { get; }
    public ObservableCollection<LibrarySource> LibrarySources { get; } = [];
    public ObservableCollection<MediaItem> MediaItems { get; } = [];

    public int MediaItemCount => MediaItems.Count;
    public int MissingMediaItemCount => MediaItems.Count(item => item.IsMissing);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _sourceName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _sourcePath = string.Empty;

    [ObservableProperty]
    private LibrarySourceType _sourceType = LibrarySourceType.Mixed;

    [ObservableProperty]
    private bool _sourceIsEnabled = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartScanCommand))]
    private LibrarySource? _selectedSource;

    [ObservableProperty]
    private string _status = "Loading library sources...";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnalyseAudiobooksCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnalyseAudiobooksCommand))]
    private bool _isScanRunning;

    [ObservableProperty]
    private LibraryScanStage _scanStage = LibraryScanStage.Idle;

    [ObservableProperty]
    private int _scanDiscoveredCount;

    public int ScanProgressMaximum => Math.Max(1, ScanDiscoveredCount);

    [ObservableProperty]
    private int _scanProcessedCount;

    [ObservableProperty]
    private int _scanAddedCount;

    [ObservableProperty]
    private int _scanRefreshedCount;

    [ObservableProperty]
    private int _scanMissingCount;

    [ObservableProperty]
    private string _scanCurrentPath = string.Empty;

    partial void OnScanDiscoveredCountChanged(int value) =>
        OnPropertyChanged(nameof(ScanProgressMaximum));

    partial void OnSelectedSourceChanged(LibrarySource? value)
    {
        ResetAudiobookAnalysis();

        if (value is null)
        {
            MediaItems.Clear();
            OnPropertyChanged(nameof(MediaItemCount));
            OnPropertyChanged(nameof(MissingMediaItemCount));
            return;
        }

        SourceName = value.Name;
        SourcePath = value.Path;
        SourceType = value.Type;
        SourceIsEnabled = value.IsEnabled;
        Status = $"Editing {value.Name}";
        _ = LoadMediaItemsAsync(value.Id);
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        await ExecuteAsync(async () =>
        {
            var sources = await _librarySourceService.GetAllAsync();
            LibrarySources.Clear();
            foreach (var source in sources)
            {
                LibrarySources.Add(source);
            }

            Status = sources.Count == 0
                ? "No library sources configured"
                : $"{sources.Count} library source{(sources.Count == 1 ? string.Empty : "s")} loaded";
        });
    }

    [RelayCommand]
    private void NewSource()
    {
        SelectedSource = null;
        SourceName = string.Empty;
        SourcePath = string.Empty;
        SourceType = LibrarySourceType.Mixed;
        SourceIsEnabled = true;
        Status = "Ready to add a library source";
    }

    [RelayCommand]
    private void Browse()
    {
        var selectedPath = _folderPickerService.PickFolder(SourcePath);
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            SourcePath = selectedPath;
        }
    }

    private bool CanSave() => !IsBusy && !IsScanRunning &&
        !string.IsNullOrWhiteSpace(SourceName) && !string.IsNullOrWhiteSpace(SourcePath);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        await ExecuteAsync(async () =>
        {
            LibrarySource saved;
            if (SelectedSource is null)
            {
                saved = await _librarySourceService.CreateAsync(SourceName, SourcePath, SourceType);
                Status = $"Added {saved.Name}";
            }
            else
            {
                saved = await _librarySourceService.UpdateAsync(SelectedSource.Id, SourceName, SourcePath,
                    SourceType, SourceIsEnabled);
                Status = $"Updated {saved.Name}";
            }

            await RefreshSourcesAsync(saved.Id);
        });
    }

    private bool CanDelete() => !IsBusy && !IsScanRunning && SelectedSource is not null;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (SelectedSource is null)
        {
            return;
        }

        var deletedName = SelectedSource.Name;
        await ExecuteAsync(async () =>
        {
            await _librarySourceService.DeleteAsync(SelectedSource.Id);
            await RefreshSourcesAsync();
            NewSource();
            Status = $"Deleted {deletedName}";
        });
    }

    private bool CanStartScan() => !IsBusy && !IsScanRunning && SelectedSource is { IsEnabled: true };

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task StartScanAsync()
    {
        if (SelectedSource is null)
        {
            return;
        }

        ResetAudiobookAnalysis();
        ResetScanProgress();
        IsScanRunning = await _backgroundScanService.QueueScanAsync(SelectedSource.Id);
        Status = IsScanRunning ? $"Scanning {SelectedSource.Name}" : "A scan is already running";
    }

    private bool CanCancelScan() => IsScanRunning;

    [RelayCommand(CanExecute = nameof(CanCancelScan))]
    private void CancelScan()
    {
        _backgroundScanService.Cancel();
        Status = "Cancelling scan...";
    }

    private void HandleScanProgress(LibraryScanProgress progress) => RunOnUiThread(() =>
    {
        ScanStage = progress.Stage;
        ScanDiscoveredCount = progress.DiscoveredCount;
        ScanProcessedCount = progress.ProcessedCount;
        ScanAddedCount = progress.AddedCount;
        ScanRefreshedCount = progress.RefreshedCount;
        ScanMissingCount = progress.MissingCount;
        ScanCurrentPath = progress.CurrentPath ?? string.Empty;
        Status = progress.Status;
        if (progress.Stage == LibraryScanStage.Cancelled)
        {
            IsScanRunning = false;
        }
    });

    private void HandleScanCompleted(LibraryScanResult result) => RunOnUiThread(() =>
    {
        IsScanRunning = false;
        ScanStage = LibraryScanStage.Completed;
        Status = $"Scan complete: {result.AddedCount} added, {result.RefreshedCount} refreshed, {result.MissingCount} missing";
        _ = LoadMediaItemsAsync(result.LibrarySourceId);
    });

    private void HandleScanFailed(Exception exception) => RunOnUiThread(() =>
    {
        IsScanRunning = false;
        ScanStage = LibraryScanStage.Failed;
        Status = exception.Message;
    });

    private async Task LoadMediaItemsAsync(Guid librarySourceId)
    {
        try
        {
            var items = await _mediaCatalogueService.GetByLibrarySourceAsync(librarySourceId);
            RunOnUiThread(() =>
            {
                if (SelectedSource?.Id != librarySourceId)
                {
                    return;
                }

                MediaItems.Clear();
                foreach (var item in items)
                {
                    MediaItems.Add(item);
                }

                OnPropertyChanged(nameof(MediaItemCount));
                OnPropertyChanged(nameof(MissingMediaItemCount));
            });

            await LoadSavedAudiobookAnalysisAsync(librarySourceId, items);
        }
        catch (Exception exception)
        {
            RunOnUiThread(() => Status = exception.Message);
        }
    }

    private void ResetScanProgress()
    {
        ScanStage = LibraryScanStage.Starting;
        ScanDiscoveredCount = 0;
        ScanProcessedCount = 0;
        ScanAddedCount = 0;
        ScanRefreshedCount = 0;
        ScanMissingCount = 0;
        ScanCurrentPath = string.Empty;
    }

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = global::System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }

    private async Task RefreshSourcesAsync(Guid? selectedId = null)
    {
        var sources = await _librarySourceService.GetAllAsync();
        LibrarySources.Clear();
        foreach (var source in sources)
        {
            LibrarySources.Add(source);
        }

        SelectedSource = selectedId is null ? null : LibrarySources.FirstOrDefault(source => source.Id == selectedId);
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        if (IsBusy || IsScanRunning)
        {
            return;
        }

        try
        {
            IsBusy = true;
            await action();
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
