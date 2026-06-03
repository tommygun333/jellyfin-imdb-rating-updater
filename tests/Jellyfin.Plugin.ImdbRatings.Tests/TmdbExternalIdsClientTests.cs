using System.Net;
using System.Net.Http;
using System.Text;
using Jellyfin.Plugin.ImdbRatings.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ImdbRatings.Tests;

public class TmdbExternalIdsClientTests
{
    [Fact]
    public async Task FetchImdbIdAsync_ReturnsImdbId_WhenTvShowResponseIsValid()
    {
        var client = CreateClient("""{"id":1234,"imdb_id":"tt1234567","tvdb_id":12345}""");

        var result = await client.FetchImdbIdAsync("1234", isTvShow: true, "fakeKey", CancellationToken.None);

        Assert.Equal("tt1234567", result);
    }

    [Fact]
    public async Task FetchImdbIdAsync_ReturnsImdbId_WhenMovieResponseIsValid()
    {
        var client = CreateClient("""{"id":5678,"imdb_id":"tt7654321"}""");

        var result = await client.FetchImdbIdAsync("5678", isTvShow: false, "fakeKey", CancellationToken.None);

        Assert.Equal("tt7654321", result);
    }

    [Fact]
    public async Task FetchImdbIdAsync_ReturnsNull_WhenImdbIdIsAbsent()
    {
        var client = CreateClient("""{"id":1234,"imdb_id":null}""");

        var result = await client.FetchImdbIdAsync("1234", isTvShow: true, "fakeKey", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchImdbIdAsync_ReturnsNull_WhenResponseIsNotSuccess()
    {
        var client = CreateClient(string.Empty, HttpStatusCode.Unauthorized);

        var result = await client.FetchImdbIdAsync("1234", isTvShow: true, "badKey", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchImdbIdAsync_ReturnsNull_WhenTmdbIdIsEmpty()
    {
        var client = CreateClient("""{"id":0,"imdb_id":"tt9999999"}""");

        var result = await client.FetchImdbIdAsync(string.Empty, isTvShow: true, "fakeKey", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchImdbIdAsync_ReturnsNull_WhenApiKeyIsEmpty()
    {
        var client = CreateClient("""{"id":1234,"imdb_id":"tt1234567"}""");

        var result = await client.FetchImdbIdAsync("1234", isTvShow: true, string.Empty, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchImdbIdAsync_UsesCorrectEndpoint_ForTvShow()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler(req =>
        {
            capturedRequest = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":99,"imdb_id":"tt0000099"}""", Encoding.UTF8, "application/json")
            };
        });

        var factory = new StubHttpClientFactory(new HttpClient(handler));
        var client = new TmdbExternalIdsClient(factory, NullLogger<TmdbExternalIdsClient>.Instance);

        await client.FetchImdbIdAsync("99", isTvShow: true, "key123", CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Contains("/tv/99/", capturedRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task FetchImdbIdAsync_UsesCorrectEndpoint_ForMovie()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler(req =>
        {
            capturedRequest = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":42,"imdb_id":"tt0000042"}""", Encoding.UTF8, "application/json")
            };
        });

        var factory = new StubHttpClientFactory(new HttpClient(handler));
        var client = new TmdbExternalIdsClient(factory, NullLogger<TmdbExternalIdsClient>.Instance);

        await client.FetchImdbIdAsync("42", isTvShow: false, "key123", CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Contains("/movie/42/", capturedRequest!.RequestUri!.AbsolutePath);
    }

    private static TmdbExternalIdsClient CreateClient(string content, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        });

        var httpClientFactory = new StubHttpClientFactory(new HttpClient(handler));
        return new TmdbExternalIdsClient(httpClientFactory, NullLogger<TmdbExternalIdsClient>.Instance);
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
