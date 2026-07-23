using Archivio.Domain;

namespace Archivio.Application.Abstractions;

public sealed record AudiobookCandidateGroup(
    string DisplayName,
    string? Author,
    string Title,
    IReadOnlyList<AudiobookCandidatePart> Parts,
    decimal Confidence,
    IReadOnlyList<string> Warnings)
{
    public bool IsMultipart => Parts.Count > 1;
}

public sealed record AudiobookCandidatePart(MediaItem MediaItem, int Sequence, bool SequenceWasInferred);
