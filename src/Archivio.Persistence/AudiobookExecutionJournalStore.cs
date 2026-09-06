using Archivio.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Archivio.Persistence;

internal sealed class AudiobookExecutionJournalStore(
    IDbContextFactory<ArchivioDbContext> dbContextFactory) : IAudiobookExecutionJournalStore
{
    public async Task CreateAsync(
        AudiobookExecutionRunEntry run,
        CancellationToken cancellationToken = default)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        context.AudiobookExecutionRuns.Add(new AudiobookExecutionRunEntity
        {
            Id = run.Id,
            LibrarySourceId = run.LibrarySourceId,
            Status = (int)run.Status,
            PlannedOperationCount = run.PlannedOperationCount,
            CompletedOperationCount = run.CompletedOperationCount,
            RolledBackOperationCount = run.RolledBackOperationCount,
            StartedAtUtc = run.StartedAtUtc,
            UpdatedAtUtc = run.UpdatedAtUtc,
            CompletedAtUtc = run.CompletedAtUtc,
            ErrorMessage = run.ErrorMessage,
            Operations = run.Operations.Select(operation => new AudiobookExecutionOperationEntity
            {
                Id = operation.Id,
                SortOrder = operation.SortOrder,
                PlanKey = operation.PlanKey,
                InputSignature = operation.InputSignature,
                MediaItemId = operation.MediaItemId,
                SourceRelativePath = operation.SourceRelativePath,
                DestinationRelativePath = operation.DestinationRelativePath,
                Kind = (int)operation.Kind,
                Status = (int)operation.Status,
                SourceSizeBytes = operation.SourceSizeBytes,
                SourceModifiedAtUtc = operation.SourceModifiedAtUtc,
                ErrorMessage = operation.ErrorMessage,
                OriginalMetadataJson = operation.OriginalMetadataJson
            }).ToList()
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateRunAsync(
        Guid runId,
        AudiobookExecutionRunStatus status,
        int completedOperationCount,
        int rolledBackOperationCount,
        string? errorMessage,
        DateTime updatedAtUtc,
        DateTime? completedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await context.AudiobookExecutionRuns
            .SingleAsync(entity => entity.Id == runId, cancellationToken);
        run.Status = (int)status;
        run.CompletedOperationCount = completedOperationCount;
        run.RolledBackOperationCount = rolledBackOperationCount;
        run.ErrorMessage = errorMessage;
        run.UpdatedAtUtc = updatedAtUtc;
        run.CompletedAtUtc = completedAtUtc;
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateOperationAsync(
        Guid operationId,
        AudiobookExecutionOperationStatus status,
        string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var operation = await context.AudiobookExecutionOperations
            .SingleAsync(entity => entity.Id == operationId, cancellationToken);
        operation.Status = (int)status;
        operation.ErrorMessage = errorMessage;
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<AudiobookExecutionRunEntry?> LoadLatestAsync(
        Guid librarySourceId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await context.AudiobookExecutionRuns
            .AsNoTracking()
            .Include(entity => entity.Operations)
            .Where(entity => entity.LibrarySourceId == librarySourceId)
            .OrderByDescending(entity => entity.StartedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (run is null)
        {
            return null;
        }

        return new AudiobookExecutionRunEntry(
            run.Id,
            run.LibrarySourceId,
            ReadEnum<AudiobookExecutionRunStatus>(run.Status),
            run.PlannedOperationCount,
            run.CompletedOperationCount,
            run.RolledBackOperationCount,
            AsUtc(run.StartedAtUtc),
            AsUtc(run.UpdatedAtUtc),
            run.CompletedAtUtc is null ? null : AsUtc(run.CompletedAtUtc.Value),
            run.ErrorMessage,
            run.Operations
                .OrderBy(operation => operation.SortOrder)
                .Select(operation => new AudiobookExecutionOperationEntry(
                    operation.Id,
                    operation.SortOrder,
                    operation.PlanKey,
                    operation.InputSignature,
                    operation.MediaItemId,
                    operation.SourceRelativePath,
                    operation.DestinationRelativePath,
                    ReadEnum<AudiobookFileOperationKind>(operation.Kind),
                    ReadEnum<AudiobookExecutionOperationStatus>(operation.Status),
                    operation.SourceSizeBytes,
                    AsUtc(operation.SourceModifiedAtUtc),
                    operation.ErrorMessage,
                    operation.OriginalMetadataJson))
                .ToList());
    }

    private static TEnum ReadEnum<TEnum>(int value) where TEnum : struct, Enum =>
        Enum.IsDefined(typeof(TEnum), value) ? (TEnum)Enum.ToObject(typeof(TEnum), value) : default;

    private static DateTime AsUtc(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
