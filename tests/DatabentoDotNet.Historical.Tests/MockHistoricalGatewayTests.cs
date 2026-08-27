using System.Net;
using System.Text;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// Tests for <see cref="MockHistoricalGateway"/> — the harness the rest of M3 is tested against.
/// </summary>
/// <remarks>
/// <para>
/// A test double that silently accepts a wrong client is worse than no double at all: every issue
/// downstream of <see href="https://github.com/jerbersoft/databentodotnet/issues/34">#34</see>
/// would go green against it. So one half of what follows drives the gateway with a request the API
/// would refuse — no <c>Authorization</c> header, a bearer token, the wrong username, a non-empty
/// password, the API key in the query string, a foreign <c>User-Agent</c> — and asserts that the
/// gateway <em>refuses</em> it, on the wire and again on the test's own thread.
/// </para>
/// <para>
/// The other half drives every response shape the harness can produce against
/// <see cref="StubHistoricalClient"/>, a client written from the API's documented HTTP behaviour
/// rather than from this gateway, so the two agree only if both match the API. Neither of them has
/// seen the library's historical client, which does not exist yet — that is the whole point of the
/// sequencing.
/// </para>
/// <para>
/// Every awaited call carries <see cref="TestContext"/>'s cancellation token, so a cancelled run
/// stops at once rather than waiting out a socket.
/// </para>
/// </remarks>
public class MockHistoricalGatewayTests
{
    private const string ListDatasets = "metadata.list_datasets";
    private const string GetRange = "timeseries.get_range";

    /// <summary>
    /// A batch file's path. Registered as a slug because it is one — everything the API serves is
    /// under <c>/v0/</c>, endpoints and job output alike, so route registration needs no second
    /// shape for it.
    /// </summary>
    private const string BatchFile = "batch/download/USER/JOB/xnas-itch-20230704.trades.dbn.zst";

    private const string DatasetsJson = """[{"dataset":"GLBX.MDP3"},{"dataset":"XNAS.ITCH"}]""";

