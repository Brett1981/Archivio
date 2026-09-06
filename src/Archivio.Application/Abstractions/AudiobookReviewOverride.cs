namespace Archivio.Application.Abstractions;

public static class AudiobookGenreCategories
{
    public static IReadOnlyList<string> All { get; } =
    [
        "Biography & Memoir",
        "Children & Young Adult",
        "Fantasy",
        "Fiction",
        "History",
        "Horror",
        "Mystery & Thriller",
        "Non-fiction",
        "Romance",
        "Science Fiction"
    ];

    public static bool Contains(string value) =>
        All.Contains(value, StringComparer.Ordinal);
}

public enum AudiobookCollectionHandling
{
    Automatic = 0,
    SeparateBooks = 1
}

public sealed record AudiobookReviewOverrideEntry(
    string PlanKey,
    string? CanonicalAuthor,
    string? CanonicalTitle,
    string? GenreCategory,
    DateTime UpdatedAtUtc,
    AudiobookCollectionHandling CollectionHandling = AudiobookCollectionHandling.Automatic,
    string? SeriesName = null,
    int? SeriesPosition = null);
