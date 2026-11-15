using System.Net;
using ExpenseTracker.Api.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ExpenseTracker.Tests;

public class CurrencyServiceTests
{
    private sealed class Handler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
    private sealed class Clients(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private static CurrencyService Service(Handler handler) => new(new Clients(new HttpClient(handler)),
        new MemoryCache(new MemoryCacheOptions()), NullLogger<CurrencyService>.Instance);

    [Fact]
    public async Task Successful_rates_are_labelled_live_and_cached_across_concurrent_requests()
    {
        var handler = new Handler("{\"rates\":{\"RON\":1,\"EUR\":0.2}}");
        var service = Service(handler);
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => service.GetSnapshotAsync()));
        Assert.Equal(1, handler.Calls);
        Assert.All(results, result => { Assert.Equal("live", result.Source); Assert.NotNull(result.FetchedAt); Assert.Equal(.2m, result.Rates["EUR"]); });
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not-json")]
    [InlineData("{\"rates\":{\"RON\":1,\"EUR\":0}}")]
    [InlineData("{\"rates\":{\"RON\":2,\"EUR\":0.2}}")]
    public async Task Invalid_provider_data_uses_explicitly_labelled_and_cached_fallback(string body)
    {
        var handler = new Handler(body); var service = Service(handler);
        var result = await service.GetSnapshotAsync();
        Assert.Equal("fallback", result.Source); Assert.Null(result.FetchedAt);
        await service.GetSnapshotAsync(); Assert.Equal(1, handler.Calls);
    }
}