    private static readonly KeyValuePair<string, string>[] GetRangeForm =
    [
        new("dataset", "XNAS.ITCH"),
        new("schema", "trades"),
        new("encoding", "dbn"),
        new("compression", "zstd"),
        new("stype_in", "raw_symbol"),
        new("stype_out", "instrument_id"),
        new("symbols", "AAPL,MSFT"),
        new("start", "2023-07-04T00:00:00Z"),
        new("end", "2023-07-05T00:00:00Z"),
    ];

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void TestApiKey_IsThirtyTwoCharacters_AndTravelsAsABasicUsernameWithAnEmptyPassword()
    {
        Assert.Equal(32, MockHistoricalGateway.TestApiKey.Length);

        // The API's "empty password" is a real, present, zero-length password — the credential ends
        // in a colon with nothing after it. A client that omitted the colon would be sending a
        // different credential, which is what the gateway's guard is checking for.
        var header = StubHistoricalClient.BasicHeader(MockHistoricalGateway.TestApiKey, string.Empty);
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..]));

        Assert.Equal(MockHistoricalGateway.TestApiKey + ":", decoded);
    }

    [Fact]
    public async Task BaseUrl_IsAnEphemeralLoopbackPort()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);

        Assert.Equal(Uri.UriSchemeHttp, gateway.BaseUrl.Scheme);
        Assert.Equal("127.0.0.1", gateway.BaseUrl.Host);
        Assert.NotEqual(0, gateway.BaseUrl.Port);

        // The trailing slash is load-bearing: without it, resolving "v0/…" against the base URL
        // replaces the last segment instead of appending to it.
        Assert.EndsWith("/", gateway.BaseUrl.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_Json_IsAnsweredAndRecordedWithItsMethodPathQueryAndHeaders()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListDatasets, MockHistoricalResponse.Json(DatasetsJson));

        using var client = new StubHistoricalClient(gateway.BaseUrl);
        KeyValuePair<string, string>[] query =
            [new("start_date", "2023-07-04"), new("end_date", "2023-07-05")];
        using var response = await client.GetAsync(ListDatasets, query, cancellationToken: Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            MockHistoricalResponse.JsonContentType,
            response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(DatasetsJson, await response.Content.ReadAsStringAsync(Cancel));

        var request = Assert.Single(gateway.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/v0/metadata.list_datasets", request.Path);
        Assert.Equal("2023-07-04", request.Query["start_date"]);
        Assert.Equal("2023-07-05", request.Query["end_date"]);
        Assert.Empty(request.Form);
        Assert.Empty(request.Body.ToArray());
        Assert.Equal(StubHistoricalClient.JsonAccept, request.Headers["Accept"]);
        Assert.Equal(StubHistoricalClient.UserAgent, request.Headers["User-Agent"]);

        // The one header a test cannot see, because it is the one carrying the API key.
        Assert.False(request.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task Post_Form_RecordsEveryFieldOfTheBody()
    {
        var body = SyntheticDbnFragment.Records(4);

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetRange, MockHistoricalResponse.Binary(body));

        using var client = new StubHistoricalClient(gateway.BaseUrl);
        using var response = await client.PostFormAsync(
            GetRange, GetRangeForm, StubHistoricalClient.BinaryAccept, Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body, await response.Content.ReadAsByteArrayAsync(Cancel));

        var request = Assert.Single(gateway.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/v0/timeseries.get_range", request.Path);
        Assert.Empty(request.Query);
        Assert.Equal(StubHistoricalClient.BinaryAccept, request.Headers["Accept"]);

        foreach (var field in GetRangeForm)
        {
            Assert.Equal(field.Value, request.Form[field.Key]);
        }

        // The raw body is kept as well as the decoded fields, so a test about *encoding* — a symbol
        // list with a comma in it, say — has something to assert against.
        Assert.Contains("stype_in=raw_symbol", Encoding.UTF8.GetString(request.Body.Span), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ZstdJsonLines_ArriveAsAFrameTheClientUnwrapsItself()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(
            "batch.list_jobs",
            MockHistoricalResponse.ZstdJsonLines(
                """{"id":"GLBX-20230704-ABCDEF","state":"done"}""",
                """{"id":"XNAS-20230704-123456","state":"processing"}"""));

        using var client = new StubHistoricalClient(gateway.BaseUrl);
        using var response = await client.GetAsync("batch.list_jobs", cancellationToken: Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // No Content-Encoding: the frame is in the body, so nothing in HttpClient decompressed it
        // and the two lines below came out of the client's own decoder.
        Assert.Empty(response.Content.Headers.ContentEncoding);

        var lines = await StubHistoricalClient.ReadZstdJsonLinesAsync(response, Cancel);
        Assert.Equal(
            [
                """{"id":"GLBX-20230704-ABCDEF","state":"done"}""",
                """{"id":"XNAS-20230704-123456","state":"processing"}""",
            ],
            lines);
    }

    [Fact]
    public async Task Binary_IsServedChunked_AndArrivesByteForByte()
    {
        var body = SyntheticDbnFragment.Records(8);

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetRange, MockHistoricalResponse.Binary(body));

        using var client = new StubHistoricalClient(gateway.BaseUrl);
        using var response = await client.PostFormAsync(
            GetRange, GetRangeForm, StubHistoricalClient.BinaryAccept, Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(
            MockHistoricalResponse.BinaryContentType,
            response.Content.Headers.ContentType?.MediaType);

        // Chunked, not merely large: no Content-Length is the property that makes back-pressure and
        // a mid-body drop expressible at all, and it is the one an HttpMessageHandler stub cannot
        // have.
        Assert.Null(response.Content.Headers.ContentLength);
        Assert.True(response.Headers.TransferEncodingChunked);

        Assert.Equal(body, await response.Content.ReadAsByteArrayAsync(Cancel));
    }

    [Fact]
    public async Task SimpleError_CarriesTheDetailStringAndARequestId()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(
            ListDatasets,
            MockHistoricalResponse
                .SimpleError(422, "Unprocessable Entity: 'start_date' must precede 'end_date'.")
                .WithRequestId("6a3f0f6e-4b3d-4a1e-9c0f-2b8a1d5e7c90"));

        using var client = new StubHistoricalClient(gateway.BaseUrl);
        using var response = await client.GetAsync(ListDatasets, cancellationToken: Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        Assert.Equal(
            """{"detail":"Unprocessable Entity: 'start_date' must precede 'end_date'."}""",
            await response.Content.ReadAsStringAsync(Cancel));

        // The header support asks for first, which is why every error this library raises has to
        // carry it.
        Assert.Equal(
            "6a3f0f6e-4b3d-4a1e-9c0f-2b8a1d5e7c90",
            Assert.Single(response.Headers.GetValues(MockHistoricalGateway.RequestIdHeader)));
    }

    [Fact]
    public async Task BusinessError_CarriesTheDetailObjectWithItsCaseDocsAndPayload()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(
            GetRange,
            MockHistoricalResponse
                .BusinessError(
                    422,
                    "data_start_date_error",
                    "The requested start date precedes the dataset's availability.",
                    "https://databento.com/docs/api-reference-historical",
                    """{"available_start_date":"2018-05-01"}""")
                .WithRequestId("b1c2d3e4-0000-4000-8000-abcdefabcdef"));

        using var client = new StubHistoricalClient(gateway.BaseUrl);
        using var response = await client.PostFormAsync(
            GetRange, GetRangeForm, StubHistoricalClient.BinaryAccept, Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);

        // `detail` is a string in the simple shape and an object in this one. Nothing but the JSON
        // distinguishes them, which is why upstream models the pair as an untagged union — and why
        // the harness has to be able to send both.
        Assert.Equal(
            """{"detail":{"case":"data_start_date_error","message":"The requested start date precedes the dataset's availability.","docs":"https://databento.com/docs/api-reference-historical","payload":{"available_start_date":"2018-05-01"}}}""",
            await response.Content.ReadAsStringAsync(Cancel));
        Assert.Equal(
            "b1c2d3e4-0000-4000-8000-abcdefabcdef",
            Assert.Single(response.Headers.GetValues(MockHistoricalGateway.RequestIdHeader)));
    }

    [Fact]
    public async Task Warnings_ArriveAsAJsonArrayInTheXWarningHeader()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(
            ListDatasets,
            MockHistoricalResponse
                .Json(DatasetsJson)
                .WithWarnings("This dataset is deprecated.", "Rate limit at 80%."));

        using var client = new StubHistoricalClient(gateway.BaseUrl);
        using var response = await client.GetAsync(ListDatasets, cancellationToken: Cancel);

        gateway.ThrowIfRejected();

        // A JSON array inside a header value, which is unusual enough to be worth pinning: a client
        // that treated it as one opaque string would show a user the brackets and the quotes.
        Assert.Equal(
            """["This dataset is deprecated.","Rate limit at 80%."]""",
            Assert.Single(response.Headers.GetValues(MockHistoricalGateway.WarningHeader)));
    }

    [Fact]
    public async Task Truncated_CompletesCleanlyWithABodyThatEndsMidRecord()
    {
        var body = SyntheticDbnFragment.Records(4);
        const int Cut = (2 * SyntheticDbnFragment.RecordSize) + 12;

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetRange, MockHistoricalResponse.Truncated(body, Cut));

        using var client = new StubHistoricalClient(gateway.BaseUrl);
        using var response = await client.PostFormAsync(
            GetRange, GetRangeForm, StubHistoricalClient.BinaryAccept, Cancel);

        gateway.ThrowIfRejected();

        // Nothing here is wrong at the HTTP layer. The transfer succeeded, the declared length
        // arrived in full, and only a decoder can tell that the last record is not all there — which
        // is exactly the failure a client that trusts a 200 will get wrong.
        await using var stream = await response.Content.ReadAsStreamAsync(Cancel);
        var (received, failure) = await StubHistoricalClient.ReadUntilEndAsync(stream, Cancel);

        Assert.Null(failure);
        Assert.Equal(Cut, response.Content.Headers.ContentLength);
        Assert.Equal(body[..Cut], received);

        var lastRecord = received.AsSpan(2 * SyntheticDbnFragment.RecordSize);
        Assert.Equal(SyntheticDbnFragment.RecordSize, SyntheticDbnFragment.DeclaredLength(lastRecord));
        Assert.True(
            SyntheticDbnFragment.DeclaredLength(lastRecord) > lastRecord.Length,
            "The body was supposed to stop inside a record, not on a boundary.");
    }

    [Fact]
    public async Task Dropped_DeliversThePrefixAndThenFailsMidBody()
    {
        var body = SyntheticDbnFragment.Records(8);
        const int Prefix = (3 * SyntheticDbnFragment.RecordSize) + 8;

        var drop = new TaskCompletionSource();

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetRange, MockHistoricalResponse.Dropped(body, Prefix, drop.Task));

        using var client = new StubHistoricalClient(gateway.BaseUrl);
        using var response = await client.PostFormAsync(
            GetRange, GetRangeForm, StubHistoricalClient.BinaryAccept, Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Read the prefix before releasing the drop. Having it in hand is what turns "the transfer
        // failed" into "the transfer failed *mid-body*", and it takes the two TCP stacks' timing
        // out of the assertion.
        await using var stream = await response.Content.ReadAsStreamAsync(Cancel);
        var received = new byte[Prefix];
        await stream.ReadExactlyAsync(received, Cancel);
        Assert.Equal(body[..Prefix], received);

        drop.SetResult();

        // Null would mean the stream ended cleanly — a body that stopped, not a connection that
        // went. Only the second is worth a retry, and only the second is what this response is for.
        var (rest, failure) = await StubHistoricalClient.ReadUntilEndAsync(stream, Cancel);
        Assert.Empty(rest);
        Assert.NotNull(failure);
    }

    [Fact]
    public async Task Range_IsAnsweredWithTwoHundredAndSixAndExactlyTheRequestedTail()
    {
        var body = SyntheticDbnFragment.Records(8);
        var half = body.Length / 2;

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(BatchFile, MockHistoricalResponse.Binary(body));

        using var client = new StubHistoricalClient(gateway.BaseUrl);
        using var response = await client.GetRangeAsync(BatchFile, half, Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);

        // Exactly the second half of a known body, compared byte for byte — not a 206 with a
        // plausible length. This is what "batch download resumes across a process restart" rests on.
        Assert.Equal(body[half..], await response.Content.ReadAsByteArrayAsync(Cancel));

        var contentRange = response.Content.Headers.ContentRange;
        Assert.NotNull(contentRange);
        Assert.Equal("bytes", contentRange.Unit);
        Assert.Equal(half, contentRange.From);
        Assert.Equal(body.Length - 1, contentRange.To);
        Assert.Equal(body.Length, contentRange.Length);
        Assert.Equal(body.Length - half, response.Content.Headers.ContentLength);

        var request = Assert.Single(gateway.Requests);
        Assert.Equal("/v0/" + BatchFile, request.Path);
        Assert.Equal($"bytes={half}-", request.Headers["Range"]);
    }

    [Fact]
    public async Task Range_StartingAtOrPastTheEndOfTheBody_IsAnsweredFourSixteen()
    {
        var body = SyntheticDbnFragment.Records(8);

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(BatchFile, MockHistoricalResponse.Binary(body));

        using var client = new StubHistoricalClient(gateway.BaseUrl);

        // The equal case is the one that matters. `bytes={length}-` is exactly what a resumed
        // download asks for when the local file is already complete, and #39's definition of done
        // has to tell it apart from "shorter, so resume". Handing back the whole body would let a
        // client that miscomputed its offset append a second copy and still go green.
        using var atTheEnd = await client.GetWithRawRangeAsync(
            BatchFile, $"bytes={body.Length}-", Cancel);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, atTheEnd.StatusCode);

        // The unsatisfied-range form of Content-Range: no first or last byte, only the length the
        // client got wrong — which is the one piece of information it needs to recompute an offset.
        var contentRange = atTheEnd.Content.Headers.ContentRange;
        Assert.NotNull(contentRange);
        Assert.False(contentRange.HasRange);
        Assert.Null(contentRange.From);
        Assert.Null(contentRange.To);
        Assert.Equal(body.Length, contentRange.Length);
        Assert.Equal($"bytes */{body.Length}", contentRange.ToString());

        using var pastTheEnd = await client.GetWithRawRangeAsync(BatchFile, "bytes=999-", Cancel);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, pastTheEnd.StatusCode);

        // A 416 is an answer, not a refusal: the harness understood the request and said no. A test
        // driving a client's 416 handling should not have to defuse ThrowIfRejected to do it.
        gateway.ThrowIfRejected();
        Assert.Empty(gateway.Rejections);
    }

    [Theory]
    [InlineData("bytes=0-99")]      // closed, which no resumed download sends
    [InlineData("bytes=-32")]       // a suffix range
    [InlineData("bytes=abc-")]      // not a count
    [InlineData("bytes= 8-")]       // padded; NumberStyles.None admits no whitespace
    [InlineData("items=0-")]        // not the bytes unit
    public async Task Range_InAFormTheApiNeverSends_IsIgnoredAndTheWholeBodyGoesOut(string range)
    {
        var body = SyntheticDbnFragment.Records(8);

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(BatchFile, MockHistoricalResponse.Binary(body));

        using var client = new StubHistoricalClient(gateway.BaseUrl);
        using var response = await client.GetWithRawRangeAsync(BatchFile, range, Cancel);

        // Serving the body in full is the honest answer for a double: a test expecting a 206 fails
        // on the status immediately, where a 416 here would be an error path nothing drives.
        gateway.ThrowIfRejected();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body, await response.Content.ReadAsByteArrayAsync(Cancel));
    }

    [Fact]
    public async Task Range_IsIgnoredByAResponseThatDoesNotServeOne()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListDatasets, MockHistoricalResponse.Json(DatasetsJson));

        using var client = new StubHistoricalClient(gateway.BaseUrl);
        using var response = await client.GetRangeAsync(ListDatasets, 4, Cancel);

        // A JSON endpoint has no ranges. Serving the whole body rather than inventing a 206 keeps
        // the harness from being able to answer a range it was never told to hold.
        gateway.ThrowIfRejected();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(DatasetsJson, await response.Content.ReadAsStringAsync(Cancel));
    }

    [Fact]
    public async Task Authorization_Missing_IsRejected()
    {
        await using var gateway = await StartedWithOneRoute();
        using var client = new StubHistoricalClient(gateway.BaseUrl);

        using var response = await client.GetWithAuthorizationAsync(ListDatasets, null, Cancel);

        AssertRefused(gateway, response, "no Authorization header");
    }

    [Fact]
    public async Task Authorization_NotBasic_IsRejected()
    {
        await using var gateway = await StartedWithOneRoute();
        using var client = new StubHistoricalClient(gateway.BaseUrl);

        using var response = await client.GetWithAuthorizationAsync(
            ListDatasets, "Bearer " + MockHistoricalGateway.TestApiKey, Cancel);

        AssertRefused(gateway, response, "not HTTP Basic");
    }

    [Fact]
    public async Task Authorization_WrongUsername_IsRejected()
    {
        await using var gateway = await StartedWithOneRoute();
        using var client = new StubHistoricalClient(gateway.BaseUrl);

        using var response = await client.GetWithAuthorizationAsync(
            ListDatasets,
            StubHistoricalClient.BasicHeader("not-the-key____________________", string.Empty),
            Cancel);

        AssertRefused(gateway, response, "username is not the expected API key");
    }

    [Fact]
    public async Task Authorization_NonEmptyPassword_IsRejected()
    {
        await using var gateway = await StartedWithOneRoute();
        using var client = new StubHistoricalClient(gateway.BaseUrl);

        // The username is right and the key is right. The only thing wrong is that a password was
        // sent at all — which a client using an HTTP library's default basic-auth helper would do
        // without noticing.
        using var response = await client.GetWithAuthorizationAsync(
            ListDatasets,
            StubHistoricalClient.BasicHeader(MockHistoricalGateway.TestApiKey, "hunter2"),
            Cancel);

        AssertRefused(gateway, response, "password is not empty");
    }

    [Fact]
    public async Task ApiKeyInTheQueryString_IsRejected()
    {
        await using var gateway = await StartedWithOneRoute();
        using var client = new StubHistoricalClient(gateway.BaseUrl);

        // The Authorization header is correct here. The request is refused because the key is *also*
        // somewhere it must never be — a URL is logged, cached and pasted into bug reports.
        using var response = await client.GetWithApiKeyInQueryAsync(ListDatasets, cancellationToken: Cancel);

        AssertRefused(gateway, response, "'key' parameter");
    }

    [Fact]
    public async Task ApiKeyAsAnUnfamiliarQueryParametersValue_IsRejected()
    {
        await using var gateway = await StartedWithOneRoute();
        using var client = new StubHistoricalClient(gateway.BaseUrl);

        using var response = await client.GetWithApiKeyInQueryAsync(
            ListDatasets, "credential", Cancel);

        AssertRefused(gateway, response, "value of a query parameter");
    }

    [Fact]
    public async Task UserAgent_WithoutTheLibrarysPrefix_IsRejected()
    {
        await using var gateway = await StartedWithOneRoute();
        using var client = new StubHistoricalClient(gateway.BaseUrl);

        using var response = await client.GetWithUserAgentAsync(
            ListDatasets, "curl/8.7.1", Cancel);

        AssertRefused(gateway, response, MockHistoricalGateway.UserAgentPrefix);
    }

    [Fact]
    public async Task NoRefusalMessageEverContainsTheApiKey()
    {
        await using var gateway = await StartedWithOneRoute();
        using var client = new StubHistoricalClient(gateway.BaseUrl);

        // Every way there is to be refused, in one place, so the rule holds over all of them rather
        // than over the ones someone remembered to check.
        using var noHeader = await client.GetWithAuthorizationAsync(ListDatasets, null, Cancel);
        using var notBasic = await client.GetWithAuthorizationAsync(ListDatasets, "Bearer x", Cancel);
        using var wrongUser = await client.GetWithAuthorizationAsync(
            ListDatasets, StubHistoricalClient.BasicHeader("someone-else", string.Empty), Cancel);
        using var withPassword = await client.GetWithAuthorizationAsync(
            ListDatasets, StubHistoricalClient.BasicHeader(MockHistoricalGateway.TestApiKey, "p"), Cancel);
        using var keyNamed = await client.GetWithApiKeyInQueryAsync(ListDatasets, cancellationToken: Cancel);
        using var keyValued = await client.GetWithApiKeyInQueryAsync(ListDatasets, "credential", Cancel);
        using var foreignAgent = await client.GetWithUserAgentAsync(ListDatasets, "curl/8.7.1", Cancel);
        using var unrouted = await client.GetAsync("metadata.list_publishers", cancellationToken: Cancel);

        Assert.Equal(8, gateway.Rejections.Count);
        foreach (var rejection in gateway.Rejections)
        {
            Assert.DoesNotContain(MockHistoricalGateway.TestApiKey, rejection, StringComparison.Ordinal);
        }

        var thrown = Assert.Throws<MockHistoricalGatewayException>(gateway.ThrowIfRejected);
        Assert.DoesNotContain(MockHistoricalGateway.TestApiKey, thrown.Message, StringComparison.Ordinal);

        // And the bodies that went back over the wire, which are the same strings.
        foreach (var response in new[] { noHeader, notBasic, wrongUser, withPassword, keyNamed, keyValued, foreignAgent, unrouted })
        {
            Assert.DoesNotContain(
                MockHistoricalGateway.TestApiKey,
                await response.Content.ReadAsStringAsync(Cancel),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ExpectedCredentials_AreTheOnesTheGatewayWasStartedWith()
    {
        const string OtherKey = "db-0000000000000000000000000000";

        await using var gateway = await MockHistoricalGateway.StartAsync(
            OtherKey, "SomeOtherClient/", Cancel);
        gateway.Get(ListDatasets, MockHistoricalResponse.Json(DatasetsJson));

        // A gateway that had hard-coded TestApiKey and the library's own prefix would wave both of
        // these through, and every downstream test would then be checking nothing.
        using var defaultClient = new StubHistoricalClient(gateway.BaseUrl);
        using var refused = await defaultClient.GetAsync(ListDatasets, cancellationToken: Cancel);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        using var rightKeyWrongAgent = new StubHistoricalClient(gateway.BaseUrl, OtherKey);
        using var alsoRefused = await rightKeyWrongAgent.GetAsync(ListDatasets, cancellationToken: Cancel);
        Assert.Equal(HttpStatusCode.Unauthorized, alsoRefused.StatusCode);

        using var accepted = await rightKeyWrongAgent.GetWithUserAgentAsync(
            ListDatasets, "SomeOtherClient/1.0", Cancel);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(2, gateway.Rejections.Count);
    }

    [Fact]
    public async Task UnregisteredRoute_IsRefusedWithAStatusTheApiNeverReturns()
    {
        await using var gateway = await StartedWithOneRoute();
        using var client = new StubHistoricalClient(gateway.BaseUrl);

        using var response = await client.GetAsync("metadata.list_publishers", cancellationToken: Cancel);

        // 501 rather than 404 on purpose: the API returns 404, so a test that misspelled a slug when
        // registering it would otherwise pass an assertion about error handling for the wrong reason.
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Contains(
            "GET /v0/metadata.list_publishers",
            Assert.Throws<MockHistoricalGatewayException>(gateway.ThrowIfRejected).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisteringTheSameRouteTwice_Throws()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListDatasets, MockHistoricalResponse.Json(DatasetsJson));

        // Silently replacing the first would make a test that registered two answers for one slug
        // pass while exercising only one of them.
        Assert.Throws<InvalidOperationException>(
            () => gateway.Get(ListDatasets, MockHistoricalResponse.Json("[]")));
    }

    [Fact]
    public async Task ThrowIfRejected_IsSilentWhenEveryRequestWasAccepted()
    {
        await using var gateway = await StartedWithOneRoute();
        using var client = new StubHistoricalClient(gateway.BaseUrl);

        using var response = await client.GetAsync(ListDatasets, cancellationToken: Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(gateway.Rejections);
        gateway.ThrowIfRejected();
    }

    private static async Task<MockHistoricalGateway> StartedWithOneRoute()
    {
        var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListDatasets, MockHistoricalResponse.Json(DatasetsJson));
        return gateway;
    }

    /// <summary>
    /// Asserts that the gateway refused a request, on the wire and on this thread, and that it did
    /// not answer with the route's own body.
    /// </summary>
    /// <remarks>
    /// Both halves matter. The status says the client was turned away; the exception says the
    /// harness knows why and will say so from the test's own stack rather than from a request
    /// thread. Delete either guard in <c>MockHistoricalGateway</c> and both of these fail — the
    /// request would fall through to its registered route and come back <c>200</c> with the
    /// datasets JSON.
    /// </remarks>
    private static void AssertRefused(
        MockHistoricalGateway gateway,
        HttpResponseMessage response,
        string expectedFragment)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var thrown = Assert.Throws<MockHistoricalGatewayException>(gateway.ThrowIfRejected);
        Assert.Contains(expectedFragment, thrown.Message, StringComparison.Ordinal);
        Assert.Contains(expectedFragment, Assert.Single(gateway.Rejections), StringComparison.Ordinal);
    }
}
