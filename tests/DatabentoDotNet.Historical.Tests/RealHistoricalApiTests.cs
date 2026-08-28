using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using DatabentoDotNet.Dbn;
using NodaTime;
using NodaTime.Text;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// Opt-in tests against the <b>real</b> Databento historical API.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these exist.</b> <see cref="MockHistoricalGateway"/> and <see cref="MetadataClient"/>
/// were written from the same reading of Databento's HTTP documentation, so a misread parameter
/// name or response field is present on both sides and they agree with each other. CLAUDE.md states
/// the consequence directly: the mock cannot confirm what it shares an author with. Ten endpoints
/// shipped, were reviewed, were merged, and had never been called. These are that call.
/// </para>
/// <para>
/// <b>It is not a theoretical concern.</b> M2's first contact with the real live gateway found two
/// defects the mock could not have found — gateway-generated records carry
/// <c>publisher_id = 0</c>, and symbol mappings arrive at the head of every session (#29). A mock
/// replays what it is told to replay. This class's first run found #45.
/// </para>
/// <para>
/// <b>Nothing here spends money.</b> Every endpoint reached from this class is discovery or a
/// billing enquiry — <c>get_cost</c> exists precisely to be called <em>before</em> committing to a
/// request — which is what makes the whole class free to run behind a key alone. The second gate,
/// <see cref="HistoricalCredentials.RequestVariable"/>, exists for the tests that do spend, and
/// those arrive with <c>timeseries.get_range</c> (#38) and <c>batch.submit_job</c> (#39).
/// </para>
/// <para>
/// <b>Nothing new belongs in <em>this</em> class past that line.</b> A test here that quietly grows
/// a data download is a test that quietly grows a bill, and it would take the class's "free to run"
/// guarantee with it. The same rule <c>RealGatewaySmokeTests</c> carries for M2.
/// </para>
/// <para>
/// <b>They skip rather than fail when no key is configured</b>, and CI filters the category out by
/// name as well. See <see cref="HistoricalCredentials"/>.
/// </para>
/// </remarks>
[Trait("Category", "Historical")]
public class RealHistoricalApiTests
{
    /// <summary>Gate for every <c>SkipUnless</c> in this class.</summary>
    public static bool IsConfigured => HistoricalCredentials.IsConfigured;

    /// <summary>
    /// A syntactically valid key that is not a real one, so <see cref="ApiKey"/> accepts it and the
    /// API is the thing that rejects it. Derived from <see cref="ApiKey.Length"/> rather than typed
    /// out, for the reason <c>RealGatewaySmokeTests</c> gives.
    /// </summary>
    private static readonly string NotARealKey = "db-" + new string('0', ApiKey.Length - 3);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static HistoricalClient Client() =>
        new() { ApiKey = HistoricalCredentials.ApiKey };

    /// <summary>
    /// The range the three billing enquiries price, and the one <see cref="RecordsExist"/> reports
    /// on. A single settled UTC day, so the numbers behind it do not move with the calendar.
    /// </summary>
    private static DateTimeRange PricedRange =>
        DateTimeRange.OnDay(HistoricalCredentials.Date);

    private static MetadataQueryParams PricedQuery()
    {
        Assert.True(
            WireStrings.TryParseSchema(HistoricalCredentials.Schema, out var schema),
            $"{HistoricalCredentials.SchemaVariable} is not a schema this library knows: "
            + $"'{HistoricalCredentials.Schema}'.");

        return new MetadataQueryParams
        {
            Dataset = HistoricalCredentials.Dataset,
            Symbols = Symbols.From(HistoricalCredentials.Symbol),
            Schema = schema,
            DateTimeRange = PricedRange,
        };
    }

    /// <summary>
    /// A query for daily bars over <paramref name="range"/> — the fixed instrument the #46 range
    /// tests measure with, as distinct from <see cref="PricedQuery"/>'s configurable one.
    /// </summary>
    /// <remarks>
    /// <see cref="Schema.Ohlcv1D"/> is pinned rather than read from
    /// <see cref="HistoricalCredentials.Schema"/> because those tests need a schema whose records
    /// land on a known instant; see
    /// <see cref="TheBillingEndpoints_ReadTheRangeEndAsExclusive"/> for why that is the whole
    /// experiment. Dataset, symbol and date remain configurable.
    /// </remarks>
    private static MetadataQueryParams DailyBarQuery(DateTimeRange range) =>
        new()
        {
            Dataset = HistoricalCredentials.Dataset,
            Symbols = Symbols.From(HistoricalCredentials.Symbol),
            Schema = Schema.Ohlcv1D,
            DateTimeRange = range,
        };

