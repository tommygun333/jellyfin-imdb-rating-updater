using System.Net;
using System.Net.Http;
using System.Text;
using Jellyfin.Plugin.ImdbRatings.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ImdbRatings.Tests;

public class OmdbApiClientTests
{
    [Fact]
    public async Task FetchRatingAsync_ReturnsParsedRatingAndVotes_WhenResponseIsValid()
    {
        var client = CreateClient("""{"Response":"True","imdbRating":"7.8","imdbVotes":"12,345"}""");

        var result = await client.FetchRatingAsync("tt1234567", "apikey", CancellationToken.None);

        Assert.True(result.HasValue);
        Assert.InRange(Math.Abs(result.Value.Rating - 7.8f), 0f, 0.0001f);
        Assert.Equal(12345, result.Value.Votes);
    }

    [Fact]
    public async Task FetchRatingAsync_ReturnsNull_WhenOmdbReturnsFalse()
    {
        var client = CreateClient("""{"Response":"False","Error":"Movie not found!"}""");

        var result = await client.FetchRatingAsync("tt0000001", "apikey", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchRatingAsync_ReturnsNull_WhenRatingIsNotAvailable()
    {
        var client = CreateClient("""{"Response":"True","imdbRating":"N/A","imdbVotes":"N/A"}""");

        var result = await client.FetchRatingAsync("tt0000001", "apikey", CancellationToken.None);

        Assert.Null(result);
    }

    private static OmdbApiClient CreateClient(string content, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        });

        var httpClientFactory = new StubHttpClientFactory(new HttpClient(handler));
        return new OmdbApiClient(httpClientFactory, NullLogger<OmdbApiClient>.Instance);
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
