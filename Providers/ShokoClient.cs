using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ImdbRatings.Providers;

/// <summary>
/// Client for the Shoko Server API used to resolve AniDB IDs to TMDB IDs.
/// </summary>
public class ShokoClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ShokoClient> _logger;

    public ShokoClient(IHttpClientFactory httpClientFactory, ILogger<ShokoClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Fetches TMDB Show and Movie IDs for a given AniDB series ID.
    /// </summary>
    public Task<ShokoTmdbIds?> GetSeriesTmdbIdsAsync(
        string serverUrl,
        string apiKey,
        string anidbSeriesId,
        CancellationToken cancellationToken)
    {
        var url = $"{serverUrl.TrimEnd('/')}/api/v3/Series/AniDB/{Uri.EscapeDataString(anidbSeriesId)}/Series?randomImages=false&includeDataFrom=TMDB";
        return FetchTmdbIdsAsync(apiKey, anidbSeriesId, url, cancellationToken);
    }

    /// <summary>
    /// Fetches TMDB Show and Movie IDs for a given AniDB episode ID.
    /// </summary>
    public Task<ShokoTmdbIds?> GetEpisodeTmdbIdsAsync(
        string serverUrl,
        string apiKey,
        string anidbEpisodeId,
        CancellationToken cancellationToken)
    {
        var url = $"{serverUrl.TrimEnd('/')}/api/v3/Episode/AniDB/{Uri.EscapeDataString(anidbEpisodeId)}/Episode?includeFiles=false&includeMediaInfo=false&includeAbsolutePaths=false&includeXRefs=false&includeDataFrom=TMDB";
        return FetchTmdbIdsAsync(apiKey, anidbEpisodeId, url, cancellationToken);
    }

    private async Task<ShokoTmdbIds?> FetchTmdbIdsAsync(
        string apiKey,
        string anidbId,
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("ShokoClient");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("accept", "text/plain");
            request.Headers.TryAddWithoutValidation("apikey", apiKey);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Shoko API returned {StatusCode} for AniDB ID {AniDbId}",
                    response.StatusCode,
                    anidbId);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var result = await JsonSerializer
                .DeserializeAsync<ShokoResponse>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var tmdb = result?.IDs?.TMDB;
            if (tmdb is null)
            {
                return null;
            }

            return new ShokoTmdbIds(
                tmdb.Show ?? Array.Empty<int>(),
                tmdb.Movie ?? Array.Empty<int>());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Shoko TMDB IDs for AniDB ID {AniDbId}", anidbId);
            return null;
        }
    }

    private sealed class ShokoResponse
    {
        [JsonPropertyName("IDs")]
        public ShokoIds? IDs { get; set; }
    }

    private sealed class ShokoIds
    {
        [JsonPropertyName("TMDB")]
        public ShokoTmdbSection? TMDB { get; set; }
    }

    private sealed class ShokoTmdbSection
    {
        [JsonPropertyName("Show")]
        public int[]? Show { get; set; }

        [JsonPropertyName("Movie")]
        public int[]? Movie { get; set; }
    }
}

/// <summary>
/// TMDB Show and Movie IDs returned by the Shoko Server API.
/// </summary>
public sealed record ShokoTmdbIds(int[] ShowIds, int[] MovieIds);