    [Fact(SkipUnless = nameof(IsConfigured), Skip = HistoricalCredentials.SkipReason)]
    public async Task ListDatasets_ContainsTheDatasetEveryOtherTestHereUses()
    {
        await using var client = Client();

        var datasets = await client.Metadata.ListDatasetsAsync(cancellationToken: Cancel);

        Assert.NotEmpty(datasets);

        // Asserted first, and about *our* dataset specifically, so that a misconfigured
        // DATABENTO_HISTORICAL_DATASET fails once and says so rather than failing nine more times
        // further down in ways that each look like a different bug.
        Assert.Contains(HistoricalCredentials.Dataset, datasets);
    }

    [Fact(SkipUnless = nameof(IsConfigured), Skip = HistoricalCredentials.SkipReason)]
    public async Task ListSchemas_ParsesEveryWireSpellingAndOffersTheOneWePrice()
    {
        await using var client = Client();

        var schemas = await client.Metadata.ListSchemasAsync(
            HistoricalCredentials.Dataset, Cancel);

        Assert.NotEmpty(schemas);

        // The assertion that only the real API can make. SchemaJsonConverter maps wire spellings to
        // the Schema enum, so a spelling Databento ships that this library does not know would
        // throw right here — and nothing in the mock could ever surface it, because the mock only
        // ever sends spellings we already wrote down.
        Assert.Contains(PricedQuery().Schema, schemas);
    }

    [Fact(SkipUnless = nameof(IsConfigured), Skip = HistoricalCredentials.SkipReason)]
    public async Task ListPublishers_ReturnsEntriesWithRealIdsVenuesAndDescriptions()
    {
        await using var client = Client();

        var publishers = await client.Metadata.ListPublishersAsync(Cancel);

        Assert.NotEmpty(publishers);
        Assert.All(publishers, publisher =>
        {
            // publisher_id = 0 is the undefined publisher, and #29 is the reminder that a zero here
            // is a real wire value rather than an impossible one — it is what the *live* gateway
            // stamps on its own generated records. It has no business in this catalog.
            Assert.NotEqual(0, publisher.PublisherId);
            Assert.NotEmpty(publisher.Dataset);
            Assert.NotEmpty(publisher.Venue);
            Assert.NotEmpty(publisher.Description);
        });

        Assert.Contains(publishers, p => p.Dataset == HistoricalCredentials.Dataset);
    }

    [Fact(SkipUnless = nameof(IsConfigured), Skip = HistoricalCredentials.SkipReason)]
    public async Task ListFields_ForTheDbnEncodingOfThePricedSchema_ReturnsNamedTypedFields()
    {
        await using var client = Client();

        var fields = await client.Metadata.ListFieldsAsync(
            new ListFieldsParams { Encoding = Encoding.Dbn, Schema = PricedQuery().Schema },
            Cancel);

        Assert.NotEmpty(fields);
        Assert.All(fields, field =>
        {
            Assert.NotEmpty(field.Name);
            Assert.NotEmpty(field.TypeName);
        });

        // Every DBN record begins with a header, so these four are on every schema's field list
        // regardless of which one DATABENTO_HISTORICAL_SCHEMA names.
        foreach (var required in new[] { "length", "rtype", "publisher_id", "instrument_id" })
        {
            Assert.Contains(fields, f => f.Name == required);
        }
    }

    [Fact(SkipUnless = nameof(IsConfigured), Skip = HistoricalCredentials.SkipReason)]
    public async Task ListUnitPrices_ReturnsPricedSchemasForEveryFeedMode()
    {
        await using var client = Client();

        var modes = await client.Metadata.ListUnitPricesAsync(
            HistoricalCredentials.Dataset, Cancel);

        Assert.NotEmpty(modes);
        Assert.All(modes, mode =>
        {
            // FeedModeJsonConverter and SchemaJsonConverter both run here, over keys Databento
            // chooses rather than keys we chose. An unrecognised feed mode or schema spelling
            // throws rather than being quietly dropped.
            Assert.NotEmpty(mode.UnitPrices);
            Assert.All(mode.UnitPrices, price => Assert.True(price.Value >= 0m));
        });

        // Distinct modes, not one mode repeated — the response is a list keyed by mode, and a
        // converter that collapsed them would still produce a non-empty list.
        Assert.Equal(modes.Count, modes.Select(m => m.Mode).Distinct().Count());
    }

    [Fact(SkipUnless = nameof(IsConfigured), Skip = HistoricalCredentials.SkipReason)]
    public async Task GetDatasetRange_ReturnsAnOrderedRangeWithPerSchemaDetail()
    {
        await using var client = Client();

        var range = await client.Metadata.GetDatasetRangeAsync(
            HistoricalCredentials.Dataset, Cancel);

        Assert.True(range.Start < range.End);
        Assert.NotEmpty(range.RangeBySchema);
        Assert.All(range.RangeBySchema, entry =>
            Assert.True(entry.Value.Start < entry.Value.End));

        // The range this class prices has to sit inside the dataset's available history, or every
        // billing enquiry below is asking about data that does not exist and a zero answer would
        // look like agreement rather than like a misconfiguration.
        Assert.True(
            range.Start <= PricedRange.Start && PricedRange.End <= range.End,
            $"{HistoricalCredentials.DateVariable} names a day outside "
            + $"{HistoricalCredentials.Dataset}'s available history.");
    }

