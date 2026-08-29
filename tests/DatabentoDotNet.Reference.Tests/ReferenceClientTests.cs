using System.Net;
using System.Text;
using DatabentoDotNet.Historical;
using DatabentoDotNet.Historical.Tests;
using Microsoft.Extensions.Logging;

namespace DatabentoDotNet.Reference.Tests;

/// <summary>
/// Tests for <see cref="ReferenceClient"/> — the configuration it carries, the transport it builds
/// from it, and what it owns.
/// </summary>
/// <remarks>
/// <para>
/// These drive a real client at <see cref="MockHistoricalGateway"/> over a real socket, for the
/// reason that harness exists at all: an <c>HttpMessageHandler</c> stub never opens one, and
/// <see cref="HttpClient"/>'s own behaviour — default headers, base-address resolution — is half of
/// what is under test.
/// </para>
/// <para>
/// <b>The credential is asserted by reaching the gateway, not by decoding the header.</b> The
/// harness refuses any request whose <c>Authorization</c> is not HTTP Basic with the API key as the
/// username and an empty password, and <see cref="MockHistoricalGateway.ThrowIfRejected"/> raises
/// that on the test's own thread. What the tests here add is the other half of "and nowhere else":
/// that the key appears in no query string, no form field, and no other header.
/// </para>
/// <para>
/// <b>Requests go through <see cref="ReferenceClient.Transport"/>, because #48 ships no endpoints.</b>
/// That is not a workaround — it is the escape hatch that property exists to be, exercised at the
/// only moment in this milestone when it is also the only route. #53–#56 add the endpoints that
/// will use it from the inside.
/// </para>
/// </remarks>
public class ReferenceClientTests
{
    private const string GetLast = "security_master.get_last";
    private const string ListEvents = "corporate_actions.list_events";

    private const string SecuritiesJson = """[{"isin":"US0378331005"}]""";

    /// <summary>
    /// The event id <c>Internal/HistoricalLog.cs</c> assigns to a server warning. Restated rather
    /// than reached for, exactly as <c>HistoricalClientTests</c> restates it: these assert the
    /// contract, and a renumbering that broke a caller's filter should break a test rather than
    /// follow the code.
    /// </summary>
    private const int ServerWarningEventId = 1;

