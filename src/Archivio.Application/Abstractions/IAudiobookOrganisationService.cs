namespace Archivio.Application.Abstractions;

public interface IAudiobookOrganisationService
{
    Task<IReadOnlyList<AudiobookCandidateGroup>> PrepareProposalsAsync(
        Guid librarySourceId,
        IReadOnlyList<AudiobookCandidateGroup> candidates,
        CancellationToken cancellationToken = default);
}
