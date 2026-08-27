using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// Tests for <see cref="HistoricalClient"/>, the HTTP transport every historical endpoint is
/// built on.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these drives a real client at <see cref="MockHistoricalGateway"/> over a real
/// socket. That is the point of the harness: an <c>HttpMessageHandler</c> stub would never open
/// one, and <see cref="HttpClient"/> — its default headers, its base-address resolution, its
/// treatment of a request header that shadows a default — is half of what is under test here.
/// </para>
/// <para>
/// <b>The credential is asserted by reaching the gateway, not by decoding the header.</b> The
/// harness refuses any request whose <c>Authorization</c> is not HTTP Basic with the API key as
/// the username and an empty password, and <see cref="MockHistoricalGateway.ThrowIfRejected"/>
/// raises that on the test's own thread. A test that decoded the header itself would be a second
/// implementation of <c>Refuse</c>, agreeing with the client rather than with the API. What the
/// tests here add on top is the other half of "and nowhere else": that the key appears in no
/// query string, no form field, and no other header.
/// </para>
/// <para>
/// Every awaited call carries <see cref="TestContext"/>'s cancellation token, so a cancelled run
/// stops at once rather than waiting out a socket.
/// </para>
/// </remarks>
public partial class HistoricalClientTests
{
    /// <summary>
    /// The event ids <c>Internal/HistoricalLog.cs</c> assigns, which are documented there as
    /// stable identifiers a caller may filter on. Restating them here rather than reaching for
    /// the internal type is deliberate: these tests assert the contract, and a renumbering that
    /// broke a caller's filter should break a test rather than follow the code.
    /// </summary>
    private const int ServerWarningEventId = 1;
    private const int MalformedWarningHeaderEventId = 2;
    private const int UnparseableErrorBodyEventId = 3;

    private const string ListDatasets = "metadata.list_datasets";
    private const string GetRecordCount = "metadata.get_record_count";

    /// <summary>
    /// A batch file's path, registered as a slug because it is one — everything the API serves is
    /// under <c>/v0/</c>, endpoints and job output alike.
    /// </summary>
    private const string BatchFile = "batch/download/USER/JOB/xnas-itch-20230704.trades.dbn.zst";

    private const string DatasetsJson = """[{"dataset":"GLBX.MDP3"},{"dataset":"XNAS.ITCH"}]""";

    private const string DocsUrl = "https://databento.com/docs/api-reference-historical";

    private static readonly KeyValuePair<string, string>[] SymbolQuery =
    [
        new("dataset", "XNAS.ITCH"),
        // The comma is the whole point: a Symbols list renders as `AAPL,MSFT`, and a comma is a
        // URI sub-delimiter that has to arrive percent-encoded rather than raw.
        new("symbols", "AAPL,MSFT"),
        new("stype_in", "raw_symbol"),
    ];

    private static readonly KeyValuePair<string, string>[] CountForm =
    [
        new("dataset", "XNAS.ITCH"),
        new("schema", "trades"),
        new("symbols", "AAPL,MSFT"),
        new("start", "1688428800000000000"),
        new("end", "1688515200000000000"),
    ];

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void PathFor_IsRelativeAndVersioned_WithNoLeadingSlash()
    {
        Assert.Equal("v0/metadata.list_datasets", HistoricalClient.PathFor(ListDatasets));

        // A slug may carry slashes; nothing about that changes the shape.
        Assert.Equal("v0/" + BatchFile, HistoricalClient.PathFor(BatchFile));

        // No leading slash, because a leading slash would resolve against the authority and throw
        // away any path the base URL carries.
        Assert.False(HistoricalClient.PathFor(ListDatasets).StartsWith('/'));
    }