    /// <summary>
    /// The corrected contract from #45, asserted where it was broken: against the real endpoint.
    /// This test previously pinned the <em>defect</em> — <c>OnDay(d)</c> answered for two days —
    /// specifically so the fix could not land silently. It is the assertion that changed, and it
    /// changed because the request did.
    /// </summary>
    [Fact(SkipUnless = nameof(IsConfigured), Skip = HistoricalCredentials.SkipReason)]
    public async Task GetDatasetCondition_ForOneDay_ReportsOnThatDayAlone()
    {
        await using var client = Client();

        var oneDay = await client.Metadata.GetDatasetConditionAsync(
            new GetDatasetConditionParams
            {
                Dataset = HistoricalCredentials.Dataset,
                DateRange = DateRange.OnDay(HistoricalCredentials.Date),
            },
            Cancel);

        var only = Assert.Single(oneDay);
        Assert.Equal(HistoricalCredentials.Date, only.Date);

        // And the general form: n days by this library's half-open contract are n days back. The
        // endpoint's own end_date is inclusive, so this is only true because the renderer converts
        // — a mock replaying a fixture we wrote could agree with either contract, which is the
        // whole reason this assertion lives out here against the real API.
        var threeDays = await client.Metadata.GetDatasetConditionAsync(
            new GetDatasetConditionParams
            {
                Dataset = HistoricalCredentials.Dataset,
                DateRange = DateRange.Between(
                    HistoricalCredentials.Date, HistoricalCredentials.Date.PlusDays(3)),
            },
            Cancel);

        Assert.Equal(3, threeDays.Count);

        // Consecutive and ascending, which is a claim about the response rather than about the
        // enum. `DatasetCondition.Available` is the zero value, so asserting `Condition != default`
        // would read as a check and be satisfied by every ordinary day.
        for (var i = 0; i < threeDays.Count; i++)
        {
            Assert.Equal(HistoricalCredentials.Date.PlusDays(i), threeDays[i].Date);
        }
    }

    /// <summary>
    /// The other half of #45, and the reason its fix went into one renderer rather than the shared
    /// one: <c>list_datasets</c> takes the same <see cref="DateRange"/> and reads <c>end_date</c>
    /// as <b>exclusive</b>. Upstream documents nothing either way for this endpoint
    /// (<c>metadata.rs:41-50</c>), so this is the probe that settled it, kept as a test.
    /// </summary>
    /// <remarks>
    /// The discriminator is a dataset's first day of data, read from <c>get_dataset_range</c> in
    /// the same run rather than hard-coded — Databento adds datasets, and a pinned date would rot
    /// into a test that passes for the wrong reason. Asking for the range that <em>ends</em> on
    /// that first day separates the two readings cleanly: an exclusive <c>end_date</c> stops the
    /// day before, so the dataset is absent; an inclusive one would include it.
    /// <para>
    /// <b>If this fails, check entitlements before suspecting the endpoint.</b>
    /// <c>get_dataset_range</c> answers for the caller's entitlements while <c>list_datasets</c>
    /// filters on availability, so an account entitled to only part of a dataset's history would
    /// see the dataset listed on the day before its own window opens and fail the first assertion
    /// without <c>end_date</c> having changed meaning at all. The two agree for the default
    /// dataset, which is why this is a note rather than a different discriminator.
    /// </para>
    /// </remarks>
    [Fact(SkipUnless = nameof(IsConfigured), Skip = HistoricalCredentials.SkipReason)]
    public async Task ListDatasets_ReadsEndDateAsExclusive_UnlikeGetDatasetCondition()
    {
        await using var client = Client();

        var firstDay = (await client.Metadata.GetDatasetRangeAsync(
            HistoricalCredentials.Dataset, Cancel)).Start.InUtc().Date;

        // [firstDay - 1, firstDay): ends the day before the dataset has any data, under this
        // library's half-open reading. Absent means the endpoint agrees with that reading.
        var endingOnTheFirstDay = await client.Metadata.ListDatasetsAsync(
            DateRange.OnDay(firstDay.PlusDays(-1)), Cancel);

        Assert.DoesNotContain(HistoricalCredentials.Dataset, endingOnTheFirstDay);

        // The control, and the half that makes the assertion above mean something: shift the same
        // one-day range forward by a day and the dataset appears. Without this, an endpoint that
        // ignored the range entirely, or answered nothing at all, would pass the first assertion.
        var startingOnTheFirstDay = await client.Metadata.ListDatasetsAsync(
            DateRange.OnDay(firstDay), Cancel);

        Assert.Contains(HistoricalCredentials.Dataset, startingOnTheFirstDay);
    }