    private static readonly KeyValuePair<string, string>[] GetLastForm =
    [
        new("index", "ts_effective"),
        // The comma is the point: a multi-value reference parameter renders as `US,GB`, and a comma
        // is a URI sub-delimiter that has to arrive percent-encoded rather than raw.
        new("countries", "US,GB"),
    ];

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Request_ArrivesAtTheVersionedPathForItsSlug()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetLast, MockHistoricalResponse.Json(SecuritiesJson));

        await using var client = ClientFor(gateway);
        using var response = await client.Transport.SendAsync(
            HttpMethod.Post, GetLast, GetLastForm, cancellationToken: Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/v0/" + GetLast, Assert.Single(gateway.Requests).Path);
    }

    [Fact]
    public async Task ApiKey_TravelsInTheAuthorizationHeaderAndNowhereElse()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetLast, MockHistoricalResponse.Json(SecuritiesJson));
        gateway.Get(ListEvents, MockHistoricalResponse.Json("""{"AGM":{}}"""));

        await using var client = ClientFor(gateway);
        using (await client.Transport.SendAsync(HttpMethod.Post, GetLast, GetLastForm, cancellationToken: Cancel))
        {
        }

        using (await client.Transport.SendAsync(HttpMethod.Get, ListEvents, parameters: null, cancellationToken: Cancel))
        {
        }

        // The harness is what checks the credential itself: Basic, the key as the username, an
        // empty password. Reaching here without a rejection is that assertion.
        gateway.ThrowIfRejected();

        foreach (var recorded in gateway.Requests)
        {
            Assert.DoesNotContain("Authorization", recorded.Headers.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(MockHistoricalGateway.TestApiKey, recorded.RawQuery, StringComparison.Ordinal);
            Assert.DoesNotContain(
                MockHistoricalGateway.TestApiKey,
                Encoding.UTF8.GetString(recorded.Body.Span),
                StringComparison.Ordinal);

            foreach (var header in recorded.Headers.Values)
            {
                Assert.DoesNotContain(MockHistoricalGateway.TestApiKey, header, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Gateway_DefaultsToBo1()
    {
        var client = new ReferenceClient { ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey) };

        Assert.Equal(HistoricalGateway.Bo1, client.Gateway);
        Assert.Null(client.BaseUrl);
    }

    [Fact]
    public async Task BaseUrl_OverridesTheGateway()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetLast, MockHistoricalResponse.Json(SecuritiesJson));

        await using var client = ClientFor(gateway);
        using (await client.Transport.SendAsync(HttpMethod.Post, GetLast, GetLastForm, cancellationToken: Cancel))
        {
        }

        gateway.ThrowIfRejected();

        // The proof is the host the request actually carried. Gateway is still its default here —
        // asserted, so this is not a test that would pass if BaseUrl were the only thing consulted
        // — and a request that reached loopback is one that did not reach hist.databento.com.
        Assert.Equal(HistoricalGateway.Bo1, client.Gateway);
        Assert.Equal(gateway.BaseUrl.Authority, Assert.Single(gateway.Requests).Headers["Host"]);
    }

    [Fact]
    public void BaseUrl_RejectsARelativeUri()
    {
        // Caught at configuration time rather than at the first request, where it would surface out
        // of the transport several frames from the property that caused it.
        Assert.Throws<ArgumentException>(() =>
        {
            _ = new ReferenceClient
            {
                ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey),
                BaseUrl = new Uri("v0/", UriKind.Relative),
            };
        });
    }

    [Fact]
    public async Task UserAgent_StartsWithTheLibraryPrefix()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListEvents, MockHistoricalResponse.Json("""{"AGM":{}}"""));

        await using var client = ClientFor(gateway);
        using (await client.Transport.SendAsync(HttpMethod.Get, ListEvents, parameters: null, cancellationToken: Cancel))
        {
        }

        gateway.ThrowIfRejected();
        Assert.StartsWith(
            MockHistoricalGateway.UserAgentPrefix,
            Assert.Single(gateway.Requests).Headers["User-Agent"],
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserAgentExtension_IsAppendedAndThePrefixSurvives()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListEvents, MockHistoricalResponse.Json("""{"AGM":{}}"""));

        await using var client = ClientFor(gateway, userAgentExtension: "MyApp/2.0");
        using (await client.Transport.SendAsync(HttpMethod.Get, ListEvents, parameters: null, cancellationToken: Cancel))
        {
        }

        gateway.ThrowIfRejected();

        var userAgent = Assert.Single(gateway.Requests).Headers["User-Agent"];
        Assert.StartsWith(MockHistoricalGateway.UserAgentPrefix, userAgent, StringComparison.Ordinal);
        Assert.EndsWith(" MyApp/2.0", userAgent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoggerFactory_ReachesTheTransport()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(
            ListEvents,
            MockHistoricalResponse.Json("""{"AGM":{}}""").WithWarnings("a server warning"));

        var logs = new RecordingLoggerFactory();
        await using var client = ClientFor(gateway, logs: logs);
        using (await client.Transport.SendAsync(HttpMethod.Get, ListEvents, parameters: null, cancellationToken: Cancel))
        {
        }

        gateway.ThrowIfRejected();

        // X-Warning has exactly one route to a caller, and this is it. A logger factory that never
        // reached the transport would leave this empty rather than fail anything louder.
        var warning = Assert.Single(logs.EntriesWith(ServerWarningEventId));
        Assert.Equal("a server warning", warning.Message);
        Assert.Equal(LogLevel.Warning, warning.Level);
    }

    [Fact]
    public void Transport_IsBuiltOnceAndReturnedOnEveryRead()
    {
        var client = new ReferenceClient { ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey) };

        // Two threads racing into the first request must get one transport, not two — the reason
        // the field is a Lazy with ExecutionAndPublication rather than a null-coalescing assignment.
        Assert.Same(client.Transport, client.Transport);
    }

    [Fact]
    public void Transport_CarriesTheClientsConfiguration()
    {
        var logs = new RecordingLoggerFactory();
        var baseUrl = new Uri("http://127.0.0.1:1/");

        var client = new ReferenceClient
        {
            ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey),
            BaseUrl = baseUrl,
            UserAgentExtension = "MyApp/2.0",
            LoggerFactory = logs,
        };

        Assert.Equal(MockHistoricalGateway.TestApiKey, client.Transport.ApiKey.Value);
        Assert.Equal(HistoricalGateway.Bo1, client.Transport.Gateway);
        Assert.Equal(baseUrl, client.Transport.BaseUrl);
        Assert.Equal("MyApp/2.0", client.Transport.UserAgentExtension);
        Assert.Same(logs, client.Transport.LoggerFactory);
    }

    [Fact]
    public async Task Disposing_IsIdempotent()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListEvents, MockHistoricalResponse.Json("""{"AGM":{}}"""));

        var client = ClientFor(gateway);
        using (await client.Transport.SendAsync(HttpMethod.Get, ListEvents, parameters: null, cancellationToken: Cancel))
        {
        }

        await client.DisposeAsync();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Disposing_BeforeAnyRequest_DoesNotThrow()
    {
        // Nothing to release: the transport is built on first use, and this client never sent one.
        var client = new ReferenceClient { ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey) };

        await client.DisposeAsync();
    }

    [Fact]
    public async Task Transport_AfterDisposal_Throws()
    {
        var client = new ReferenceClient { ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey) };
        await client.DisposeAsync();

        // Rather than quietly building a second transport, which is what a plain null check would
        // do on a client whose transport was never built.
        Assert.Throws<ObjectDisposedException>(() => client.Transport);
    }

    [Fact]
    public async Task OwnedTransport_IsDisposedWithTheClient()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListEvents, MockHistoricalResponse.Json("""{"AGM":{}}"""));

        var client = ClientFor(gateway);
        var transport = client.Transport;

        await client.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            transport.SendAsync(HttpMethod.Get, ListEvents, parameters: null, cancellationToken: Cancel));
    }

    [Fact]
    public async Task SuppliedTransport_IsTheOneRequestsGoThrough()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListEvents, MockHistoricalResponse.Json("""{"AGM":{}}"""));

        await using var transport = TransportFor(gateway);
        await using var client = new ReferenceClient(transport);

        Assert.Same(transport, client.Transport);

        using (await client.Transport.SendAsync(HttpMethod.Get, ListEvents, parameters: null, cancellationToken: Cancel))
        {
        }

        gateway.ThrowIfRejected();
        Assert.Single(gateway.Requests);
    }

    [Fact]
    public async Task SuppliedTransport_SurvivesTheClientThatBorrowedIt()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListEvents, MockHistoricalResponse.Json("""{"AGM":{}}"""));

        await using var transport = TransportFor(gateway);

        var client = new ReferenceClient(transport);
        await client.DisposeAsync();

        // Ownership did not transfer: whoever created the transport is still using it.
        using (await transport.SendAsync(HttpMethod.Get, ListEvents, parameters: null, cancellationToken: Cancel))
        {
        }

        gateway.ThrowIfRejected();
        Assert.Single(gateway.Requests);
    }

    [Fact]
    public async Task SuppliedTransport_ReportsItsOwnConfiguration()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);

        var logs = new RecordingLoggerFactory();
        await using var transport = TransportFor(gateway, logs: logs, userAgentExtension: "MyApp/2.0");
        await using var client = new ReferenceClient(transport);

        Assert.Equal(MockHistoricalGateway.TestApiKey, client.ApiKey.Value);
        Assert.Equal(HistoricalGateway.Bo1, client.Gateway);
        Assert.Equal(gateway.BaseUrl, client.BaseUrl);
        Assert.Equal("MyApp/2.0", client.UserAgentExtension);
        Assert.Same(logs, client.LoggerFactory);
    }

    [Fact]
    public async Task SuppliedTransport_RefusesAConfigurationOverride()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        await using var transport = TransportFor(gateway);

        // A supplied transport is already built and already carries a credential, so none of these
        // could reach the wire. A property reporting a key no request carries is worse than a throw.
        AssertRefused(nameof(ReferenceClient.ApiKey), () =>
            new ReferenceClient(transport) { ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey) });

        AssertRefused(nameof(ReferenceClient.Gateway), () =>
            new ReferenceClient(transport) { Gateway = HistoricalGateway.Bo1 });

        AssertRefused(nameof(ReferenceClient.BaseUrl), () =>
            new ReferenceClient(transport) { BaseUrl = new Uri("http://127.0.0.1:1/") });

        AssertRefused(nameof(ReferenceClient.UserAgentExtension), () =>
            new ReferenceClient(transport) { UserAgentExtension = "MyApp/2.0" });

        AssertRefused(nameof(ReferenceClient.LoggerFactory), () =>
            new ReferenceClient(transport) { LoggerFactory = new RecordingLoggerFactory() });
    }

    [Fact]
    public void SuppliedTransport_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new ReferenceClient(null!);
        });
    }

    private static void AssertRefused(string property, Func<ReferenceClient> construct)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => construct());

        // The message names the property, because "this cannot be set" on a client with five
        // settable properties sends the reader looking rather than pointing them at the line.
        Assert.Contains(property, exception.Message, StringComparison.Ordinal);
    }

    private static ReferenceClient ClientFor(
        MockHistoricalGateway gateway,
        RecordingLoggerFactory? logs = null,
        string? userAgentExtension = null) => new()
        {
            ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey),
            BaseUrl = gateway.BaseUrl,
            LoggerFactory = logs,
            UserAgentExtension = userAgentExtension,
        };

    private static HistoricalClient TransportFor(
        MockHistoricalGateway gateway,
        RecordingLoggerFactory? logs = null,
        string? userAgentExtension = null) => new()
        {
            ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey),
            BaseUrl = gateway.BaseUrl,
            LoggerFactory = logs,
            UserAgentExtension = userAgentExtension,
        };
}
