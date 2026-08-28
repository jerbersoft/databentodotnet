using System.Globalization;
using System.Text;
using NodaTime;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// The composition test #35's own issue comments asked for by name, and that #32, #33 and #34
/// structurally could not hold between them.
/// </summary>
/// <remarks>
/// <para>
/// #32 gave this codebase <see cref="Symbols"/> and <see cref="DatabentoDotNet.ApiKey"/>; #33 gave
/// it <see cref="DateRange"/> and <see cref="DateTimeRange"/>; #34 gave it
/// <see cref="MockHistoricalGateway"/>. All three were built in parallel by implementers who never
/// saw each other's code, and each passed its own review — but nothing on that branch compiled
/// <c>Symbols</c> and a date range into the same request, because <c>src/DatabentoDotNet.Historical</c>
/// had no reference to <c>DatabentoDotNet.Dbn</c> until this issue's Task 1 added one. The first of
/// #35's two issue comments names the risk directly: the join "is expected to need no
/// <c>using</c>... but that is reasoning, not a passing test." This file is that test.
/// </para>
/// <para>
/// <b>No <c>using DatabentoDotNet;</c> anywhere below, and that omission is the point.</b> This
/// file's namespace is <c>DatabentoDotNet.Historical.Tests</c>, nested under
/// <c>DatabentoDotNet.Historical</c>, itself nested under <c>DatabentoDotNet</c> — the namespace
/// <see cref="Symbols"/> and <see cref="DatabentoDotNet.ApiKey"/> live in. C#'s enclosing-namespace
/// lookup walks outward through every one of those levels, so the two resolve unqualified, the same
/// way <c>HistoricalClientTests.cs</c> already relies on for <c>ApiKey</c>. Adding the
/// <c>using</c> "to be safe" would make this file compile for a different reason than the one it
/// exists to check.
/// </para>
/// <para>
/// <b>What is deliberately not here.</b> <c>HistoricalClientTests.cs</c> carries 35 tests and
/// already owns: the credential travelling only in <c>Authorization</c>
/// (<c>ApiKey_TravelsInTheAuthorizationHeaderAndNowhereElse</c>), all three error-body shapes, the
/// <c>User-Agent</c> prefix, and percent-encoding a comma in a <em>query</em> parameter
/// (<c>Get_PercentEncodesACommaInAParameterValue</c>). Nothing below repeats those. What is new
/// here is real <see cref="Symbols"/> and a real date range composed through a real
/// <see cref="HistoricalClient"/> — which nothing in that file does, because that file's fixed
/// <c>SymbolQuery</c>/<c>CountForm</c> arrays are plain strings, not values built from this
/// project's own types — plus a credential-containment check that repeats
/// <c>ApiKey_TravelsInTheAuthorizationHeaderAndNowhereElse</c>'s own surfaces over requests built
/// from that composition path instead of from fixed literals. It is a new <em>input path</em>,
/// not a new surface — an attempt to add one
/// (<see cref="RecordedRequest.Path"/>/<see cref="RecordedRequest.RouteKey"/>) did not survive
/// being run; see that test's own remarks for why.
/// </para>
/// <para>
/// <b>The GET/POST split below follows D4</b> (#35's decision record): <c>timeseries.get_range</c>
/// is a documented <c>POST</c>+form call site, and <c>metadata.get_dataset_condition</c> a
/// documented <c>GET</c>+query one (the foundation-review comment on #35 gives the full lists for
/// both). Neither endpoint has a facade yet — that is #36's and #38's job — so the parameters below
/// are assembled by hand, the way any caller of the public <see cref="HistoricalClient.SendAsync"/>
/// escape hatch would before its facade exists.
/// </para>
/// </remarks>
public sealed class HistoricalClientCompositionTests
{
    private const string TimeseriesGetRange = "timeseries.get_range";
    private const string GetDatasetCondition = "metadata.get_dataset_condition";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Post_ComposesARealSymbolsAndARealDateTimeRangeIntoUpstreamsExactForm()
    {
        // The nine fields historical/timeseries.rs:128-143 builds, in upstream's own order.
        // Everything but symbols/start/end is a literal: pulling Schema/SType in to produce
        // "trades"/"raw_symbol" would test whether *they* compose with HistoricalClient, a
        // question this task was not asked — #38 gives timeseries.get_range its own facade and
        // its own test. Symbols and DateTimeRange are what the foundation batch left unchecked,
        // so they are the only two pieces of this form built from real library values.
        //
        // ["MSFT", "AAPL"], not alphabetical: Symbols.From preserves order rather than sorting,
        // and upstream's own wiremock test (timeseries.rs:331) deliberately uses the reversed
        // pair "SPOT,AAPL" for the same reason — an alphabetical pair can't tell an
        // order-preserving join from a sorted one apart.
        var symbols = Symbols.From(["MSFT", "AAPL"]);
        var range = DateTimeRange.FromUnixNanoseconds(1_688_428_800_000_000_000, 1_688_515_200_000_000_000);

        KeyValuePair<string, string>[] parameters =
        [
            new("dataset", "GLBX.MDP3"),
            new("schema", "trades"),
            new("encoding", "dbn"),
            new("compression", "zstd"),
            new("stype_in", "raw_symbol"),
            new("stype_out", "instrument_id"),
            new("symbols", symbols.ToApiString()),
            new("start", range.StartUnixNanoseconds.ToString(CultureInfo.InvariantCulture)),
            new("end", range.EndUnixNanoseconds.ToString(CultureInfo.InvariantCulture)),
        ];

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(TimeseriesGetRange, MockHistoricalResponse.Json("{}"));

        await using var client = ClientFor(gateway);
        using (await client.SendAsync(HttpMethod.Post, TimeseriesGetRange, parameters, cancellationToken: Cancel))
        {
        }

        gateway.ThrowIfRejected();

        var recorded = Assert.Single(gateway.Requests);

        // Decoded first: upstream's own spelling of the two values the foundation batch could
        // never prove compose, side by side in one Form for the first time in this codebase.
        // "MSFT,AAPL" is Symbols.ToApiString() (DatabentoDotNet.Dbn); the two integers are
        // DateTimeRange.StartUnixNanoseconds/.EndUnixNanoseconds (DatabentoDotNet.Historical).
        Assert.Equal("MSFT,AAPL", recorded.Form["symbols"]);
        Assert.Equal("1688428800000000000", recorded.Form["start"]);
        Assert.Equal("1688515200000000000", recorded.Form["end"]);

        // Raw next, and byte-exact: FormUrlEncodedContent's own output for these nine pairs in
        // this order, verified independently against a throwaway console program before this
        // assertion was written. A Symbols rendering that joined with the wrong separator, or a
        // range rendered through Instant.ToString() instead of the nanosecond accessor, would
        // show up here even if — implausibly — it still happened to decode back to the same
        // values asserted above.
        const string ExpectedBody =
            "dataset=GLBX.MDP3&schema=trades&encoding=dbn&compression=zstd&stype_in=raw_symbol"
            + "&stype_out=instrument_id&symbols=MSFT%2CAAPL&start=1688428800000000000&end=1688515200000000000";
        Assert.Equal(ExpectedBody, Encoding.UTF8.GetString(recorded.Body.Span));
    }

