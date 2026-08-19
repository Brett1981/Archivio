using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Archivio.Application.Abstractions;

namespace Archivio.Application.Services;

public sealed class OpenLibraryMetadataProvider : IBookMetadataProvider, IDisposable
{
    private static readonly TimeSpan DefaultMinimumRequestInterval = TimeSpan.FromSeconds(1);
    private static readonly IReadOnlyList<TimeSpan> DefaultRetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _minimumRequestInterval;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;
    private readonly SemaphoreSlim _rateLimit = new(1, 1);
    private DateTime _nextRequestAtUtc;

    public OpenLibraryMetadataProvider() :
        this(CreateHttpClient(), DefaultMinimumRequestInterval, DefaultRetryDelays)
    {
    }

    public OpenLibraryMetadataProvider(HttpClient httpClient) :
        this(httpClient, DefaultMinimumRequestInterval, DefaultRetryDelays)
    {
    }

    public OpenLibraryMetadataProvider(
        HttpClient httpClient,
        TimeSpan minimumRequestInterval,
        IReadOnlyList<TimeSpan> retryDelays)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(retryDelays);
        if (minimumRequestInterval < TimeSpan.Zero || retryDelays.Any(delay => delay < TimeSpan.Zero))
        {
            throw new ArgumentOutOfRangeException(nameof(minimumRequestInterval));
        }

        _httpClient = httpClient;
        _minimumRequestInterval = minimumRequestInterval;
        _retryDelays = retryDelays;
    }

    public string Name => "Open Library";

    public async Task<IReadOnlyList<OnlineBookSearchResult>> SearchAsync(
        IReadOnlyList<OnlineMetadataQuery> queries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queries);
        if (queries.Count == 0)
        {
            return [];
        }

        var clauses = queries.Select(query =>
        {
            var title = EscapeQueryValue(query.Title);
            return string.IsNullOrWhiteSpace(query.Author)
                ? $"title:\"{title}\""
                : $"(title:\"{title}\" AND author:\"{EscapeQueryValue(query.Author)}\")";
        }).Distinct(StringComparer.Ordinal);
        var queryText = string.Join(" OR ", clauses);
        var requestUri = "search.json?q=" + Uri.EscapeDataString(queryText) +
            "&fields=key,title,author_name,first_publish_year,subject,cover_i&limit=50&lang=en";

        await _rateLimit.WaitAsync(cancellationToken);
        try
        {
            using var response = await GetWithRetryAsync(requestUri, cancellationToken);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<SearchResponse>(cancellationToken);

            return payload?.Docs
                .Where(document => !string.IsNullOrWhiteSpace(document.Key) &&
                                   !string.IsNullOrWhiteSpace(document.Title))
                .Select(document => new OnlineBookSearchResult(
                    Name,
                    document.Key!,
                    document.Title!,
                    document.AuthorNames ?? [],
                    document.FirstPublishYear,
                    document.Subjects?.Take(20).ToList() ?? [],
                    document.CoverId is null
                        ? null
                        : $"https://covers.openlibrary.org/b/id/{document.CoverId}-M.jpg",
                    $"https://openlibrary.org{document.Key}"))
                .ToList() ?? [];
        }
        finally
        {
            _rateLimit.Release();
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _rateLimit.Dispose();
    }

    private static string EscapeQueryValue(string value) =>
        value.Trim()[..Math.Min(150, value.Trim().Length)]
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static HttpClient CreateHttpClient()
    {
        var httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://openlibrary.org/"),
            Timeout = TimeSpan.FromSeconds(15)
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Metaroq/0.1 (local media organiser)");
        return httpClient;
    }

    private async Task<HttpResponseMessage> GetWithRetryAsync(
        string requestUri,
        CancellationToken cancellationToken)
    {
        var attemptCount = _retryDelays.Count + 1;
        for (var attempt = 0; attempt < attemptCount; attempt++)
        {
            await WaitForRequestWindowAsync(cancellationToken);
            try
            {
                var response = await _httpClient.GetAsync(requestUri, cancellationToken);
                _nextRequestAtUtc = DateTime.UtcNow + _minimumRequestInterval;
                if (!IsTransient(response.StatusCode))
                {
                    return response;
                }

                if (attempt == attemptCount - 1)
                {
                    var statusCode = response.StatusCode;
                    response.Dispose();
                    throw new HttpRequestException(
                        $"Open Library returned {(int)statusCode} after {attemptCount} attempts.",
                        null,
                        statusCode);
                }

                var retryDelay = GetRetryDelay(response, attempt);
                response.Dispose();
                await Task.Delay(retryDelay, cancellationToken);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                _nextRequestAtUtc = DateTime.UtcNow + _minimumRequestInterval;
                if (attempt == attemptCount - 1)
                {
                    throw new HttpRequestException(
                        $"Open Library did not respond after {attemptCount} attempts.",
                        exception);
                }

                await Task.Delay(_retryDelays[attempt], cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                _nextRequestAtUtc = DateTime.UtcNow + _minimumRequestInterval;
                if (attempt == attemptCount - 1)
                {
                    throw new HttpRequestException(
                        $"Open Library could not be reached after {attemptCount} attempts.",
                        exception);
                }

                await Task.Delay(_retryDelays[attempt], cancellationToken);
            }
        }

        throw new HttpRequestException("Open Library could not be reached.");
    }

    private async Task WaitForRequestWindowAsync(CancellationToken cancellationToken)
    {
        var delay = _nextRequestAtUtc - DateTime.UtcNow;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }
    }

    private TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is not null)
        {
            retryAfter = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
        }

        return retryAfter is null
            ? _retryDelays[attempt]
            : TimeSpan.FromTicks(Math.Clamp(retryAfter.Value.Ticks, 0, TimeSpan.FromSeconds(30).Ticks));
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;

    private sealed record SearchResponse(
        [property: JsonPropertyName("docs")] IReadOnlyList<SearchDocument> Docs);

    private sealed record SearchDocument(
        [property: JsonPropertyName("key")] string? Key,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("author_name")] IReadOnlyList<string>? AuthorNames,
        [property: JsonPropertyName("first_publish_year")] int? FirstPublishYear,
        [property: JsonPropertyName("subject")] IReadOnlyList<string>? Subjects,
        [property: JsonPropertyName("cover_i")] int? CoverId);
}
