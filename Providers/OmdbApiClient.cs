using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ImdbRatings.Providers;

public class OmdbApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OmdbApiClient> _logger;

    public OmdbApiClient(IHttpClientFactory httpClientFactory, ILogger<OmdbApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<(float Rating, int Votes)?> FetchRatingAsync(string imdbId, string apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imdbId) || string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        try
        {
            var requestUri = $"https://www.omdbapi.com/?i={Uri.EscapeDataString(imdbId)}&apikey={Uri.EscapeDataString(apiKey)}&r=json";
            var client = _httpClientFactory.CreateClient("ImdbRatings");
            using var response = await client.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("OMDb request failed for {ImdbId} with status code {StatusCode}", imdbId, response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var result = await JsonSerializer.DeserializeAsync<OmdbResponse>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result is null || !string.Equals(result.Response, "True", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(result.ImdbRating) || string.Equals(result.ImdbRating, "N/A", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!float.TryParse(result.ImdbRating, NumberStyles.Float, CultureInfo.InvariantCulture, out var rating))
            {
                return null;
            }

            var votesText = result.ImdbVotes?.Replace(",", string.Empty, StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(votesText)
                || string.Equals(votesText, "N/A", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(votesText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var votes))
            {
                return null;
            }

            return (rating, votes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch OMDb rating for {ImdbId}", imdbId);
            return null;
        }
    }

    private sealed class OmdbResponse
    {
        [JsonPropertyName("Response")]
        public string? Response { get; set; }

        [JsonPropertyName("imdbRating")]
        public string? ImdbRating { get; set; }

        [JsonPropertyName("imdbVotes")]
        public string? ImdbVotes { get; set; }
    }
}
