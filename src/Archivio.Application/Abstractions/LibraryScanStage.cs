namespace Archivio.Application.Abstractions;

public enum LibraryScanStage
{
    Idle = 0,
    Starting = 1,
    Discovering = 2,
    Reconciling = 3,
    Saving = 4,
    Completed = 5,
    Cancelled = 6,
    Failed = 7
}
