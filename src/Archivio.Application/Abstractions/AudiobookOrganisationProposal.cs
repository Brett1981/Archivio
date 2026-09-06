namespace Archivio.Application.Abstractions;

public enum AudiobookOrganisationAction
{
    Keep = 0,
    Rename = 1,
    MoveAndRename = 2,
    OrganiseMultipart = 3,
    ConsolidateCandidates = 4
}

public sealed record AudiobookOrganisationProposal(
    string PlanKey,
    string CanonicalAuthor,
    string CanonicalTitle,
    int? FirstPublishedYear,
    string GenreCategory,
    string SuggestedRelativeFolder,
    string SuggestedFileNamePattern,
    AudiobookOrganisationAction RecommendedAction,
    int RelatedCandidateCount,
    int SourceFileCount,
    bool IsPrimaryCandidate,
    bool UsesOnlineMetadata,
    decimal Confidence,
    bool ReadyForAutomaticHandling,
    bool FutureCombineCandidate,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Warnings,
    DateTime GeneratedAtUtc,
    bool UsesManualGenre = false,
    bool UsesManualAuthor = false,
    bool UsesManualTitle = false,
    AudiobookCollectionHandling CollectionHandling = AudiobookCollectionHandling.Automatic,
    string? SeriesName = null,
    int? SeriesPosition = null,
    string? CollectionPlanKey = null)
{
    public string CanonicalDisplay => $"{CanonicalAuthor} — {CanonicalTitle}";
    public string YearDisplay => FirstPublishedYear is null
        ? "Year unavailable"
        : FirstPublishedYear.Value.ToString();
    public string ActionLabel => RecommendedAction switch
    {
        AudiobookOrganisationAction.Keep => "Already organised",
        AudiobookOrganisationAction.Rename => "Rename file",
        AudiobookOrganisationAction.MoveAndRename => "Move and rename",
        AudiobookOrganisationAction.OrganiseMultipart => "Organise multipart files",
        AudiobookOrganisationAction.ConsolidateCandidates => "Consolidate split candidates",
        _ => "Review organisation plan"
    };
    public string ReadinessLabel => ReadyForAutomaticHandling
        ? "Ready for future automatic handling"
        : "Review before future automatic handling";
    public string GroupSummary => RelatedCandidateCount == 1
        ? $"{SourceFileCount:N0} source file{(SourceFileCount == 1 ? string.Empty : "s")}"
        : $"{RelatedCandidateCount:N0} related candidates · {SourceFileCount:N0} source files";
    public string ProposalSummary => $"{ActionLabel} · {GenreCategory}";
    public string GenreSourceLabel => UsesManualGenre ? "Confirmed by you" : "Suggested by Metaroq";
    public string IdentitySourceLabel => UsesManualAuthor || UsesManualTitle
        ? "Author and title confirmed by you"
        : "Author and title suggested by Metaroq";
    public bool IsSeparateBookPlan => CollectionHandling == AudiobookCollectionHandling.SeparateBooks;
    public string SeriesDisplay => string.IsNullOrWhiteSpace(SeriesName)
        ? "No series assigned"
        : SeriesPosition is null
            ? SeriesName
            : $"{SeriesName} · Book {SeriesPosition}";
    public string DestinationDisplay => $"{SuggestedRelativeFolder}  →  {SuggestedFileNamePattern}";
    public string FutureOptionLabel => FutureCombineCandidate
        ? "Future option: assess combining these files into one audiobook after format compatibility checks."
        : "Single-file audiobook; combining is not required.";
}

public sealed record AudiobookOrganisationCacheEntry(
    string CandidateKey,
    string InputSignature,
    DateTime GeneratedAtUtc,
    AudiobookOrganisationProposal Proposal);
