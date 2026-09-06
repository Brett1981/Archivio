namespace Archivio.App;

public interface IAudiobookExecutionConfirmationService
{
    bool ConfirmExecution(int planCount, int operationCount);
    bool ConfirmRecovery(int operationCount);
}
