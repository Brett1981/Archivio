using System.IO;
using System.Windows;
using Archivio.Application;
using Archivio.Application.Abstractions;
using Archivio.Infrastructure;
using Archivio.Media;
using Archivio.Metadata;
using Archivio.Persistence;
using Archivio.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Archivio.App;

public partial class App : System.Windows.Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var bootstrapPaths = new LocalAppDataPaths();
            bootstrapPaths.EnsureCreated();

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(
                    Path.Combine(bootstrapPaths.LogsDirectory, "metaroq-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14)
                .CreateLogger();

            _host = Host.CreateDefaultBuilder(e.Args)
                .UseSerilog()
                .ConfigureAppConfiguration(configuration =>
                {
                    configuration.SetBasePath(AppContext.BaseDirectory);
                    configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                })
                .ConfigureServices((context, services) =>
                {
                    services.AddApplication(context.Configuration);
                    services.AddInfrastructure();
                    services.AddPersistence();
                    services.AddMedia();
                    services.AddWorkers();
                    services.AddSingleton<ILocalMediaMetadataService, LocalMediaMetadataService>();
                    services.AddSingleton<IAudiobookMetadataWriter, TagLibAudiobookMetadataWriter>();
                    services.AddSingleton<IFolderPickerService, FolderPickerService>();
                    services.AddSingleton<IAudiobookExecutionConfirmationService, AudiobookExecutionConfirmationService>();
                    services.AddSingleton<MainWindowViewModel>();
                    services.AddSingleton<MainWindow>();
                })
                .Build();

            await _host.StartAsync();
            await _host.Services.GetRequiredService<IDatabaseInitializer>().InitializeAsync();

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "Metaroq failed during startup");
            MessageBox.Show(exception.Message, "Metaroq startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        await Log.CloseAndFlushAsync();
        base.OnExit(e);
    }
}
