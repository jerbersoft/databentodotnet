using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// The <see cref="HistoricalClient.Handler"/> seam: a caller may supply the
/// <see cref="HttpMessageHandler"/> the client sends through, which is how
/// <c>IHttpClientFactory</c> — and therefore a bounded <c>PooledConnectionLifetime</c> — becomes
/// reachable from a long-running host.
/// </summary>
/// <remarks>
/// <para>
/// <b>The credential assertion is the load-bearing one here.</b> Everything the client does to a
/// request it builds — HTTP Basic from the <see cref="ApiKey"/>, the validated
/// <c>User-Agent</c>, the <c>Accept</c> header, the base address — has to survive a supplied
/// handler, or the seam has quietly become a second path to the wire. That is the property
/// <see cref="HistoricalClient.ApiKey"/>'s remarks promise and the reason a full
/// <see cref="HttpClient"/> seam was rejected.
/// </para>
/// <para>
/// A recording handler rather than <see cref="MockHistoricalGateway"/>, and deliberately: what is
/// under test is which handler the client sends <em>through</em>, which a real socket cannot
/// report. <see cref="HistoricalClientTests"/> keeps the socket-level coverage.
/// </para>
/// </remarks>
public class HistoricalClientHandlerTests
{
    private const string ApiKeyValue = "test-API________________________";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SendAsync_WithASuppliedHandler_SendsThroughIt()
    {
        using var handler = new RecordingHandler();
        await using var client = new HistoricalClient
        {
            ApiKey = new ApiKey(ApiKeyValue),
            Handler = handler,
            DisposesHandler = false,
        };

        using var response = await client.GetPathAsync(
            HistoricalClient.PathFor("metadata.list_datasets"), cancellationToken: Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task SendAsync_WithASuppliedHandler_StillSendsEveryHeaderTheClientOwns()
    {
        using var handler = new RecordingHandler();
        await using var client = new HistoricalClient
        {
            ApiKey = new ApiKey(ApiKeyValue),
            Handler = handler,
            DisposesHandler = false,
            UserAgentExtension = "MyApp/1.0",
        };

        using var response = await client.GetPathAsync(
            HistoricalClient.PathFor("metadata.list_datasets"), cancellationToken: Cancel);

        var request = Assert.Single(handler.Requests);

        // HTTP Basic with the key as the username and an empty password — the one place in this
        // library where the key reaches the wire, asserted here because the seam is exactly where
        // a second place would appear.
        var authorization = request.Headers.Authorization;
        Assert.NotNull(authorization);
        Assert.Equal("Basic", authorization.Scheme);
        Assert.Equal(
            ApiKeyValue + ":",
            Encoding.UTF8.GetString(Convert.FromBase64String(authorization.Parameter!)));

        Assert.Contains("MyApp/1.0", request.Headers.UserAgent.ToString());
        Assert.Contains(
            request.Headers.Accept,
            media => media.MediaType == HistoricalClient.JsonMediaType);

        // The base address still resolved, so the seam did not cost the gateway either.
        Assert.Equal("hist.databento.com", request.RequestUri!.Host);
        Assert.Equal("/v0/metadata.list_datasets", request.RequestUri.AbsolutePath);
    }

    [Fact]
    public async Task DisposeAsync_WithDisposesHandlerFalse_LeavesTheHandlerUsable()
    {
        using var handler = new RecordingHandler();
        var client = new HistoricalClient
        {
            ApiKey = new ApiKey(ApiKeyValue),
            Handler = handler,
            DisposesHandler = false,
        };

        (await client.GetPathAsync(HistoricalClient.PathFor("metadata.list_datasets"), cancellationToken: Cancel))
            .Dispose();
        await client.DisposeAsync();

        Assert.False(handler.Disposed);
    }

    [Fact]
    public async Task DisposeAsync_ByDefault_DisposesTheSuppliedHandler()
    {
        // The default is true because HttpClient's own is, and a caller who supplies a handler and
        // says nothing has handed over its lifetime. IHttpClientFactory's caller says otherwise —
        // which is what the property is for.
        var handler = new RecordingHandler();
        var client = new HistoricalClient
        {
            ApiKey = new ApiKey(ApiKeyValue),
            Handler = handler,
        };

        (await client.GetPathAsync(HistoricalClient.PathFor("metadata.list_datasets"), cancellationToken: Cancel))
            .Dispose();
        await client.DisposeAsync();

        Assert.True(handler.Disposed);
    }

    [Fact]
    public async Task SendAsync_WithNoHandler_StillWorks()
    {
        // The property is additive: a client that sets neither behaves exactly as it did before.
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get("metadata.list_datasets", MockHistoricalResponse.Json("""[{"dataset":"GLBX.MDP3"}]"""));

        await using var client = new HistoricalClient
        {
            ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey),
            BaseUrl = gateway.BaseUrl,
        };

        using var response = await client.GetPathAsync(
            HistoricalClient.PathFor("metadata.list_datasets"), cancellationToken: Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Answers every request with an empty JSON array and keeps what it was asked, so a test can
    /// assert which handler the client sent through and what it put on the request.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<HttpRequestMessage> _requests = [];

        public IReadOnlyList<HttpRequestMessage> Requests => _requests;

        public int Count => _requests.Count;

        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _requests.Add(request);

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, HistoricalClient.JsonMediaType),
                RequestMessage = request,
            };

            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
