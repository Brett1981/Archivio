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
            "Every source and destination will be checked again. Existing destinations will never be overwritten. " +
            "If a later move fails, completed moves will be rolled back where safe.\n\nContinue?";
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
            "Recovery will only move recorded destinations back to their original sources when that can be done without overwriting anything.\n\nContinue with recovery?",
            "Recover interrupted execution",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }
}