    [Fact(SkipUnless = nameof(IsConfigured), Skip = HistoricalCredentials.SkipReason)]
    public async Task GetRecordCount_ForTheSettledRange_IsNonZero()
    {
        await using var client = Client();

        var count = await client.Metadata.GetRecordCountAsync(PricedQuery(), Cancel);

        // A settled day for a real symbol has records. Zero here means the dataset, symbol, schema
        // and date have been overridden into a combination that has none, which would make every
        // other billing assertion vacuously true.
        Assert.True(
            count > 0,
            $"No records for {HistoricalCredentials.Symbol} on {HistoricalCredentials.Date}. "
            + "Check the dataset, symbol, schema and date overrides.");
    }

    [Fact(SkipUnless = nameof(IsConfigured), Skip = HistoricalCredentials.SkipReason)]
    public async Task GetBillableSize_ForTheSameRange_ExceedsWhatTheRecordCountAloneCouldBe()
    {
        await using var client = Client();
        var query = PricedQuery();

        var count = await client.Metadata.GetRecordCountAsync(query, Cancel);
        var bytes = await client.Metadata.GetBillableSizeAsync(query, Cancel);

        // Both endpoints answer for the identical parameter set, so they are checkable against each
        // other rather than only against zero: DBN's smallest record is well over a byte, so the
        // billable size of n records cannot be smaller than n.
        Assert.True(bytes > count, $"billable size {bytes} is not larger than {count} records.");
    }

