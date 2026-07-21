using System.Collections.ObjectModel;
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

    public MainWindowViewModel(
        IOptions<ArchivioOptions> options,
        ILibrarySourceService librarySourceService,
        IFolderPickerService folderPickerService)
    {
        _librarySourceService = librarySourceService;
        _folderPickerService = folderPickerService;
        Title = options.Value.ProductName;
        Version = options.Value.Version;
        SourceTypes = Enum.GetValues<LibrarySourceType>();
    }

    public string Title { get; }
    public string Version { get; }
    public IReadOnlyList<LibrarySourceType> SourceTypes { get; }
    public ObservableCollection<LibrarySource> LibrarySources { get; } = [];

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
    private LibrarySource? _selectedSource;

    [ObservableProperty]
    private string _status = "Loading library sources...";

    [ObservableProperty]
    private bool _isBusy;

    partial void OnSelectedSourceChanged(LibrarySource? value)
    {
        if (value is null)
        {
            return;
        }

        SourceName = value.Name;
        SourcePath = value.Path;
        SourceType = value.Type;
        SourceIsEnabled = value.IsEnabled;
        Status = $"Editing {value.Name}";
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

    private bool CanSave() =>
        !IsBusy &&
        !string.IsNullOrWhiteSpace(SourceName) &&
        !string.IsNullOrWhiteSpace(SourcePath);

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
                saved = await _librarySourceService.UpdateAsync(
                    SelectedSource.Id,
                    SourceName,
                    SourcePath,
                    SourceType,
                    SourceIsEnabled);
                Status = $"Updated {saved.Name}";
            }

            await RefreshSourcesAsync(saved.Id);
        });
    }

    private bool CanDelete() => !IsBusy && SelectedSource is not null;

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

    private async Task RefreshSourcesAsync(Guid? selectedId = null)
    {
        var sources = await _librarySourceService.GetAllAsync();
        LibrarySources.Clear();

        foreach (var source in sources)
        {
            LibrarySources.Add(source);
        }

        SelectedSource = selectedId is null
            ? null
            : LibrarySources.FirstOrDefault(source => source.Id == selectedId);
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            SaveCommand.NotifyCanExecuteChanged();
            DeleteCommand.NotifyCanExecuteChanged();
            await action();
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
        finally
        {
            IsBusy = false;
            SaveCommand.NotifyCanExecuteChanged();
            DeleteCommand.NotifyCanExecuteChanged();
        }
    }
}
