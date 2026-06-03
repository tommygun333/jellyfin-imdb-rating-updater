using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ImdbRatings.Providers;

public class TmdbExternalIdsClient
{
    private const string TmdbApiBase = "https://api.themoviedb.org/3";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TmdbExternalIdsClient> _logger;

    public TmdbExternalIdsClient(IHttpClientFactory httpClientFactory, ILogger<TmdbExternalIdsClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Fetches the IMDb ID for a TMDb title by calling the TMDb external IDs endpoint.
    /// </summary>
    /// <param name="tmdbId">The TMDb ID of the item.</param>
    /// <param name="isTvShow"><c>true</c> for TV series; <c>false</c> for movies.</param>
    /// <param name="apiKey">The TMDb API key (v3 auth).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The IMDb ID (e.g. <c>tt1234567</c>) or <c>null</c> if not available.</returns>
    public async Task<string?> FetchImdbIdAsync(
        string tmdbId,
        bool isTvShow,
        string apiKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tmdbId) || string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var mediaType = isTvShow ? "tv" : "movie";
        var url = $"{TmdbApiBase}/{mediaType}/{Uri.EscapeDataString(tmdbId)}/external_ids?api_key={Uri.EscapeDataString(apiKey)}";

        try
        {
            var client = _httpClientFactory.CreateClient("ImdbRatings");
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "TMDb external IDs request failed for {MediaType} {TmdbId} with status {StatusCode}",
                    mediaType, tmdbId, response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var result = await JsonSerializer
                .DeserializeAsync<TmdbExternalIdsResponse>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var imdbId = result?.ImdbId;
            if (string.IsNullOrWhiteSpace(imdbId))
            {
                return null;
            }

            return imdbId;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch TMDb external IDs for {MediaType} {TmdbId}", mediaType, tmdbId);
            return null;
        }
    }

    private sealed class TmdbExternalIdsResponse
    {
        [JsonPropertyName("imdb_id")]
        public string? ImdbId { get; set; }
    }
}
