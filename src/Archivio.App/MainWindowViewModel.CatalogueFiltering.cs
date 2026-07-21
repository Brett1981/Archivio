using System.ComponentModel;
using System.Windows.Data;
using Archivio.Application.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Archivio.App;

public sealed partial class MainWindowViewModel
{
    private ICollectionView? _mediaItemsView;

    public ICollectionView MediaItemsView
    {
        get
        {
            if (_mediaItemsView is null)
            {
                _mediaItemsView = CollectionViewSource.GetDefaultView(MediaItems);
                _mediaItemsView.Filter = FilterMediaItem;
                _mediaItemsView.CollectionChanged += (_, _) => OnPropertyChanged(nameof(VisibleMediaItemCount));
            }

            return _mediaItemsView;
        }
    }

    public int VisibleMediaItemCount => MediaItemsView.Cast<object>().Count();

    [ObservableProperty]
    private string _mediaSearchText = string.Empty;

    [ObservableProperty]
    private bool _showMissingOnly;

    partial void OnMediaSearchTextChanged(string value) => RefreshMediaItemsView();

    partial void OnShowMissingOnlyChanged(bool value) => RefreshMediaItemsView();

    private bool FilterMediaItem(object item)
    {
        if (item is not Domain.MediaItem mediaItem)
        {
            return false;
        }

        return MediaCatalogueFilter.Apply([mediaItem], MediaSearchText, ShowMissingOnly).Count == 1;
    }

    private void RefreshMediaItemsView()
    {
        MediaItemsView.Refresh();
        OnPropertyChanged(nameof(VisibleMediaItemCount));
    }
}
