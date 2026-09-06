using Archivio.Application.Abstractions;

namespace Archivio.Infrastructure;

internal sealed class OpenLibraryCoverArtworkProvider : IAudiobookCoverArtworkProvider, IDisposable
{
    private const int MaximumArtworkBytes = 10 * 1024 * 1024;
    private readonly HttpClient _httpClient = CreateHttpClient();

    public async Task<AudiobookArtwork?> FetchAsync(
        string coverUrl,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(coverUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "covers.openlibrary.org", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        using var response = await _httpClient.GetAsync(
            UseLargeCover(uri),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode ||
            response.Content.Headers.ContentLength > MaximumArtworkBytes)
        {
            return null;
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (destination.Length + read > MaximumArtworkBytes)
            {
                return null;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        var data = destination.ToArray();
        var mimeType = DetectMimeType(data);
        return mimeType is null || data.Length == 0 ? null : new AudiobookArtwork(mimeType, data);
    }

    public void Dispose() => _httpClient.Dispose();

    private static Uri UseLargeCover(Uri uri)
    {
        var builder = new UriBuilder(uri);
        if (builder.Path.EndsWith("-M.jpg", StringComparison.OrdinalIgnoreCase))
        {
            builder.Path = builder.Path[..^6] + "-L.jpg";
        }

        if (string.IsNullOrWhiteSpace(builder.Query))
        {
            builder.Query = "default=false";
        }

        return builder.Uri;
    }

    private static string? DetectMimeType(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "image/jpeg";
        }

        ReadOnlySpan<byte> pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        return data.StartsWith(pngSignature) ? "image/png" : null;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Metaroq/0.2 (local media organiser)");
        return client;
    }
}