    [Fact]
    public async Task Get_RendersADateRangeAsIsoDatesInTheQuery_NeverAsUnixNanoseconds()
    {
        // historical.rs:348-355 renders DateRange as start_date/end_date, yyyy-MM-dd, in a
        // query. historical.rs:357-364 also defines AddToQuery<DateTimeRange>, rendering
        // start/end as nanoseconds into a query — but it has zero call sites anywhere in the
        // crate. Modelling this test on that second form would pin a request upstream never
        // sends; it is also, per #35's first issue comment, the exact mistake the harness's own
        // exemplar form made before this issue existed.
        var range = DateRange.Between(new LocalDate(2023, 7, 4), new LocalDate(2023, 7, 5));

        KeyValuePair<string, string>[] parameters =
        [
            new("dataset", "GLBX.MDP3"),
            // Upstream's own parameter names, not ones this test invented.
            new("start_date", range.StartIsoDate),
            new("end_date", range.EndIsoDate),
        ];

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(GetDatasetCondition, MockHistoricalResponse.Json("{}"));

        await using var client = ClientFor(gateway);
        using (await client.SendAsync(HttpMethod.Get, GetDatasetCondition, parameters, cancellationToken: Cancel))
        {
        }

        gateway.ThrowIfRejected();

        var recorded = Assert.Single(gateway.Requests);
        Assert.Equal("2023-07-04", recorded.Query["start_date"]);
        Assert.Equal("2023-07-05", recorded.Query["end_date"]);

        // Exact, not Contains: a RawQuery equal to this literal cannot also contain
        // "1688428800000000000" — the same 2023-07-04T00:00:00Z rendered, wrongly, as Unix
        // nanoseconds. Byte-exact here is what makes the DateRange/DateTimeRange swap the other
        // test in this file guards from the POST side impossible to pass from the GET side too.
        Assert.Equal("?dataset=GLBX.MDP3&start_date=2023-07-04&end_date=2023-07-05", recorded.RawQuery);
    }

