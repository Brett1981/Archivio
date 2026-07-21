using Archivio.Application.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Options;

namespace Archivio.App;

public sealed partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel(IOptions<ArchivioOptions> options)
    {
        Title = options.Value.ProductName;
        Version = options.Value.Version;
    }

    public string Title { get; }
    public string Version { get; }

    [ObservableProperty]
    private string _status = "Foundation ready";
}
