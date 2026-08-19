using Archivio.Domain;

namespace Archivio.Application.Services;

public static class MediaCatalogueFilter
{
    public static IReadOnlyList<MediaItem> Apply(
        IEnumerable<MediaItem> items,
        string? searchText,
        bool missingOnly)
    {
        ArgumentNullException.ThrowIfNull(items);

        var query = items;

        if (missingOnly)
        {
            query = query.Where(item => item.IsMissing);
        }

        var normalizedSearch = searchText?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedSearch))
        {
            query = query.Where(item =>
                item.FileName.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase) ||
                item.RelativePath.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase) ||
                item.Extension.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase));
        }

        return query
            .OrderBy(item => item.IsMissing)
            .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
