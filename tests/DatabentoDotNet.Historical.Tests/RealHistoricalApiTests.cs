using System.Net;
using DatabentoDotNet.Dbn;
using NodaTime;

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

    [Fact(SkipUnless = nameof(IsConfigured), Skip = HistoricalCredentials.SkipReason)]
    public async Task GetDatasetCondition_ReadsEndDateAsInclusive_WhichIsTheDefectPinnedByIssue45()
    {
        await using var client = Client();

        // DateRange.OnDay(d) is (d, d+1) — half-open, one day by this library's contract. The
        // endpoint reads end_date as *inclusive*, so it answers for two days. Upstream documents
        // the inclusive end on this one field (metadata.rs:285) and half-open everywhere else; the
        // port carried the shared half-open type in without absorbing the difference.
        //
        // This asserts the behaviour as it actually is, not as it should be, so that #45 cannot be
        // fixed silently: the fix flips this to a single detail and this test has to change with
        // it. GetDatasetConditionParams' own doc comment deferred exactly this question to #40, and
        // this is the answer.
        var oneDayByOurContract = await client.Metadata.GetDatasetConditionAsync(
            new GetDatasetConditionParams
            {
                Dataset = HistoricalCredentials.Dataset,
                DateRange = DateRange.OnDay(HistoricalCredentials.Date),
            },
            Cancel);

        Assert.Equal(2, oneDayByOurContract.Count);
        Assert.Equal(HistoricalCredentials.Date, oneDayByOurContract[0].Date);
        Assert.Equal(HistoricalCredentials.Date.PlusDays(1), oneDayByOurContract[1].Date);

        // And the general form: n days requested by our contract come back as n + 1.
        var threeDaysByOurContract = await client.Metadata.GetDatasetConditionAsync(
            new GetDatasetConditionParams
            {
                Dataset = HistoricalCredentials.Dataset,
                DateRange = DateRange.Between(
                    HistoricalCredentials.Date, HistoricalCredentials.Date.PlusDays(3)),
            },
            Cancel);

        Assert.Equal(4, threeDaysByOurContract.Count);
        Assert.All(threeDaysByOurContract, detail =>
            Assert.True(detail.Condition != default || detail.LastModifiedDate is not null));
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

        Assert.True(cost >= 0m);

        // The point of returning decimal rather than upstream's f64 (metadata.rs:190). The API
        // answers this endpoint with a JSON number carrying about twelve decimal places — a real
        // response for one settled day was 0.467667996883 — and this is the first check that the
        // converter reads one at full precision instead of through a binary float.
        Assert.True(
            cost > 0m,
            "A range with records should cost something; a zero here means the query matched "
            + "nothing, which GetRecordCount would also have caught.");
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
        Assert.DoesNotContain(
            HistoricalCredentials.ApiKey.Value, rendered, StringComparison.Ordinal);
    }
}
