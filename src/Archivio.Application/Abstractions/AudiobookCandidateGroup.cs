using System.Security.Cryptography;
using System.Text;
using Archivio.Domain;

namespace Archivio.Application.Abstractions;

public sealed record AudiobookCandidateGroup(
    string DisplayName,
    string? Author,
    string Title,
    MetadataValueSource AuthorSource,
    MetadataValueSource TitleSource,
    bool HasLoadedLocalMetadata,
    IReadOnlyList<AudiobookCandidatePart> Parts,
    decimal Confidence,
    IReadOnlyList<string> Warnings)
{
    public OnlineMetadataSuggestion? OnlineSuggestion { get; init; }
    public AudiobookOrganisationProposal? OrganisationProposal { get; init; }
    public AudiobookBatchPlan? BatchPlan { get; init; }
    public bool IsMultipart => Parts.Count > 1;
    public bool NeedsReview => Confidence < 0.80m || Warnings.Count > 0;
    public string TypeLabel => IsMultipart ? "Multipart" : "Single file";
    public string ReviewLabel => NeedsReview ? "Needs review" : "High confidence";
    public string AuthorDisplay => string.IsNullOrWhiteSpace(Author) ? "Unknown author" : Author;
    public string AuthorSourceDisplay => new MetadataValue(Author, AuthorSource).SourceDisplay;
    public string TitleSourceDisplay => new MetadataValue(Title, TitleSource).SourceDisplay;
    public string MetadataProvenanceSummary => $"Title: {TitleSourceDisplay} · Author: {AuthorSourceDisplay}";
    public string MetadataStatusLabel => HasLoadedLocalMetadata
        ? "Local metadata loaded"
        : "Select to load local metadata";
    public bool HasOnlineSuggestion => OnlineSuggestion is not null;
    public bool HasOrganisationProposal => OrganisationProposal is not null;
    public bool IsPrimaryOrganisationPlan => OrganisationProposal?.IsPrimaryCandidate == true;
    public bool IsReviewClearedOrganisationPlan => IsPrimaryOrganisationPlan && !NeedsReview;
    public bool HasBatchPlan => BatchPlan is not null;
    public string ReviewAuthorDisplay => OrganisationProposal?.CanonicalAuthor ?? AuthorDisplay;
    public string ReviewTitle => OrganisationProposal?.CanonicalTitle ?? Title;
    public decimal ReviewConfidence => OrganisationProposal?.Confidence ?? Confidence;
    public int ReviewSourceFileCount => OrganisationProposal?.SourceFileCount ?? Parts.Count;
    public bool ReviewItemNeedsReview => BatchPlan?.IsBlocked == true ||
        (OrganisationProposal is not null
            ? !OrganisationProposal.ReadyForAutomaticHandling
            : NeedsReview);
    public string ReviewItemStatusLabel => ReviewItemNeedsReview ? "Needs review" : "High confidence";
    public string ReviewItemSummary => OrganisationProposal is null
        ? TypeLabel
        : $"1 audiobook · {ReviewSourceFileCount:N0} source file{(ReviewSourceFileCount == 1 ? string.Empty : "s")}";
    public string ReviewPlannedResult => OrganisationProposal is null
        ? "Analyse this candidate to prepare an organisation plan"
        : ReviewSourceFileCount > 1
            ? $"One folder with {ReviewSourceFileCount:N0} ordered tracks"
            : "One organised audiobook file";
    public string ReviewHandlingLabel => ReviewSourceFileCount > 1
        ? "Keep as ordered tracks"
        : OrganisationProposal?.ActionLabel ?? "Keep current file";
    public string ReviewHandlingDetail => ReviewSourceFileCount > 1
        ? "Available now · preserves chapters"
        : "Available now · non-destructive organisation";
    public string SourceFilesDisclosureLabel =>
        $"Show {ReviewSourceFileCount:N0} source file{(ReviewSourceFileCount == 1 ? string.Empty : "s")}";
    public string OnlineSuggestionLabel => OnlineSuggestion is null
        ? "No online suggestion"
        : OnlineSuggestion.ProvenanceDisplay;
    public string CandidateKey
    {
        get
        {
            var identity = string.Join(
                "|",
                Parts.Select(part => part.MediaItem.Id).OrderBy(id => id));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        }
    }

    public IReadOnlyList<string> ConfidenceReasons
    {
        get
        {
            var reasons = new List<string>();
            reasons.Add(string.IsNullOrWhiteSpace(Author)
                ? "Author could not be inferred."
                : $"Author came from {AuthorSourceDisplay.ToLowerInvariant()}.");
            reasons.Add(string.IsNullOrWhiteSpace(Title)
                ? "Title could not be inferred."
                : $"Title came from {TitleSourceDisplay.ToLowerInvariant()}.");
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

public sealed record AudiobookCandidatePart(
    MediaItem MediaItem,
    int Sequence,
    bool SequenceWasInferred,
    LocalMediaMetadata Metadata)
{
    public string OrderingStatus => SequenceWasInferred ? "Detected" : "Filename order";
}
