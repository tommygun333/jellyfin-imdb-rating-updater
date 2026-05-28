using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ImdbRatings.Providers;

public class ImdbGraphqlClient
{
    private const string GraphqlEndpoint = "https://caching.graphql.imdb.com/";

    private static readonly string RatingsQuery = """
        query GetTitle($id: ID!) {
          title(id: $id) {
            ratingsSummary {
              aggregateRating
              voteCount
            }
          }
        }
        """;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ImdbGraphqlClient> _logger;

    public ImdbGraphqlClient(IHttpClientFactory httpClientFactory, ILogger<ImdbGraphqlClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<(float Rating, int Votes)?> FetchRatingAsync(string imdbId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return null;
        }

        try
        {
            var payload = new
            {
                query = RatingsQuery,
                operationName = "GetTitle",
                variables = new { id = imdbId }
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var client = _httpClientFactory.CreateClient("ImdbRatings");
            using var request = new HttpRequestMessage(HttpMethod.Post, GraphqlEndpoint)
            {
                Content = content
            };

            request.Headers.TryAddWithoutValidation("accept", "application/graphql+json, application/json");
            request.Headers.TryAddWithoutValidation("accept-language", "en-US,en;q=0.9");
            request.Headers.TryAddWithoutValidation("origin", "https://www.imdb.com");
            request.Headers.TryAddWithoutValidation(
                "user-agent",
                "Mozilla/5.0 (Linux; Android 6.0; Nexus 5 Build/MRA58N) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Mobile Safari/537.36");

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("IMDb GraphQL request failed for {ImdbId} with status code {StatusCode}", imdbId, response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var result = await JsonSerializer.DeserializeAsync<GraphqlResponse>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            var ratings = result?.Data?.Title?.RatingsSummary;
            if (ratings is null)
            {
                return null;
            }

            if (!ratings.AggregateRating.HasValue || !ratings.VoteCount.HasValue)
            {
                return null;
            }

            return ((float)ratings.AggregateRating.Value, ratings.VoteCount.Value);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch IMDb GraphQL rating for {ImdbId}", imdbId);
            return null;
        }
    }

    private sealed class GraphqlResponse
    {
        [JsonPropertyName("data")]
        public GraphqlData? Data { get; set; }
    }

    private sealed class GraphqlData
    {
        [JsonPropertyName("title")]
        public GraphqlTitle? Title { get; set; }
    }

    private sealed class GraphqlTitle
    {
        [JsonPropertyName("ratingsSummary")]
        public RatingsSummary? RatingsSummary { get; set; }
    }

    private sealed class RatingsSummary
    {
        [JsonPropertyName("aggregateRating")]
        public double? AggregateRating { get; set; }

        [JsonPropertyName("voteCount")]
        public int? VoteCount { get; set; }
    }
}
