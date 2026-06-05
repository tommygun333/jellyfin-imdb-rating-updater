using System;
using System.Linq;
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

            // Extract episode-level TMDB cross-references from the TMDB.Episodes section,
            // which is populated when includeDataFrom=TMDB is requested for an episode endpoint.
            var episodeCrossRefs = result?.TMDB?.Episodes
                ?.Where(e => e.ShowId != 0 && e.EpisodeNumber > 0)
                ?.Select(e => new ShokoTmdbEpisodeCrossRef(e.ShowId, e.SeasonNumber, e.EpisodeNumber))
                ?.ToArray() ?? Array.Empty<ShokoTmdbEpisodeCrossRef>();

            return new ShokoTmdbIds(
                tmdb.Show ?? Array.Empty<int>(),
                tmdb.Movie ?? Array.Empty<int>(),
                episodeCrossRefs);
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

        /// <summary>
        /// Full TMDB data section, populated when includeDataFrom=TMDB is requested.
        /// Present on episode responses and contains cross-reference details including
        /// season and episode numbers needed for per-episode IMDb resolution.
        /// </summary>
        [JsonPropertyName("TMDB")]
        public ShokoTmdbData? TMDB { get; set; }
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

    private sealed class ShokoTmdbData
    {
        [JsonPropertyName("Episodes")]
        public ShokoTmdbEpisodeData[]? Episodes { get; set; }
    }

    private sealed class ShokoTmdbEpisodeData
    {
        [JsonPropertyName("ShowID")]
        public int ShowId { get; set; }

        [JsonPropertyName("SeasonNumber")]
        public int SeasonNumber { get; set; }

        [JsonPropertyName("EpisodeNumber")]
        public int EpisodeNumber { get; set; }
    }
}

/// <summary>
/// TMDB Show and Movie IDs, plus per-episode cross-references, returned by the Shoko Server API.
/// </summary>
public sealed record ShokoTmdbIds(int[] ShowIds, int[] MovieIds, ShokoTmdbEpisodeCrossRef[] EpisodeCrossRefs);

/// <summary>
/// TMDB episode cross-reference data including the parent show ID and the episode's position
/// within the show. Used to call the TMDB per-episode external_ids API.
/// </summary>
public sealed record ShokoTmdbEpisodeCrossRef(int ShowId, int SeasonNumber, int EpisodeNumber);