    [Fact]
    public async Task ApiKey_AppearsInNoSurface_WhenQueryAndFormValuesComeFromRealSymbolsAndDateRanges()
    {
        var symbols = Symbols.From(["AAPL", "MSFT"]);
        var dateRange = DateRange.Between(new LocalDate(2023, 7, 4), new LocalDate(2023, 7, 5));
        var timeRange = DateTimeRange.FromUnixNanoseconds(1_688_428_800_000_000_000, 1_688_515_200_000_000_000);

        KeyValuePair<string, string>[] queryParameters =
        [
            new("dataset", "GLBX.MDP3"),
            new("start_date", dateRange.StartIsoDate),
            new("end_date", dateRange.EndIsoDate),
        ];

        KeyValuePair<string, string>[] formParameters =
        [
            new("dataset", "GLBX.MDP3"),
            new("symbols", symbols.ToApiString()),
            new("start", timeRange.StartUnixNanoseconds.ToString(CultureInfo.InvariantCulture)),
            new("end", timeRange.EndUnixNanoseconds.ToString(CultureInfo.InvariantCulture)),
        ];

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(GetDatasetCondition, MockHistoricalResponse.Json("{}"));
        gateway.Post(TimeseriesGetRange, MockHistoricalResponse.Json("{}"));

        await using var client = ClientFor(gateway);

        // A GET whose query came from a real DateRange, and a POST whose form came from a real
        // Symbols and a real DateTimeRange. One request can't carry both a query and a form — D4
        // routes parameters by HTTP method — so "both" means these two exchanges from one
        // client, each scanned below in its own iteration of the foreach.
        using (await client.SendAsync(HttpMethod.Get, GetDatasetCondition, queryParameters, cancellationToken: Cancel))
        {
        }

        using (await client.SendAsync(HttpMethod.Post, TimeseriesGetRange, formParameters, cancellationToken: Cancel))
        {
        }

        // The harness's own guard: MockHistoricalGateway.Refuse rejects any request whose query
        // or form carries the key under a key-looking name, or as the *value* of any query or
        // form parameter. Reaching here without a rejection is that check passing.
        gateway.ThrowIfRejected();

        // What this adds over ApiKey_TravelsInTheAuthorizationHeaderAndNowhereElse is not a new
        // *surface* — RawQuery, Body and Headers below are the same three that test scans, over
        // the same MockHistoricalGateway.Refuse guard. It is a new *input path*: that test's
        // query and form values are fixed string literals (SymbolQuery, CountForm); these come
        // out of a real Symbols.ToApiString(), a real DateRange.StartIsoDate/.EndIsoDate and a
        // real DateTimeRange.StartUnixNanoseconds/.EndUnixNanoseconds — the composition #35's own
        // issue comments asked to have exercised at all, and the one #35's definition of done
        // ties the "key reaches nothing but Authorization" guarantee to. This test scans nothing
        // ApiKey_TravelsInTheAuthorizationHeaderAndNowhereElse does not already scan, and its
        // name should not be read as claiming a broader check than that.
        //
        // An earlier round of this test also scanned RecordedRequest.Path and .RouteKey, on the
        // reasoning that MockHistoricalGateway.Refuse never reads either — true: Refuse touches
        // only Authorization, User-Agent, request.Query and request.Form. That reasoning was
        // correct and still missed the point: this harness's HandleAsync looks the response up
        // by an exact "{Method} {Path}" match, so any request that reaches a 2xx response
        // necessarily has a Path identical to whatever the test itself registered. The assertion
        // was vacuously true given success, not conditionally true — a path that actually
        // differed would fail to route, and SendAsync would throw on the resulting non-2xx
        // status three steps earlier, before the assertion ever ran (confirmed with a mutation;
        // see the task report). It was removed for that reason, and not replaced: a non-form
        // body is outside the guard but unconstructible through this client, which only ever
        // sends a form or nothing, and every header but Authorization is outside the guard but
        // already walked by HistoricalClientTests.cs:164-167. There is no surface left to add.
        Assert.Equal(2, gateway.Requests.Count);
        foreach (var recorded in gateway.Requests)
        {
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
    public async Task ApiKey_NeverAppearsInAnExceptionOrALogEntryOrAnyToString()
    {
        const string DocsUrl = "https://databento.com/docs/api-reference-historical";

        // Four slugs from D4's GET+query list, each carrying a different provocation: the
        // simple error shape, the business error shape, a body that is neither ("unparseable"),
        // and — on a request that otherwise succeeds — an X-Warning header. One client, built on
        // MockHistoricalGateway.TestApiKey, drives all four: every route below is a route a real
        // Databento error or warning could arrive on, and the key never has any business
        // appearing in what any of them produces.
        const string SimpleSlug = "metadata.list_datasets";
        const string BusinessSlug = "metadata.list_schemas";
        const string UnparseableSlug = "metadata.list_fields";
        const string WarningSlug = "metadata.list_publishers";

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(
            SimpleSlug,
            MockHistoricalResponse.SimpleError(401, "Authorization failed: bad key.").WithRequestId("req-simple"));
        gateway.Get(
            BusinessSlug,
            MockHistoricalResponse.BusinessError(
                422,
                "data_start_after_available",
                "The requested start is after the dataset's available range.",
                DocsUrl));
        gateway.Get(
            UnparseableSlug,
            MockHistoricalResponse.Json("<html><head><title>502 Bad Gateway</title></head></html>", 502));
        gateway.Get(
            WarningSlug,
            MockHistoricalResponse.Json("{}").WithWarnings("this dataset is being retired"));

        var logs = new RecordingLoggerFactory();
        await using var client = ClientFor(gateway, logs);

        var simple = await Assert.ThrowsAsync<DatabentoApiException>(() =>
            client.SendAsync(HttpMethod.Get, SimpleSlug, parameters: null, cancellationToken: Cancel));
        var business = await Assert.ThrowsAsync<DatabentoApiException>(() =>
            client.SendAsync(HttpMethod.Get, BusinessSlug, parameters: null, cancellationToken: Cancel));
        var unparseable = await Assert.ThrowsAsync<DatabentoApiException>(() =>
            client.SendAsync(HttpMethod.Get, UnparseableSlug, parameters: null, cancellationToken: Cancel));

        using (await client.SendAsync(HttpMethod.Get, WarningSlug, parameters: null, cancellationToken: Cancel))
        {
        }

        gateway.ThrowIfRejected();

        foreach (var exception in new[] { simple, business, unparseable })
        {
            Assert.DoesNotContain(MockHistoricalGateway.TestApiKey, exception.ToString(), StringComparison.Ordinal);
        }

        // Exactly two: one UnparseableErrorBody entry for the HTML body, one ServerWarning entry
        // for the retirement notice. Pinning the count, rather than only asserting on whatever
        // arrived, is what rules out the scan below passing vacuously because nothing was
        // actually logged.
        Assert.Equal(2, logs.Entries.Count);
        foreach (var entry in logs.Entries)
        {
            Assert.DoesNotContain(MockHistoricalGateway.TestApiKey, entry.Message, StringComparison.Ordinal);
            if (entry.Exception is not null)
            {
                Assert.DoesNotContain(
                    MockHistoricalGateway.TestApiKey, entry.Exception.ToString(), StringComparison.Ordinal);
            }
        }

        // ApiKey.ToString() is the redacted "…" plus the last BucketIdLength characters — never
        // the whole key, and … is the literal ellipsis ApiKey.cs formats with, not three
        // periods.
        var redacted = "…" + MockHistoricalGateway.TestApiKey[^ApiKey.BucketIdLength..];
        Assert.Equal(redacted, client.ApiKey.ToString());

        // HistoricalClient declares no ToString override of its own, so this is the BCL default
        // (the fully qualified type name) — which is exactly the point: the client has nothing
        // to say about its own key, and a future override added for debugging convenience that
        // tried to be helpful by including it would be caught right here.
        Assert.DoesNotContain(MockHistoricalGateway.TestApiKey, client.ToString()!, StringComparison.Ordinal);
    }

    private static HistoricalClient ClientFor(MockHistoricalGateway gateway, RecordingLoggerFactory? logs = null) =>
        new()
        {
            ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey),
            BaseUrl = gateway.BaseUrl,
            LoggerFactory = logs,
        };
}
