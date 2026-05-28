using System.Net;
using System.Net.Http;
using System.Text;
using Jellyfin.Plugin.ImdbRatings.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ImdbRatings.Tests;

public class ImdbGraphqlClientTests
{
    [Fact]
    public async Task FetchRatingAsync_ReturnsParsedRatingAndVotes_WhenResponseIsValid()
    {
        var client = CreateClient("""{"data":{"title":{"ratingsSummary":{"aggregateRating":7.8,"voteCount":12345}}}}""");

        var result = await client.FetchRatingAsync("tt1234567", CancellationToken.None);

        Assert.True(result.HasValue);
        Assert.InRange(Math.Abs(result.Value.Rating - 7.8f), 0f, 0.0001f);
        Assert.Equal(12345, result.Value.Votes);
    }

    [Fact]
    public async Task FetchRatingAsync_ReturnsNull_WhenTitleIsNull()
    {
        var client = CreateClient("""{"data":{"title":null}}""");

        var result = await client.FetchRatingAsync("tt0000001", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchRatingAsync_ReturnsNull_WhenRatingSummaryIsNull()
    {
        var client = CreateClient("""{"data":{"title":{"ratingsSummary":null}}}""");

        var result = await client.FetchRatingAsync("tt0000001", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchRatingAsync_ReturnsNull_WhenResponseIsNotSuccess()
    {
        var client = CreateClient(string.Empty, HttpStatusCode.InternalServerError);

        var result = await client.FetchRatingAsync("tt0000001", CancellationToken.None);

        Assert.Null(result);
    }

    private static ImdbGraphqlClient CreateClient(string content, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        });

        var httpClientFactory = new StubHttpClientFactory(new HttpClient(handler));
        return new ImdbGraphqlClient(httpClientFactory, NullLogger<ImdbGraphqlClient>.Instance);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _httpClient;

        public StubHttpClientFactory(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public HttpClient CreateClient(string name) => _httpClient;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responseFactory(request));
    }
}
