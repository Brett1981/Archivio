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
    public bool NeedsReview => Confidence < 0.80m || Warnings.Count > 0;
    public string TypeLabel => IsMultipart ? "Multipart" : "Single file";
    public string ReviewLabel => NeedsReview ? "Needs review" : "High confidence";
    public string AuthorDisplay => string.IsNullOrWhiteSpace(Author) ? "Unknown author" : Author;

    public IReadOnlyList<string> ConfidenceReasons
    {
        get
        {
            var reasons = new List<string>();
            reasons.Add(string.IsNullOrWhiteSpace(Author)
                ? "Author could not be inferred."
                : "Author inferred from filename or folder structure.");
            reasons.Add(string.IsNullOrWhiteSpace(Title)
                ? "Title could not be inferred."
                : "Title inferred from filename or folder structure.");
            reasons.Add(IsMultipart
                ? Parts.All(part => part.SequenceWasInferred)
                    ? "All multipart sequence numbers were inferred."
                    : "One or more multipart sequence numbers use filename ordering."
                : "Single-file audiobook does not require part ordering.");
            reasons.Add(Warnings.Count == 0
                ? "No analysis warnings were detected."
                : $"{Warnings.Count} analysis warning{(Warnings.Count == 1 ? string.Empty : "s")} require review.");
            return reasons;
        }
    }
}

public sealed record AudiobookCandidatePart(MediaItem MediaItem, int Sequence, bool SequenceWasInferred)
{
    public string OrderingStatus => SequenceWasInferred ? "Detected" : "Filename order";
}
