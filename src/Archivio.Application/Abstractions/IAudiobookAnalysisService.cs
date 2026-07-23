using Archivio.Domain;

namespace Archivio.Application.Abstractions;

public interface IAudiobookAnalysisService
{
    IReadOnlyList<AudiobookCandidateGroup> Analyse(IEnumerable<MediaItem> mediaItems);
}