    [Fact(SkipUnless = nameof(IsConfigured), Skip = HistoricalCredentials.SkipReason)]
    public async Task GetCost_ForTheSameRange_ParsesAsDecimalAtFullPrecision()
    {
        await using var client = Client();

        var cost = await client.Metadata.GetCostAsync(PricedQuery(), Cancel);

        // A range with records costs something. A zero here means the query matched nothing, which
        // GetRecordCount would also have caught.
        Assert.True(cost > 0m, "A range with records should cost something.");

        // What this does *not* assert, deliberately. The API answers this endpoint with a JSON
        // number carrying about twelve decimal places — a real response for one settled day was
        // 0.467667996883 — which is why the return type is decimal rather than upstream's f64
        // (metadata.rs:190). Pinning a scale would be brittle, since a range that happens to price
        // to 0.5 has a scale of one, and pinning the value itself would pin this account's plan.
        // Reaching this line at all is the check: a JSON number of that width parsed into decimal
        // without the converter throwing.
        Assert.Equal(cost, decimal.Parse(
            cost.ToString(System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact(SkipUnless = nameof(IsConfigured), Skip = HistoricalCredentials.SkipReason)]
    public async Task WithAKeyTheApiRejects_ThrowsWithoutLeakingIt()
    {
        // Free, and deterministic regardless of entitlements: a well-formed key that is not a real
        // one is refused by the API before it does any work.
        await using var client = new HistoricalClient { ApiKey = new ApiKey(NotARealKey) };

        var rejected = await Assert.ThrowsAsync<DatabentoApiException>(
            () => client.Metadata.ListDatasetsAsync(cancellationToken: Cancel));

        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        // The key travels in an Authorization header on every one of these requests, so the
        // redaction rule is load-bearing here in a way it is not for most exceptions. Nothing the
        // exception renders may carry it — not the message, not the payload, not the inner
        // exception.
        var rendered = rejected.ToString();
        Assert.DoesNotContain(NotARealKey, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(
            NotARealKey[..^ApiKey.BucketIdLength], rendered, StringComparison.Ordinal);

        // And the real key, which was never sent on this request, certainly must not appear.
        // Assert.False rather than Assert.DoesNotContain: the latter renders the substring it
        // searched for into the failure message, and here that substring is the live key. The
        // failure this guards is a leak, so its own message must not be one.
        Assert.False(
            rendered.Contains(HistoricalCredentials.ApiKey.Value, StringComparison.Ordinal),
            "The configured API key appeared in the rendered exception.");
    }

    /// <summary>
    /// <c>symbology.resolve</c> reads <c>end_date</c> as <b>exclusive</b>, so a one-day
    /// <see cref="DateRange"/> resolves one day.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is #45's rule kept rather than restated.</b> That issue found
    /// <c>get_dataset_condition</c> reading <c>end_date</c> as inclusive while its neighbour
    /// <c>list_datasets</c> read it as exclusive, and closed with "probe the endpoint you are
    /// about to change, not the one next to it". Upstream documents an exclusive end here
    /// (<c>symbology.rs:78</c>) — and documented one for <c>get_dataset_condition</c>'s
    /// neighbours too. So this endpoint was asked directly before
    /// <see cref="ResolveParams.ToFormParameters"/> chose a renderer, and this test is that
    /// question made permanent.
    /// </para>
    /// <para>
    /// Free: <c>symbology.resolve</c> moves no market data, so this runs behind
    /// <see cref="IsConfigured"/> alone, with no billable opt-in.
    /// </para>
    /// </remarks>
    [Fact(SkipUnless = nameof(IsConfigured), Skip = HistoricalCredentials.SkipReason)]
    public async Task Resolve_ReadsEndDateAsExclusive()
    {
        await using var client = Client();
        var day = HistoricalCredentials.Date;

        var resolution = await client.Symbology.ResolveAsync(
            new ResolveParams
            {
                Dataset = HistoricalCredentials.Dataset,
                Symbols = Symbols.From(HistoricalCredentials.Symbol),
                DateRange = DateRange.OnDay(day),
            },
            Cancel);

        var interval = Assert.Single(resolution.Mappings[HistoricalCredentials.Symbol]);
        Assert.Equal(day, interval.StartDate);
        Assert.Equal(day.PlusDays(1), interval.EndDate);

        // A three-day range returns the end that was sent, unchanged -- the other direction of the
        // same fact, and the one that would fail if the inclusive renderer were ever swapped in.
        var threeDays = await client.Symbology.ResolveAsync(
            new ResolveParams
            {
                Dataset = HistoricalCredentials.Dataset,
                Symbols = Symbols.From(HistoricalCredentials.Symbol),
                DateRange = DateRange.Between(day, day.PlusDays(3)),
            },
            Cancel);

        Assert.Equal(
            day.PlusDays(3),
            Assert.Single(threeDays.Mappings[HistoricalCredentials.Symbol]).EndDate);
    }

    /// <summary>
    /// The server refuses <c>start_date == end_date</c>, which is how an inclusive-end endpoint
    /// would have to spell a single day.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The strongest form of the previous test's claim, and the one that does not depend on
    /// reading intervals correctly.</b> <c>get_dataset_condition</c> accepts <c>start == end</c>
    /// and answers for that one day; this endpoint rejects it outright with HTTP 422
    /// <c>data_date_range_start_on_or_after_end</c>. An endpoint cannot both refuse an empty
    /// half-open range and read its end as inclusive.
    /// </para>
    /// <para>
    /// Reaching the request needs a <see cref="DateRange"/> the type will not construct — every
    /// factory refuses <c>end &lt;= start</c> — so the parameters are rendered by hand. That is
    /// the point: <b>this library cannot send the request the server rejects</b>, and the test
    /// records what would happen if it could.
    /// </para>
    /// </remarks>
    [Fact(SkipUnless = nameof(IsConfigured), Skip = HistoricalCredentials.SkipReason)]
    public async Task Resolve_WithAnEmptyRange_IsRefusedByTheServer()
    {
        await using var client = Client();
        var day = LocalDatePattern.Iso.Format(HistoricalCredentials.Date);

        var refused = await Assert.ThrowsAsync<DatabentoApiException>(
            () => client.SendJsonAsync(
                HttpMethod.Post,
                "symbology.resolve",
                [
                    new KeyValuePair<string, string>("dataset", HistoricalCredentials.Dataset),
                    new KeyValuePair<string, string>("stype_in", "raw_symbol"),
                    new KeyValuePair<string, string>("stype_out", "instrument_id"),
                    new KeyValuePair<string, string>("symbols", HistoricalCredentials.Symbol),
                    new KeyValuePair<string, string>("start_date", day),
                    new KeyValuePair<string, string>("end_date", day),
                ],
                RawHistoricalJson.Default.JsonDocument,
                Cancel));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
    }

    /// <summary>
    /// A symbol that does not exist comes back in <see cref="Resolution.NotFound"/> — and in
    /// <see cref="Resolution.Mappings"/> as well, with no intervals.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The mock cannot establish this, because its fixtures were written from these
    /// responses.</b> #37's definition of done asks that a partly-resolved symbol appear in
    /// <c>mappings</c> as well as its own bucket; the real API turns out to do something stronger
    /// and stranger — <em>every</em> requested symbol is a key in <c>result</c>, including one that
    /// resolved to nothing at all — and that is worth a test against the server rather than
    /// against a fixture agreeing with the reader that produced it.
    /// </para>
    /// <para>
    /// <b>It also pins that this is not an error.</b> The response arrives as HTTP 200 carrying
    /// <c>"status": 2, "message": "Not found"</c>; if the transport ever began treating that body
    /// as a failure, this call would throw instead of returning.
    /// </para>
    /// <para>
    /// <b><see cref="Resolution.Partial"/> is not asserted here, and that is a limit rather than an
    /// omission.</b> No request to a dataset like this one produces a partial resolution: raw
    /// symbols resolve across the whole requested window even outside a contract's listed life,
    /// and a range starting before the dataset's first day is refused with a 422. So <c>partial</c>
    /// is covered against the mock, with a body marked synthetic, and the two buckets reachable for
    /// real are covered here.
    /// </para>
    /// </remarks>
    [Fact(SkipUnless = nameof(IsConfigured), Skip = HistoricalCredentials.SkipReason)]
    public async Task Resolve_PutsAnUnresolvableSymbolInMappingsAsWellAsNotFound()
    {
        const string NotASymbol = "NOTAREALSYMBOL";

        await using var client = Client();

        var resolution = await client.Symbology.ResolveAsync(
            new ResolveParams
            {
                Dataset = HistoricalCredentials.Dataset,
                Symbols = Symbols.From([HistoricalCredentials.Symbol, NotASymbol]),
                DateRange = DateRange.OnDay(HistoricalCredentials.Date),
            },
            Cancel);

        Assert.Contains(NotASymbol, resolution.NotFound);
        Assert.Empty(resolution.Mappings[NotASymbol]);

        // The symbol that did resolve is in the same dictionary, with its interval.
        Assert.Single(resolution.Mappings[HistoricalCredentials.Symbol]);
    }

    /// <summary>
    /// The three billing endpoints read a <see cref="DateTimeRange"/>'s <c>end</c> as
    /// <b>exclusive</b>, which is what <see cref="DateTimeRange"/> has always told its callers and
    /// what nothing had ever asked the server (#46).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The claim under test was a universal one, and universal claims about a server are what
    /// #45 exists to warn about.</b> <see cref="DateTimeRange"/>'s summary asserted an exclusive
    /// end for every time-queried endpoint, and the only real-API coverage it had was
    /// <see cref="PricedRange"/> feeding count and cost assertions that a range off by a whole day
    /// would satisfy just as well. <c>DateRange</c> carried the identical shape of claim into #45
    /// and was <em>wrong</em>; it carried it into #37 and was right. Being right by luck twice is
    /// not verification.
    /// </para>
    /// <para>
    /// <b>The discriminator is <c>ohlcv-1d</c>, not the configured schema, and that is deliberate.
    /// </b> A daily bar is stamped at exactly UTC midnight — probed, not assumed — so a
    /// one-nanosecond window can be placed to make the two readings differ by a whole record
    /// instead of by a nanosecond no trade lands on. The configured schema defaults to
    /// <c>trades</c>, and both windows return zero against it: no trade printed at exactly
    /// <c>00:00:00.000000000</c>. That is a schema this measurement cannot be made with, so it is
    /// not used — the control below would fail rather than the discriminator passing hollowly, but
    /// a test that reports "no bar at midnight" when the real answer is "wrong instrument" is a
    /// bad way to learn it. The dataset, symbol and date stay configurable; only the schema is
    /// pinned, because it is the instrument of measurement rather than the subject.
    /// </para>
    /// <para>
    /// <b>Both windows sit on the same instant, so only the configured day needs data.</b> The
    /// obvious framing — one whole day, expecting one bar — cannot discriminate on its own, because
    /// an inclusive end would only add a record if the <em>next</em> day also has one. Whether it
    /// does is a question about the dataset's calendar rather than about the endpoint, and not one
    /// worth answering: ending a window exactly at the bar removes the dependency entirely. The
    /// control proves a bar sits at midnight, and the discriminator asks whether a window ending
    /// there contains it.
    /// </para>
    /// <para>
    /// Free. All three are billing enquiries, so this keeps the class's guarantee.
    /// </para>
    /// </remarks>
    [Fact(SkipUnless = nameof(IsConfigured), Skip = HistoricalCredentials.SkipReason)]
    public async Task TheBillingEndpoints_ReadTheRangeEndAsExclusive()
    {
        await using var client = Client();

        // Midnight UTC on the configured day, reached through the library's own conversion rather
        // than rebuilt here, so a change in that conversion moves this test with it.
        var midnight = DateRange.OnDay(HistoricalCredentials.Date).ToDateTimeRange().Start;
        var oneNanosecond = Duration.FromNanoseconds(1L);

        var atTheBar = DailyBarQuery(DateTimeRange.Between(midnight, midnight + oneNanosecond));
        var endingAtTheBar = DailyBarQuery(DateTimeRange.Between(midnight - oneNanosecond, midnight));

        // The control, and it has to pass first: it establishes that a daily bar is stamped at
        // exactly midnight and that these windows are not empty for some unrelated reason. Without
        // it, the discriminator's zero would be indistinguishable from "this symbol has no data".
        var barsAtTheBar = await client.Metadata.GetRecordCountAsync(atTheBar, Cancel);
        Assert.True(
            barsAtTheBar == 1,
            $"Expected exactly one {Schema.Ohlcv1D.ToWireString()} bar stamped at "
            + $"{midnight} for {HistoricalCredentials.Symbol}, got {barsAtTheBar}. "
            + $"Check the dataset, symbol and date overrides before suspecting the endpoint.");

        // The discriminator. This window ends exactly where the bar is stamped: an exclusive end
        // leaves the bar out, an inclusive one takes it in.
        Assert.Equal(0UL, await client.Metadata.GetRecordCountAsync(endingAtTheBar, Cancel));

        // The same question put to the other two endpoints, which take the identical parameter
        // type. Three separate quantities agreeing is what lets the correction below name the
        // whole MetadataQueryParams group rather than one endpoint.
        Assert.True(await client.Metadata.GetBillableSizeAsync(atTheBar, Cancel) > 0);
        Assert.Equal(0UL, await client.Metadata.GetBillableSizeAsync(endingAtTheBar, Cancel));

        Assert.True(await client.Metadata.GetCostAsync(atTheBar, Cancel) > 0m);
        Assert.Equal(0m, await client.Metadata.GetCostAsync(endingAtTheBar, Cancel));
    }

    /// <summary>
    /// The server refuses <c>start == end</c> on a time range, which is how an inclusive-end
    /// endpoint would have to spell a single instant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same evidence #37 found for <c>symbology.resolve</c>, in the time-range direction.</b>
    /// It is stronger than a count, because it does not depend on reading a record boundary
    /// correctly: an endpoint cannot both reject an empty half-open range and read its end as
    /// inclusive, since under that reading the range is not empty at all — it is exactly one
    /// instant wide. <c>get_dataset_condition</c>, which genuinely does read its end as inclusive,
    /// accepts <c>start_date == end_date</c> and answers for that day (#45).
    /// </para>
    /// <para>
    /// As with the <c>symbology.resolve</c> case above, the parameters are rendered by hand:
    /// every <see cref="DateTimeRange"/> factory refuses <c>end &lt;= start</c>, so
    /// <b>this library cannot send the request the server rejects</b> and the test records what
    /// would happen if it could.
    /// </para>
    /// </remarks>
    [Fact(SkipUnless = nameof(IsConfigured), Skip = HistoricalCredentials.SkipReason)]
    public async Task WithAnEmptyTimeRange_TheBillingEndpointsAreRefusedByTheServer()
    {
        await using var client = Client();

        var midnight = DateRange.OnDay(HistoricalCredentials.Date).ToDateTimeRange()
            .StartUnixNanoseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var refused = await Assert.ThrowsAsync<DatabentoApiException>(
            () => client.SendJsonAsync(
                HttpMethod.Post,
                "metadata.get_record_count",
                [
                    new KeyValuePair<string, string>("dataset", HistoricalCredentials.Dataset),
                    new KeyValuePair<string, string>("schema", Schema.Ohlcv1D.ToWireString()),
                    new KeyValuePair<string, string>("stype_in", SType.RawSymbol.ToWireString()),
                    new KeyValuePair<string, string>("symbols", HistoricalCredentials.Symbol),
                    new KeyValuePair<string, string>("start", midnight),
                    new KeyValuePair<string, string>("end", midnight),
                ],
                RawHistoricalJson.Default.JsonDocument,
                Cancel));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        // The reason, not just the status: this endpoint answers 422 for several unrelated
        // complaints, so the status alone would be satisfied by a symbol that failed to resolve or
        // a schema the dataset does not offer -- neither of which is the claim under test.
        Assert.Equal("data_time_range_start_on_or_after_end", refused.Case);
    }

    /// <summary>
    /// <c>get_dataset_range</c>'s <c>end</c> is an <b>exclusive</b> bound on what a query may ask
    /// for, which is what <see cref="DatasetRange.ToDateTimeRange"/> already assumes when it hands
    /// that instant to <see cref="DateTimeRange.Between"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the response direction of the same question, and it is where a #45-shaped
    /// off-by-one would have lived.</b> Everything else in #46 asks how the server reads an
    /// <c>end</c> this library sends; this asks what the <c>end</c> the server <em>sends</em>
    /// means. If it named the last available instant rather than the first unavailable one, then
    /// <see cref="DatasetRange.ToDateTimeRange"/> would be building a range that silently excludes
    /// the final record, and nothing in the library would say so.
    /// </para>
    /// <para>
    /// It does not: two queries sharing a start and differing only in that one ends exactly on the
    /// reported bound and the other one nanosecond past it are answered and refused respectively.
    /// So the reported <c>end</c> is the first instant a query may not reach, which is precisely an
    /// exclusive bound. <b>No change was needed — recorded because a defect looked for and not
    /// found is worth as much as one found, and only if it is written down.</b>
    /// </para>
    /// <para>
    /// <b>Two details here are the difference between a test and a test that passes for the wrong
    /// reason</b>, and both were found by probing a draft of it rather than by reading it back.
    /// The refusal is asserted by <see cref="DatabentoApiException.Case"/> and not by its status:
    /// this endpoint answers 422 for several unrelated complaints, so a status-only assertion is
    /// satisfied by any of them. And the symbols are <see cref="Symbols.All"/> rather than the
    /// configured one, because the configured symbol is an expired contract chosen for being
    /// settled — over a window near an active dataset's live edge it resolves to nothing, and the
    /// endpoint answers <c>422 symbology_invalid_request</c> having never reached the question
    /// this test is asking.
    /// </para>
    /// <para>
    /// <b>The bound's value is never asserted, only its behaviour.</b> For an active dataset it is
    /// a live ingest watermark that moves every few seconds — it read
    /// <c>2026-08-28T07:37:47.468634000Z</c> when this was written — so the two calls here can
    /// legitimately see different values, and the test is written to survive that.
    /// </para>
    /// </remarks>
    [Fact(SkipUnless = nameof(IsConfigured), Skip = HistoricalCredentials.SkipReason)]
    public async Task GetDatasetRange_ReportsAnEndThatIsExclusiveOfWhatAQueryMayAsk()
    {
        await using var client = Client();

        var bySchema = (await client.Metadata.GetDatasetRangeAsync(
            HistoricalCredentials.Dataset, Cancel)).RangeBySchema;

        Assert.True(
            bySchema.ContainsKey(Schema.Ohlcv1D),
            $"{HistoricalCredentials.Dataset} does not offer "
            + $"{Schema.Ohlcv1D.ToWireString()}, which the #46 range tests measure with.");

        var reportedEnd = bySchema[Schema.Ohlcv1D].End;
        var aDayBefore = reportedEnd - Duration.FromDays(1);

        MetadataQueryParams DailyBarsOverEverything(DateTimeRange range) =>
            new()
            {
                Dataset = HistoricalCredentials.Dataset,
                Symbols = Symbols.All,
                Schema = Schema.Ohlcv1D,
                DateTimeRange = range,
            };

        // The control, and the half that makes the refusal below mean something: a window ending
        // exactly on the reported bound is answered. Without it, the refusal would be equally
        // consistent with the bound being unusable from either side.
        var upToTheEnd = await client.Metadata.GetRecordCountAsync(
            DailyBarsOverEverything(DateTimeRange.Between(aDayBefore, reportedEnd)), Cancel);

        Assert.True(upToTheEnd > 0, "A window ending on the reported bound returned nothing.");

        // The discriminator. Same start, same everything, one nanosecond further on: out of range.
        var refused = await Assert.ThrowsAsync<DatabentoApiException>(
            () => client.Metadata.GetRecordCountAsync(
                DailyBarsOverEverything(
                    DateTimeRange.Between(aDayBefore, reportedEnd + Duration.FromNanoseconds(1L))),
                Cancel));

        // The reason, not just the status. See the remarks: 422 alone does not discriminate.
        Assert.Equal("data_schema_not_fully_available", refused.Case);
    }

    /// <summary>
    /// The response carries <c>stype_in</c> and <c>stype_out</c> of its own — and this library
    /// deliberately does not read them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This pins the fact that corrected #37's porting note.</b> That note said to echo the two
    /// symbology types from the request "because the response does not carry them". Echoing them is
    /// right; the reason was not, and the difference matters — a design comment resting on a false
    /// claim about the wire is one nobody can re-derive later. So the raw body is read here, and
    /// the two keys are asserted present.
    /// </para>
    /// <para>
    /// The same read pins the other two claims the mock's fixtures are transcribed from: that
    /// <c>result</c> holds a key for a symbol resolving to nothing, and that
    /// <see cref="Resolution"/> ignores the <c>status</c> and <c>message</c> fields the body also
    /// carries.
    /// </para>
    /// </remarks>
    [Fact(SkipUnless = nameof(IsConfigured), Skip = HistoricalCredentials.SkipReason)]
    public async Task Resolve_ResponseCarriesEchoedFields_ThisLibraryDeliberatelyIgnores()
    {
        await using var client = Client();
        var day = HistoricalCredentials.Date;

        using var body = await client.SendJsonAsync(
            HttpMethod.Post,
            "symbology.resolve",
            [
                new KeyValuePair<string, string>("dataset", HistoricalCredentials.Dataset),
                new KeyValuePair<string, string>("stype_in", "raw_symbol"),
                new KeyValuePair<string, string>("stype_out", "instrument_id"),
                new KeyValuePair<string, string>("symbols", HistoricalCredentials.Symbol),
                new KeyValuePair<string, string>("start_date", LocalDatePattern.Iso.Format(day)),
                new KeyValuePair<string, string>("end_date", LocalDatePattern.Iso.Format(day.PlusDays(1))),
            ],
            RawHistoricalJson.Default.JsonDocument,
            Cancel);

        var root = body.RootElement;
        Assert.Equal("raw_symbol", root.GetProperty("stype_in").GetString());
        Assert.Equal("instrument_id", root.GetProperty("stype_out").GetString());

        // Present, and ignored: Resolution exposes neither, so a change in either would not
        // surface anywhere except here.
        Assert.True(root.TryGetProperty("status", out _));
        Assert.True(root.TryGetProperty("message", out _));
    }
}

/// <summary>
/// A context for reading a response as an untyped document, for the two tests above that assert
/// something about the wire rather than about a decoded type.
/// </summary>
/// <remarks>
/// Declared here rather than reused from the shipping assembly, whose contexts are
/// <see langword="internal"/> and which this project cannot name — the same split
/// <c>MetadataResponseTests</c> makes, and for the same reason: a test about the raw body should
/// not be reading it through the very context whose configuration it is checking around.
/// </remarks>
[JsonSerializable(typeof(JsonDocument))]
internal sealed partial class RawHistoricalJson : JsonSerializerContext
{
}
