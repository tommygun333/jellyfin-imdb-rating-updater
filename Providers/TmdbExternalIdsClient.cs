using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ImdbRatings.Providers;

/// <summary>
/// Client for the TMDB API used to resolve TMDB Show/Movie IDs to IMDb IDs via external_ids endpoints.
/// </summary>
public class TmdbExternalIdsClient
{
    private const string TmdbBaseUrl = "https://api.themoviedb.org/3";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TmdbExternalIdsClient> _logger;

    public TmdbExternalIdsClient(IHttpClientFactory httpClientFactory, ILogger<TmdbExternalIdsClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Returns the IMDb ID for a TMDB TV show, or null if not available.
    /// </summary>
    public Task<string?> GetImdbIdForShowAsync(int tmdbShowId, string apiKey, CancellationToken cancellationToken)
    {
        var url = $"{TmdbBaseUrl}/tv/{tmdbShowId}/external_ids";
        return FetchImdbIdAsync(apiKey, $"TMDB show {tmdbShowId}", url, cancellationToken);
    }

    /// <summary>
    /// Returns the IMDb ID for a TMDB movie, or null if not available.
    /// </summary>
    public Task<string?> GetImdbIdForMovieAsync(int tmdbMovieId, string apiKey, CancellationToken cancellationToken)
    {
        var url = $"{TmdbBaseUrl}/movie/{tmdbMovieId}/external_ids";
        return FetchImdbIdAsync(apiKey, $"TMDB movie {tmdbMovieId}", url, cancellationToken);
    }

    private async Task<string?> FetchImdbIdAsync(
        string apiKey,
        string logContext,
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("TmdbClient");
            // Append api_key as a query parameter — not logged to avoid exposing the key.
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{url}?api_key={apiKey}");
            request.Headers.TryAddWithoutValidation("accept", "application/json");

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "TMDB external_ids request failed for {Context} with status {StatusCode}",
                    logContext,
                    response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var result = await JsonSerializer
                .DeserializeAsync<TmdbExternalIdsResponse>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var imdbId = result?.ImdbId;
            return string.IsNullOrWhiteSpace(imdbId) ? null : imdbId;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch TMDB external IDs for {Context}", logContext);
            return null;
        }
    }

    private sealed class TmdbExternalIdsResponse
    {
        [JsonPropertyName("imdb_id")]
        public string? ImdbId { get; set; }
    }
}