    [Fact]
    public void BaseUrl_RejectsARelativeUri()
    {
        // Caught at configuration time rather than at the first request, where it would surface
        // as an InvalidOperationException out of Uri.AbsoluteUri with nothing pointing at the
        // property that caused it.
        Assert.Throws<ArgumentException>(() => new HistoricalClient
        {
            ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey),
            BaseUrl = new Uri("v0/", UriKind.Relative),
        });
    }

    [Fact]
    public async Task Request_ArrivesAtTheVersionedPathForItsSlug()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListDatasets, MockHistoricalResponse.Json(DatasetsJson));

        await using var client = ClientFor(gateway);
        using var response = await client.SendAsync(
            HttpMethod.Get, ListDatasets, parameters: null, cancellationToken: Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/v0/" + ListDatasets, Assert.Single(gateway.Requests).Path);
    }

    [Fact]
    public async Task Request_ArrivesAtTheVersionedPath_ForASlugCarryingSlashes()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(BatchFile, MockHistoricalResponse.Json("{}"));

        await using var client = ClientFor(gateway);
        using var response = await client.SendAsync(
            HttpMethod.Get, BatchFile, parameters: null, cancellationToken: Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/v0/" + BatchFile, Assert.Single(gateway.Requests).Path);
    }

    [Fact]
    public async Task ApiKey_TravelsInTheAuthorizationHeaderAndNowhereElse()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListDatasets, MockHistoricalResponse.Json(DatasetsJson));
        gateway.Post(GetRecordCount, MockHistoricalResponse.Json("""{"record_count":42}"""));

        await using var client = ClientFor(gateway);
        using (await client.SendAsync(HttpMethod.Get, ListDatasets, SymbolQuery, cancellationToken: Cancel))
        {
        }

        using (await client.SendAsync(HttpMethod.Post, GetRecordCount, CountForm, cancellationToken: Cancel))
        {
        }

        // The harness is what checks the credential itself: Basic, the key as the username, an
        // empty password. Reaching here without a rejection is that assertion.
        gateway.ThrowIfRejected();
        Assert.Empty(gateway.Rejections);

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
    public async Task UserAgent_StartsWithTheLibraryPrefix()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListDatasets, MockHistoricalResponse.Json(DatasetsJson));

        await using var client = ClientFor(gateway);
        using (await client.SendAsync(HttpMethod.Get, ListDatasets, parameters: null, cancellationToken: Cancel))
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
        const string Extension = "MyApp/2.0";

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListDatasets, MockHistoricalResponse.Json(DatasetsJson));

        await using var client = ClientFor(gateway, userAgentExtension: Extension);
        using (await client.SendAsync(HttpMethod.Get, ListDatasets, parameters: null, cancellationToken: Cancel))
        {
        }

        gateway.ThrowIfRejected();

        var userAgent = Assert.Single(gateway.Requests).Headers["User-Agent"];
        Assert.StartsWith(MockHistoricalGateway.UserAgentPrefix, userAgent, StringComparison.Ordinal);
        Assert.EndsWith(" " + Extension, userAgent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_PutsItsParametersInTheQueryString_AndSendsNoForm()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListDatasets, MockHistoricalResponse.Json(DatasetsJson));

        await using var client = ClientFor(gateway);
        using (await client.SendAsync(HttpMethod.Get, ListDatasets, SymbolQuery, cancellationToken: Cancel))
        {
        }

        gateway.ThrowIfRejected();

        var recorded = Assert.Single(gateway.Requests);
        Assert.Equal("XNAS.ITCH", recorded.Query["dataset"]);
        Assert.Equal("AAPL,MSFT", recorded.Query["symbols"]);
        Assert.Equal("raw_symbol", recorded.Query["stype_in"]);
        Assert.Empty(recorded.Form);
        Assert.Empty(recorded.Body.ToArray());
    }

    [Fact]
    public async Task Get_PercentEncodesACommaInAParameterValue()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListDatasets, MockHistoricalResponse.Json(DatasetsJson));

        await using var client = ClientFor(gateway);
        using (await client.SendAsync(HttpMethod.Get, ListDatasets, SymbolQuery, cancellationToken: Cancel))
        {
        }

        gateway.ThrowIfRejected();

        // RawQuery, not Query: the decoded view percent-decodes and would show the comma either
        // way, so only the raw one can tell an encoded comma from a literal one.
        var rawQuery = Assert.Single(gateway.Requests).RawQuery;
        Assert.Contains("symbols=AAPL%2CMSFT", rawQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("AAPL,MSFT", rawQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_PutsItsParametersInTheForm_AndLeavesTheQueryEmpty()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetRecordCount, MockHistoricalResponse.Json("""{"record_count":42}"""));

        await using var client = ClientFor(gateway);
        using (await client.SendAsync(HttpMethod.Post, GetRecordCount, CountForm, cancellationToken: Cancel))
        {
        }

        gateway.ThrowIfRejected();

        var recorded = Assert.Single(gateway.Requests);
        Assert.Equal("XNAS.ITCH", recorded.Form["dataset"]);
        Assert.Equal("trades", recorded.Form["schema"]);
        Assert.Equal("AAPL,MSFT", recorded.Form["symbols"]);
        Assert.Equal("1688428800000000000", recorded.Form["start"]);
        Assert.Empty(recorded.Query);
        Assert.Equal(string.Empty, recorded.RawQuery);
    }

    [Fact]
    public async Task Post_WithNoParameters_StillSendsAnEmptyForm()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetRecordCount, MockHistoricalResponse.Json("{}"));

        await using var client = ClientFor(gateway);
        using (await client.SendAsync(HttpMethod.Post, GetRecordCount, parameters: null, cancellationToken: Cancel))
        {
        }

        gateway.ThrowIfRejected();

        // An absent body carries no Content-Type, and a server that branches on
        // application/x-www-form-urlencoded — this harness's HasFormContentType guard does —
        // sees a different request. An empty form is not the same thing as no form.
        var recorded = Assert.Single(gateway.Requests);
        Assert.Contains(
            "application/x-www-form-urlencoded",
            recorded.Headers["Content-Type"],
            StringComparison.Ordinal);
        Assert.Empty(recorded.Form);
    }

    [Fact]
    public async Task Accept_DefaultsToApplicationJson()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListDatasets, MockHistoricalResponse.Json(DatasetsJson));

        await using var client = ClientFor(gateway);
        using (await client.SendAsync(HttpMethod.Get, ListDatasets, parameters: null, cancellationToken: Cancel))
        {
        }

        gateway.ThrowIfRejected();
        Assert.Equal(HistoricalClient.JsonMediaType, Assert.Single(gateway.Requests).Headers["Accept"]);
    }

    [Fact]
    public async Task Accept_OverridesTheDefaultForOneRequestOnly()
    {
        const string Binary = "application/octet-stream";

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListDatasets, MockHistoricalResponse.Json(DatasetsJson));

        await using var client = ClientFor(gateway);
        using (await client.SendAsync(HttpMethod.Get, ListDatasets, parameters: null, accept: Binary, cancellationToken: Cancel))
        {
        }

        using (await client.SendAsync(HttpMethod.Get, ListDatasets, parameters: null, cancellationToken: Cancel))
        {
        }

        gateway.ThrowIfRejected();

        // The override replaces the default for its own request rather than adding to it —
        // HttpClient copies a default header across only when the request does not already carry
        // one by that name — and the next request is unaffected, because nothing touched
        // DefaultRequestHeaders, which every request this client sends shares.
        Assert.Equal(2, gateway.Requests.Count);
        Assert.Equal(Binary, gateway.Requests[0].Headers["Accept"]);
        Assert.Equal(HistoricalClient.JsonMediaType, gateway.Requests[1].Headers["Accept"]);
    }

    [Fact]
    public async Task BaseUrlCarryingAPath_KeepsThatPathWhenTheSlugIsResolvedAgainstIt()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListDatasets, MockHistoricalResponse.Json(DatasetsJson));

        // No trailing slash, which is the hazard: new Uri(new Uri("http://host/api"), "v0/x") is
        // http://host/v0/x — the last segment is replaced, not appended to. The client normalises
        // its base address to end in '/' precisely so a proxy mounted at /api keeps its mount.
        var mounted = new Uri(gateway.BaseUrl, "api");
        Assert.False(mounted.AbsoluteUri.EndsWith('/'));

        await using var client = ClientFor(gateway, baseUrl: mounted);

        // The harness serves /v0/…, so /api/v0/… matches no route and comes back 501. That is the
        // assertion working, not failing: the request reached the server carrying the mount point.
        await Assert.ThrowsAsync<DatabentoApiException>(() =>
            client.SendAsync(HttpMethod.Get, ListDatasets, parameters: null, cancellationToken: Cancel));

        Assert.Equal("/api/v0/" + ListDatasets, Assert.Single(gateway.Requests).Path);
    }

    [Fact]
    public async Task BaseUrlCarryingAQuery_StillKeepsItsPath()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListDatasets, MockHistoricalResponse.Json(DatasetsJson));

        // Normalising the whole URI string rather than its path appends the slash *after* the
        // query — "…/api?token=x/" — leaving the path as "/api" and losing the mount point
        // exactly as the no-trailing-slash case does. Normalising AbsolutePath and rebuilding is
        // what keeps the two cases the same case.
        var mounted = new Uri(gateway.BaseUrl, "api?token=x");
        Assert.Equal("/api", mounted.AbsolutePath);

        await using var client = ClientFor(gateway, baseUrl: mounted);

        await Assert.ThrowsAsync<DatabentoApiException>(() =>
            client.SendAsync(HttpMethod.Get, ListDatasets, parameters: null, cancellationToken: Cancel));

        // The path survives. The query does not, and cannot: resolving a relative reference that
        // has a path replaces the base's query outright (RFC 3986 §5.3), so a token parked there
        // reaches no request. The Authorization header is the only credential this client sends.
        var recorded = Assert.Single(gateway.Requests);
        Assert.Equal("/api/v0/" + ListDatasets, recorded.Path);
        Assert.Equal(string.Empty, recorded.RawQuery);
    }

    [Fact]
    public async Task SimpleError_MapsTheDetailStringToTheMessage_AndLeavesTheRestNull()
    {
        const string Detail = "Authorization failed: bad key.";
        const string RequestId = "req-simple";

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListDatasets, MockHistoricalResponse.SimpleError(401, Detail).WithRequestId(RequestId));

        await using var client = ClientFor(gateway);
        var exception = await Assert.ThrowsAsync<DatabentoApiException>(() =>
            client.SendAsync(HttpMethod.Get, ListDatasets, parameters: null, cancellationToken: Cancel));

        gateway.ThrowIfRejected();

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal(RequestId, exception.RequestId);
        Assert.Null(exception.Case);
        Assert.Null(exception.DocsUrl);
        Assert.Null(exception.Payload);

        // The exception composes its Message from the status and the request id as well, the way
        // upstream's Display does, so the server's own text is the tail of it.
        Assert.EndsWith(Detail, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BusinessError_MapsCaseMessageDocsAndPayload()
    {
        const string Case = "data_start_after_available";
        const string Message = "The requested start is after the dataset's available range.";
        const string RequestId = "req-business";
        const string PayloadJson = """{"dataset":"GLBX.MDP3","available_start":"2010-06-06"}""";

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(
            ListDatasets,
            MockHistoricalResponse.BusinessError(422, Case, Message, DocsUrl, PayloadJson)
                .WithRequestId(RequestId));

        await using var client = ClientFor(gateway);
        var exception = await Assert.ThrowsAsync<DatabentoApiException>(() =>
            client.SendAsync(HttpMethod.Get, ListDatasets, parameters: null, cancellationToken: Cancel));

        gateway.ThrowIfRejected();

        Assert.Equal(HttpStatusCode.UnprocessableContent, exception.StatusCode);
        Assert.Equal(RequestId, exception.RequestId);
        Assert.Equal(Case, exception.Case);
        Assert.Equal(DocsUrl, exception.DocsUrl);
        Assert.Contains(Message, exception.Message, StringComparison.Ordinal);

        var payload = Assert.IsAssignableFrom<IReadOnlyDictionary<string, System.Text.Json.JsonElement>>(
            exception.Payload);

        // Readable after the JsonDocument that produced them is long gone, because the exception
        // clones every element it is handed.
        Assert.Equal("GLBX.MDP3", payload["dataset"].GetString());
        Assert.Equal("2010-06-06", payload["available_start"].GetString());
    }

    [Fact]
    public async Task BusinessError_WithoutCaseOrPayload_LeavesBothNull()
    {
        const string Message = "The requested schema is not available for this dataset.";

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(
            ListDatasets,
            MockHistoricalResponse.BusinessError(400, @case: null, Message, DocsUrl));

        await using var client = ClientFor(gateway);
        var exception = await Assert.ThrowsAsync<DatabentoApiException>(() =>
            client.SendAsync(HttpMethod.Get, ListDatasets, parameters: null, cancellationToken: Cancel));

        gateway.ThrowIfRejected();

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Null(exception.RequestId);
        Assert.Null(exception.Case);
        Assert.Null(exception.Payload);
        Assert.Equal(DocsUrl, exception.DocsUrl);
        Assert.Contains(Message, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnparseableErrorBody_BecomesTheMessageVerbatim_AndIsLogged()
    {
        const string Body = "<html><head><title>502 Bad Gateway</title></head><body>nginx</body></html>";

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListDatasets, MockHistoricalResponse.Json(Body, 502).WithRequestId("req-html"));

        var logs = new RecordingLoggerFactory();
        await using var client = ClientFor(gateway, logs);
        var exception = await Assert.ThrowsAsync<DatabentoApiException>(() =>
            client.SendAsync(HttpMethod.Get, ListDatasets, parameters: null, cancellationToken: Cancel));

        gateway.ThrowIfRejected();

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.Equal("req-html", exception.RequestId);
        Assert.Null(exception.Case);
        Assert.Null(exception.DocsUrl);
        Assert.Null(exception.Payload);

        // Verbatim, not swallowed: an error page from a proxy in front of the API is usually the
        // only thing that says the API was never reached.
        Assert.EndsWith(Body, exception.Message, StringComparison.Ordinal);

        Assert.Single(logs.EntriesWith(UnparseableErrorBodyEventId));
    }

    [Fact]
    public async Task MalformedErrorEnvelope_BecomesTheMessageVerbatim_AndIsLogged()
    {
        // Well-formed JSON, and neither documented shape: `detail` is an object, so this is not
        // the simple form, and it has no `message` or `docs`, so it is not the business form
        // either. Upstream's untagged union reports that as a deserialization failure and falls
        // back to the raw body; so does this.
        const string Body = """{"detail":{"case":"something_new"}}""";

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListDatasets, MockHistoricalResponse.Json(Body, 400));

        var logs = new RecordingLoggerFactory();
        await using var client = ClientFor(gateway, logs);
        var exception = await Assert.ThrowsAsync<DatabentoApiException>(() =>
            client.SendAsync(HttpMethod.Get, ListDatasets, parameters: null, cancellationToken: Cancel));

        gateway.ThrowIfRejected();

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Null(exception.Case);
        Assert.Null(exception.DocsUrl);
        Assert.Null(exception.Payload);
        Assert.EndsWith(Body, exception.Message, StringComparison.Ordinal);
        Assert.Single(logs.EntriesWith(UnparseableErrorBodyEventId));
    }

    [Fact]
    public async Task ReadJsonAsync_OnABodyThatIsTheJsonNullLiteral_Throws()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListDatasets, MockHistoricalResponse.Json("null"));

        await using var client = ClientFor(gateway);
        using var response = await client.SendAsync(
            HttpMethod.Get, ListDatasets, parameters: null, cancellationToken: Cancel);

        // A caller asked for a payload and the API said there is none. Upstream's serde rejects
        // null for anything that is not an Option, and a null slipping out as a `default(T)`
        // would be the quiet kind of wrong this library exists to prevent.
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() =>
            HistoricalClient.ReadJsonAsync(response, TestJson.Default.ListDatasetRow, Cancel));

        gateway.ThrowIfRejected();
    }

    [Fact]
    public async Task Warnings_AreLoggedInOrder()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(
            ListDatasets,
            MockHistoricalResponse.Json(DatasetsJson).WithWarnings("first warning", "second warning"));

        var logs = new RecordingLoggerFactory();
        await using var client = ClientFor(gateway, logs);
        using (await client.SendAsync(HttpMethod.Get, ListDatasets, parameters: null, cancellationToken: Cancel))
        {
        }

        gateway.ThrowIfRejected();

        var warnings = logs.EntriesWith(ServerWarningEventId);
        Assert.Equal(2, warnings.Count);
        Assert.Equal("first warning", warnings[0].Message);
        Assert.Equal("second warning", warnings[1].Message);
        Assert.All(warnings, entry => Assert.Equal(LogLevel.Warning, entry.Level));
    }

    [Fact]
    public async Task Warnings_AreLoggedFromEveryOccurrenceOfTheHeader()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(
            ListDatasets,
            MockHistoricalResponse.Json(DatasetsJson)
                .WithHeader(MockHistoricalGateway.WarningHeader, """["from the first header"]""")
                .WithHeader(MockHistoricalGateway.WarningHeader, """["from the second header"]"""));

        var logs = new RecordingLoggerFactory();
        await using var client = ClientFor(gateway, logs);
        using (await client.SendAsync(HttpMethod.Get, ListDatasets, parameters: null, cancellationToken: Cancel))
        {
        }

        gateway.ThrowIfRejected();

        // One response may carry the header more than once, and each occurrence is its own JSON
        // array — so each is parsed on its own rather than the set being treated as one value.
        var warnings = logs.EntriesWith(ServerWarningEventId);
        Assert.Equal(2, warnings.Count);
        Assert.Equal("from the first header", warnings[0].Message);
        Assert.Equal("from the second header", warnings[1].Message);
    }

    [Fact]
    public async Task MalformedWarningHeader_IsLogged_AndTheRequestStillSucceeds()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(
            ListDatasets,
            MockHistoricalResponse.Json(DatasetsJson)
                .WithHeader(MockHistoricalGateway.WarningHeader, "not json"));

        var logs = new RecordingLoggerFactory();
        await using var client = ClientFor(gateway, logs);

        var datasets = await client.SendJsonAsync(
            HttpMethod.Get, ListDatasets, parameters: null, TestJson.Default.ListDatasetRow, Cancel);

        gateway.ThrowIfRejected();

        // A warning that broke the call it was attached to would be worse than no warning.
        Assert.Equal(2, datasets.Count);
        Assert.Equal("GLBX.MDP3", datasets[0].Dataset);

        Assert.Single(logs.EntriesWith(MalformedWarningHeaderEventId));
        Assert.Empty(logs.EntriesWith(ServerWarningEventId));
    }

    [Fact]
    public async Task WarningsOnAFailedRequest_AreLoggedAndTheErrorStillThrows()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(
            ListDatasets,
            MockHistoricalResponse.SimpleError(400, "That range is not available.")
                .WithWarnings("this dataset is being retired"));

        var logs = new RecordingLoggerFactory();
        await using var client = ClientFor(gateway, logs);
        var exception = await Assert.ThrowsAsync<DatabentoApiException>(() =>
            client.SendAsync(HttpMethod.Get, ListDatasets, parameters: null, cancellationToken: Cancel));

        gateway.ThrowIfRejected();

        // Warnings first, then the error check — upstream's order, and the right one: a failing
        // response can still carry an X-Warning, and the warning is often why it failed.
        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal(
            "this dataset is being retired",
            Assert.Single(logs.EntriesWith(ServerWarningEventId)).Message);
    }

    [Fact]
    public async Task ErrorBodyThatFailsMidTransfer_StillReportsTheStatusAndTheRequestId()
    {
        var body = Encoding.UTF8.GetBytes(
            """{"detail":"The dataset is unavailable while its metadata is rebuilt."}""");

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);

        // Never completed. The gateway writes the prefix, waits out its own budget, and only then
        // resets — and that wait is the point: it is what guarantees the response headers are
        // parsed by the client before the connection goes, so this test asserts the read-failure
        // path rather than racing two TCP stacks over whether the headers arrived at all.
        gateway.Timeout = Duration.FromSeconds(1);
        var neverDropped = new TaskCompletionSource();

        gateway.Get(
            ListDatasets,
            MockHistoricalResponse.Dropped(body, 12, neverDropped.Task, statusCode: 500)
                .WithRequestId("req-dropped"));

        await using var client = ClientFor(gateway);
        var exception = await Assert.ThrowsAsync<DatabentoApiException>(() =>
            client.SendAsync(HttpMethod.Get, ListDatasets, parameters: null, cancellationToken: Cancel));

        gateway.ThrowIfRejected();

        // The status and the request id are read before the body, so a transfer that dies partway
        // through the body costs the body text and nothing else. Support asks for the request id
        // first, and an error that lost it because the connection went is an error nobody can
        // chase.
        Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
        Assert.Equal("req-dropped", exception.RequestId);
        Assert.Null(exception.Case);
        Assert.Null(exception.DocsUrl);
        Assert.Null(exception.Payload);
        Assert.Contains("could not be read", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_ReturnsAtTheHeaders_WithTheBodyStillOnTheSocket()
    {
        var body = Encoding.UTF8.GetBytes(new string('x', 4096));

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);

        // Low, so that a regression fails fast instead of waiting out the default ten seconds.
        gateway.Timeout = Duration.FromSeconds(2);

        var release = new TaskCompletionSource();
        gateway.Get(ListDatasets, MockHistoricalResponse.Dropped(body, 16, release.Task));

        await using var client = ClientFor(gateway);
        using var response = await client.SendAsync(
            HttpMethod.Get, ListDatasets, parameters: null, cancellationToken: Cancel);

        // This is a direct assertion on HttpCompletionOption.ResponseHeadersRead rather than on a
        // proxy for it. The gateway has written sixteen bytes of a chunked body and is blocked
        // waiting on `release`, which only this line completes — so SendAsync returning at all,
        // with `release` still pending, means it returned on the headers. Under
        // ResponseContentRead the await above cannot complete until the body does, and the body
        // cannot complete until the gateway gives up: the test would fail rather than pass
        // quietly, which is the whole reason it exists. #38 streams bodies larger than memory and
        // will be written assuming this without re-checking it.
        Assert.False(release.Task.IsCompleted);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        release.SetResult();

        // And the body really was still in flight: releasing the drop resets the connection, so
        // the read that had not happened yet now fails. A body that had already been buffered
        // would come back intact instead.
        var failure = await Record.ExceptionAsync(() => response.Content.ReadAsStringAsync(Cancel));
        Assert.NotNull(failure);
        Assert.True(
            failure is IOException or HttpRequestException,
            $"Expected a transfer failure, got {failure.GetType().Name}.");
    }

    [Fact]
    public async Task ReadJsonAsync_RoundTripsAJsonBody()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListDatasets, MockHistoricalResponse.Json(DatasetsJson));

        await using var client = ClientFor(gateway);

        // SendAsync hands back an undisposed response; disposing it is the caller's job.
        using var response = await client.SendAsync(
            HttpMethod.Get, ListDatasets, parameters: null, cancellationToken: Cancel);

        var datasets = await HistoricalClient.ReadJsonAsync(response, TestJson.Default.ListDatasetRow, Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(["GLBX.MDP3", "XNAS.ITCH"], datasets.Select(row => row.Dataset));
    }

    [Fact]
    public async Task SendJsonAsync_SendsAndReadsInOneCall()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetRecordCount, MockHistoricalResponse.Json(DatasetsJson));

        await using var client = ClientFor(gateway);
        var datasets = await client.SendJsonAsync(
            HttpMethod.Post, GetRecordCount, CountForm, TestJson.Default.ListDatasetRow, Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(["GLBX.MDP3", "XNAS.ITCH"], datasets.Select(row => row.Dataset));
        Assert.Equal("trades", Assert.Single(gateway.Requests).Form["schema"]);
    }

    [Fact]
    public async Task ReadZstdJsonLinesAsync_RoundTripsEveryLine()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(
            ListDatasets,
            MockHistoricalResponse.ZstdJsonLines(
                """{"dataset":"GLBX.MDP3"}""",
                """{"dataset":"XNAS.ITCH"}""",
                """{"dataset":"OPRA.PILLAR"}"""));

        await using var client = ClientFor(gateway);
        using var response = await client.SendAsync(
            HttpMethod.Get, ListDatasets, parameters: null, cancellationToken: Cancel);

        var rows = await HistoricalClient.ReadZstdJsonLinesAsync(response, TestJson.Default.DatasetRow, Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(["GLBX.MDP3", "XNAS.ITCH", "OPRA.PILLAR"], rows.Select(row => row.Dataset));
    }

    [Fact]
    public async Task ReadZstdJsonLinesAsync_ToleratesATrailingNewlineAndABlankLine()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);

        // ZstdJsonLines writes a newline after every element, so the empty element produces a
        // blank line and the frame ends "…}\n\n" — the shape a line-oriented writer leaves behind.
        gateway.Get(
            ListDatasets,
            MockHistoricalResponse.ZstdJsonLines(
                """{"dataset":"GLBX.MDP3"}""",
                """{"dataset":"XNAS.ITCH"}""",
                string.Empty));

        await using var client = ClientFor(gateway);
        using var response = await client.SendAsync(
            HttpMethod.Get, ListDatasets, parameters: null, cancellationToken: Cancel);

        var rows = await HistoricalClient.ReadZstdJsonLinesAsync(response, TestJson.Default.DatasetRow, Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(["GLBX.MDP3", "XNAS.ITCH"], rows.Select(row => row.Dataset));
    }

    [Fact]
    public async Task SendZstdJsonLinesAsync_SendsAndReadsInOneCall()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListDatasets, MockHistoricalResponse.ZstdJsonLines("""{"dataset":"GLBX.MDP3"}"""));

        await using var client = ClientFor(gateway);
        var rows = await client.SendZstdJsonLinesAsync(
            HttpMethod.Get, ListDatasets, parameters: null, TestJson.Default.DatasetRow, Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal("GLBX.MDP3", Assert.Single(rows).Dataset);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListDatasets, MockHistoricalResponse.Json(DatasetsJson));

        var client = ClientFor(gateway);
        using (await client.SendAsync(HttpMethod.Get, ListDatasets, parameters: null, cancellationToken: Cancel))
        {
        }

        await client.DisposeAsync();
        await client.DisposeAsync();

        gateway.ThrowIfRejected();
    }

    [Fact]
    public async Task DisposeAsync_OnAClientThatNeverSentARequest_DoesNotThrow()
    {
        // Nothing to release: the HttpClient is built on first use, and there has not been one.
        var client = new HistoricalClient { ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey) };

        await client.DisposeAsync();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task SendAsync_AfterDisposal_Throws()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListDatasets, MockHistoricalResponse.Json(DatasetsJson));

        var client = ClientFor(gateway);
        await client.DisposeAsync();

        // Rather than quietly building a second HttpClient, which is what a Lazy that was never
        // forced would otherwise do.
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            client.SendAsync(HttpMethod.Get, ListDatasets, parameters: null, cancellationToken: Cancel));

        Assert.Empty(gateway.Requests);
    }

    /// <summary>One row of a JSON body, for the reader tests.</summary>
    /// <remarks>
    /// Deliberately minimal, and deliberately not one of the endpoint response types — those
    /// arrive with the endpoints in #36–#39, and a reader test that waited for one would be
    /// testing the wrong thing. What it has to be is a type with a source-generated
    /// <c>JsonTypeInfo</c>, because that is the only shape either reader accepts.
    /// </remarks>
    private sealed class DatasetRow
    {
        /// <summary>The dataset's code — <c>GLBX.MDP3</c>.</summary>
        public string? Dataset { get; set; }
    }

    /// <summary>
    /// The source-generated serialization context these tests read through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Declared in the test project rather than the library, which is exactly how every
    /// endpoint issue will do it: <c>HistoricalClient</c>'s readers take a
    /// <c>JsonTypeInfo&lt;T&gt;</c> because the shipping assembly is trim- and AOT-analysed
    /// with warnings as errors, so the reflection-based <c>JsonSerializer</c> overloads do not
    /// compile there at all. This context is the test-side proof that the signature is usable.
    /// </para>
    /// <para>
    /// <b>Nested and private, rather than at namespace scope.</b> <c>DatasetRow</c> and
    /// <c>TestJson</c> are names any other file in this project might reasonably want, and a
    /// fixture belonging to one file has no business claiming either of them assembly-wide.
    /// Nesting is what lets the next file declare its own — which it should, rather than
    /// adding a <c>[JsonSerializable]</c> here and coupling two files that share nothing else.
    /// The cost is that every enclosing type has to be <see langword="partial"/>, because that
    /// is what the source generator emits into.
    /// </para>
    /// <para>
    /// The camel-case naming policy is what maps <c>Dataset</c> to the wire's <c>dataset</c>.
    /// The source generator's default matches property names exactly, so without it every
    /// value would come back null and the assertions would fail for a reason that has nothing
    /// to do with the client.
    /// </para>
    /// </remarks>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(DatasetRow))]
    [JsonSerializable(typeof(List<DatasetRow>))]
    private sealed partial class TestJson : JsonSerializerContext
    {
    }

    private static HistoricalClient ClientFor(
        MockHistoricalGateway gateway,
        RecordingLoggerFactory? logs = null,
        string? userAgentExtension = null,
        Uri? baseUrl = null) => new()
        {
            ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey),
            BaseUrl = baseUrl ?? gateway.BaseUrl,
            LoggerFactory = logs,
            UserAgentExtension = userAgentExtension,
        };
}

