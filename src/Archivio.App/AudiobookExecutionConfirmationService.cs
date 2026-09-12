using System.Windows;

namespace Archivio.App;

internal sealed class AudiobookExecutionConfirmationService : IAudiobookExecutionConfirmationService
{
    public bool ConfirmExecution(int planCount, int operationCount)
    {
        var planLabel = planCount == 1 ? "plan" : "plans";
        var fileLabel = operationCount == 1 ? "file" : "files";
        var message =
            $"Metaroq is ready to execute {planCount:N0} approved {planLabel} covering {operationCount:N0} {fileLabel}.\n\n" +
            "Each file's embedded metadata will be updated to the approved author, title, genre, year, series, track order, and available cover artwork. " +
            "A Plex-compatible cover file will also be added to the audiobook folder when artwork is available; existing cover files are preserved. " +
            "Every source and destination will be checked again, and existing destinations will never be overwritten. " +
            "If an individual file is locked or cannot be updated, that file will be skipped and processing will continue; successful files will remain at their destinations. " +
            "An explicit cancellation or a system-level interruption will still use the execution journal for safe recovery.\n\nContinue?";
        return MessageBox.Show(
            global::System.Windows.Application.Current?.MainWindow,
            message,
            "Execute approved plans",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public bool ConfirmRecovery(int operationCount)
    {
        var fileLabel = operationCount == 1 ? "operation" : "operations";
        return MessageBox.Show(
            global::System.Windows.Application.Current?.MainWindow,
            $"Metaroq found an interrupted execution containing {operationCount:N0} {fileLabel}. " +
            "Recovery will restore journalled metadata and only move recorded destinations back to their original sources when that can be done without overwriting anything.\n\nContinue with recovery?",
            "Recover interrupted execution",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }
}
