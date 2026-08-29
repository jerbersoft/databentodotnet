using System.Globalization;
using System.Text.Json;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Historical;
using DatabentoDotNet.Historical.Tests;
using NodaTime;

namespace DatabentoDotNet.Reference.Tests;

/// <summary>
/// Tests for <see cref="SecurityMasterClient"/> — the two forms it posts, the three keys that
/// separate them, and the fifty-field row they both read back.
/// </summary>
/// <remarks>
/// <para>
/// These drive a real <see cref="ReferenceClient"/> at <see cref="MockHistoricalGateway"/> over a
/// real socket, as <see cref="AdjustmentFactorsClientTests"/> does and for the same reason: an
/// <c>HttpMessageHandler</c> stub never opens one, and half of what is under test is what
/// <see cref="HttpClient"/> itself does with a form body.
/// </para>
/// <para>
/// <b>The gateway is an independent oracle for the request and only a mirror for the response.</b>
/// It was written from Databento's HTTP documentation, before any reference client existed, and it
/// decodes the form body itself — so what <see cref="RecordedRequest.Form"/> reports is a genuine
/// second reading of what went on the wire. The response half is not: the rows below are strings
/// this file wrote, and a misreading of the reference API's JSON shape would sit in both this
/// fixture and the model without either noticing. Only #57 settles that, against real rows.
/// </para>
/// </remarks>
public class SecurityMasterClientTests
{
    private const string GetRange = "security_master.get_range";
    private const string GetLast = "security_master.get_last";

    /// <summary>
    /// Upstream's own test fixture row (<c>security.rs:333-384</c>), transcribed verbatim — nulls,
    /// scrambled values and all. Twelve of its fifty fields are <c>null</c>, which is why
    /// <see cref="FullRow"/> exists as well.
    /// </summary>
    private const string UpstreamRow = """
        {"ts_record":"2009-05-12T13:44:05Z","ts_effective":"2000-07-04T00:00:00Z","listing_id":"L-211","listing_group_id":"LG-81068","security_id":"S-516531","issuer_id":"I-2112","listing_status":"L","listing_source":"M","listing_created_date":"2001-01-06","listing_date":"1996-09-30","delisting_date":null,"issuer_name":"Sun Life Financial Services of Canada Inc.","security_type":null,"security_description":"Ordinary Shares","primary_exchange":"CATSE","exchange":"USNYSE","operating_mic":"XBEY","symbol":"SLF","nasdaq_symbol":"SLF","local_code":"SOLA","isin":"CA8667961053","us_code":"866796105","bbg_comp_id":"BBG000BRM1N5","bbg_comp_ticker":"SLF LB","figi":"BBG000BRM1Y3","figi_ticker":"SLF LB","fisn":null,"lei":null,"sic":"CDA","cik":"Share Depository Certificate","gics":null,"naics":null,"cic":"USD","cfi":"I","incorporation_country":"CA","listing_country":"LB","register_country":"LB","trading_currency":"USD","multi_currency":false,"segment_mic_name":null,"segment_mic":null,"structure":null,"lot_size":1,"par_value":null,"par_value_currency":null,"voting":"M","vote_per_sec":null,"shares_outstanding":14920000,"shares_outstanding_date":"2000-07-04","ts_created":"1970-01-01T00:00:00.000000000Z"}
        """;

    /// <summary>
    /// <see cref="UpstreamRow"/> with its twelve <c>null</c>s filled in, so that every one of the
    /// fifty fields is read with a value at least once.
    /// </summary>
    /// <remarks>
    /// Thirty-eight of these values are upstream's, and the twelve filled ones —
    /// <c>delisting_date</c>, <c>security_type</c>, <c>fisn</c>, <c>lei</c>, <c>gics</c>,
    /// <c>naics</c>, <c>segment_mic_name</c>, <c>segment_mic</c>, <c>structure</c>,
    /// <c>par_value</c>, <c>par_value_currency</c> and <c>vote_per_sec</c> — are invented, because
    /// no fixture anywhere carries them. They are plausible rather than authoritative, which is
    /// enough for what this row is for: proving that a field is read at all. What each field
    /// <em>means</em> is upstream's doc comment, and what a real one holds is #57's to find out.
    /// </remarks>
    private const string FullRow = """
        {"ts_record":"2009-05-12T13:44:05Z","ts_effective":"2000-07-04T00:00:00Z","listing_id":"L-211","listing_group_id":"LG-81068","security_id":"S-516531","issuer_id":"I-2112","listing_status":"L","listing_source":"M","listing_created_date":"2001-01-06","listing_date":"1996-09-30","delisting_date":"2019-03-15","issuer_name":"Sun Life Financial Services of Canada Inc.","security_type":"EQS","security_description":"Ordinary Shares","primary_exchange":"CATSE","exchange":"USNYSE","operating_mic":"XBEY","symbol":"SLF","nasdaq_symbol":"SLF","local_code":"SOLA","isin":"CA8667961053","us_code":"866796105","bbg_comp_id":"BBG000BRM1N5","bbg_comp_ticker":"SLF LB","figi":"BBG000BRM1Y3","figi_ticker":"SLF LB","fisn":"SUN LIFE FINL/SH","lei":"549300FTHOEC5AV6QO23","sic":"CDA","cik":"Share Depository Certificate","gics":"40301040","naics":"524113","cic":"USD","cfi":"I","incorporation_country":"CA","listing_country":"LB","register_country":"LB","trading_currency":"USD","multi_currency":false,"segment_mic_name":"NYSE Equities","segment_mic":"XNYS","structure":"Ordinary","lot_size":1,"par_value":1.5,"par_value_currency":"CAD","voting":"M","vote_per_sec":1,"shares_outstanding":14920000,"shares_outstanding_date":"2000-07-04","ts_created":"1970-01-01T00:00:00.000000000Z"}
        """;

    /// <summary>The fifteen required fields and nothing else — the thirty-five optional ones absent.</summary>
    private const string SparseRow = """
        {"ts_record":"2024-05-01T12:00:00Z","ts_effective":"2024-05-01T00:00:00Z","listing_id":"L-1","listing_group_id":"LG-1","security_id":"S-1","issuer_id":"I-1","listing_status":"N","listing_source":"S","listing_created_date":"2024-04-01","issuer_name":"Acme","security_description":"Ordinary Shares","exchange":"USNYSE","incorporation_country":"US","multi_currency":true,"ts_created":"2024-05-01 12:00:00"}
        """;

    /// <summary>The same fifteen, with all thirty-five optional fields present and explicitly <c>null</c>.</summary>
    private const string NulledRow = """
        {"ts_record":"2024-05-01T12:00:00Z","ts_effective":"2024-05-01T00:00:00Z","listing_id":"L-1","listing_group_id":"LG-1","security_id":"S-1","issuer_id":"I-1","listing_status":"D","listing_source":"M","listing_created_date":"2024-04-01","listing_date":null,"delisting_date":null,"issuer_name":"Acme","security_type":null,"security_description":"Ordinary Shares","primary_exchange":null,"exchange":"USNYSE","operating_mic":null,"symbol":null,"nasdaq_symbol":null,"local_code":null,"isin":null,"us_code":null,"bbg_comp_id":null,"bbg_comp_ticker":null,"figi":null,"figi_ticker":null,"fisn":null,"lei":null,"sic":null,"cik":null,"gics":null,"naics":null,"cic":null,"cfi":null,"incorporation_country":"US","listing_country":null,"register_country":null,"trading_currency":null,"multi_currency":false,"segment_mic_name":null,"segment_mic":null,"structure":null,"lot_size":null,"par_value":null,"par_value_currency":null,"voting":null,"vote_per_sec":null,"shares_outstanding":null,"shares_outstanding_date":null,"ts_created":"2024-05-01T12:00:00Z"}
        """;

    /// <summary>
    /// <c>2023-10-10T00:00:00Z</c> in Unix nanoseconds — the start upstream's own test uses
    /// (<c>security.rs:325</c>). Written as the integer rather than derived from <see cref="Start"/>,
    /// so the assertion on the <c>start</c> form field compares the wire against a literal instead
    /// of against the arithmetic that produced it.
    /// </summary>
    private const long StartUnixNanoseconds = 1_696_896_000_000_000_000L;

    /// <summary>Thirty days later, <c>2023-11-09T00:00:00Z</c>. Likewise a literal.</summary>
    private const long EndUnixNanoseconds = 1_699_488_000_000_000_000L;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static Instant Start => NodaConstants.UnixEpoch + Duration.FromNanoseconds(StartUnixNanoseconds);

    private static Instant End => NodaConstants.UnixEpoch + Duration.FromNanoseconds(EndUnixNanoseconds);

    /* ------------------------------------------------------------------ *
     * What goes on the wire.
     * ------------------------------------------------------------------ */

    [Theory]
    [InlineData(GetRange)]
    [InlineData(GetLast)]
    public async Task BothEndpointsPostToTheirVersionedSlug(string endpoint)
    {
        await using var gateway = await StartAsync(endpoint);
        await using var client = ClientFor(gateway);
        await DrainAsync(client, endpoint);

        gateway.ThrowIfRejected();
        var recorded = Assert.Single(gateway.Requests);
        Assert.Equal("POST", recorded.Method);
        Assert.Equal("/v0/" + endpoint, recorded.Path);
    }

    /// <summary>
    /// An unfiltered open range sends six fields and no more. <c>end</c>, <c>countries</c> and
    /// <c>security_types</c> are <em>absent</em>, not empty — <c>countries=</c> is a different
    /// request from no <c>countries</c> at all.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_SendsExactlySixFieldsForAnUnfilteredOpenRange()
    {
        await using var gateway = await StartAsync(GetRange);
        await using var client = ClientFor(gateway);
        await DrainAsync(client.SecurityMaster.GetRangeAsync(Range(), Cancel));

        gateway.ThrowIfRejected();
        var form = Assert.Single(gateway.Requests).Form;

        Assert.Equal(
            ["allocate_isins", "compression", "index", "start", "stype_in", "symbols"],
            form.Keys.OrderBy(key => key, StringComparer.Ordinal));

        Assert.Equal("ts_effective", form["index"]);
        Assert.Equal("raw_symbol", form["stype_in"]);
        Assert.Equal("MSFT", form["symbols"]);
        Assert.Equal("true", form["allocate_isins"]);
        Assert.Equal("zstd", form["compression"]);
        Assert.Equal(StartUnixNanoseconds, long.Parse(form["start"], CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// And <c>get_last</c> sends four. It has no range to open or close, so there is no fifth field
    /// it could conditionally add.
    /// </summary>
    [Fact]
    public async Task GetLastAsync_SendsExactlyFourFieldsWhenUnfiltered()
    {
        await using var gateway = await StartAsync(GetLast);
        await using var client = ClientFor(gateway);
        await DrainAsync(client.SecurityMaster.GetLastAsync(Last(), Cancel));

        gateway.ThrowIfRejected();
        var form = Assert.Single(gateway.Requests).Form;

        Assert.Equal(
            ["allocate_isins", "compression", "stype_in", "symbols"],
            form.Keys.OrderBy(key => key, StringComparer.Ordinal));

        Assert.Equal("raw_symbol", form["stype_in"]);
        Assert.Equal("MSFT", form["symbols"]);
        Assert.Equal("true", form["allocate_isins"]);
        Assert.Equal("zstd", form["compression"]);
    }

    /// <summary>
    /// <b>The Definition of done's headline assertion.</b> Both endpoints are called with every
    /// shared parameter set identically, so any difference in the recorded forms can only come from
    /// the three keys this test is about. It is a set comparison rather than a presence check on
    /// <c>index</c>, because the failure worth catching is <c>get_last</c> quietly inheriting a
    /// range — and a request carrying <c>start</c> without <c>index</c> would pass a presence check.
    /// </summary>
    [Fact]
    public async Task TheTwoFormsDifferByExactlyIndexStartAndEnd()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetRange, MockHistoricalResponse.ZstdJsonLines(FullRow))
               .Post(GetLast, MockHistoricalResponse.ZstdJsonLines(FullRow));

        await using var client = ClientFor(gateway);

        await DrainAsync(client.SecurityMaster.GetRangeAsync(
            new SecurityMasterGetRangeParams
            {
                Symbols = Symbols.From("MSFT"),
                DateTimeRange = ReferenceDateTimeRange.Between(Start, End),
                StypeIn = SType.RawSymbol,
                Countries = [Country.From("US")],
                SecurityTypes = [SecurityType.From("EQS")],
                AllocateIsins = false,
            },
            Cancel));

        await DrainAsync(client.SecurityMaster.GetLastAsync(
            new SecurityMasterGetLastParams
            {
                Symbols = Symbols.From("MSFT"),
                StypeIn = SType.RawSymbol,
                Countries = [Country.From("US")],
                SecurityTypes = [SecurityType.From("EQS")],
                AllocateIsins = false,
            },
            Cancel));

        gateway.ThrowIfRejected();
        Assert.Equal(2, gateway.Requests.Count);

        var range = gateway.Requests[0].Form;
        var last = gateway.Requests[1].Form;

        Assert.Equal(
            ["end", "index", "start"],
            range.Keys.Except(last.Keys, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal));
        Assert.Empty(last.Keys.Except(range.Keys, StringComparer.Ordinal));

        // Every key they share carries the same value too, so "they differ by three keys" is a
        // claim about the whole body rather than only about its key set.
        foreach (var key in last.Keys)
        {
            Assert.Equal(range[key], last[key]);
        }
    }

    /// <summary>
    /// The same claim from the other side, and the one the compiler enforces:
    /// <see cref="SecurityMasterGetLastParams"/> has nowhere to put a range, and neither parameter
    /// type is assignable to the other — so <c>get_last</c> cannot inherit one by being handed the
    /// wrong object.
    /// </summary>
    [Fact]
    public void GetLastParams_HasNoRangeOfAnySpellingAndIsUnrelatedToGetRangeParams()
    {
        Assert.Null(typeof(SecurityMasterGetLastParams).GetProperty("Index"));
        Assert.Null(typeof(SecurityMasterGetLastParams).GetProperty("DateTimeRange"));

        Assert.False(typeof(SecurityMasterGetLastParams).IsAssignableFrom(typeof(SecurityMasterGetRangeParams)));
        Assert.False(typeof(SecurityMasterGetRangeParams).IsAssignableFrom(typeof(SecurityMasterGetLastParams)));
    }

    [Fact]
    public async Task GetRangeAsync_SendsTheEndOnlyWhenTheRangeIsClosed()
    {
        await using var gateway = await StartAsync(GetRange);
        await using var client = ClientFor(gateway);
        await DrainAsync(client.SecurityMaster.GetRangeAsync(
            Range() with { DateTimeRange = ReferenceDateTimeRange.Between(Start, End) },
            Cancel));

        gateway.ThrowIfRejected();
        Assert.Equal(
            EndUnixNanoseconds,
            long.Parse(Assert.Single(gateway.Requests).Form["end"], CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Both spellings of the index reach the wire. The default is asserted above; this is the one a
    /// caller has to ask for, and the two are one character apart in a way a swapped
    /// <c>switch</c> arm would hide.
    /// </summary>
    [Theory]
    [InlineData(SecurityMasterIndex.TsEffective, "ts_effective")]
    [InlineData(SecurityMasterIndex.TsRecord, "ts_record")]
    public async Task GetRangeAsync_SendsTheIndexItWasGiven(SecurityMasterIndex index, string expected)
    {
        await using var gateway = await StartAsync(GetRange);
        await using var client = ClientFor(gateway);
        await DrainAsync(client.SecurityMaster.GetRangeAsync(Range() with { Index = index }, Cancel));

        gateway.ThrowIfRejected();
        Assert.Equal(expected, Assert.Single(gateway.Requests).Form["index"]);
    }

    [Theory]
    [InlineData(GetRange)]
    [InlineData(GetLast)]
    public async Task BothEndpointsJoinTheTwoFiltersWithCommas(string endpoint)
    {
        await using var gateway = await StartAsync(endpoint);
        await using var client = ClientFor(gateway);
        await DrainAsync(
            client,
            endpoint,
            [Country.From("US"), Country.From("GB")],
            [SecurityType.From("EQS"), SecurityType.From("ETF")]);

        gateway.ThrowIfRejected();
        var form = Assert.Single(gateway.Requests).Form;

        Assert.Equal("US,GB", form["countries"]);
        Assert.Equal("EQS,ETF", form["security_types"]);
    }

    /// <summary>
    /// An empty filter list means the same thing as no filter: the parameter is left out. Asserted
    /// separately from the <see langword="null"/> case because a rendering that special-cased only
    /// <see langword="null"/> would pass the tests above and send <c>countries=</c> here.
    /// </summary>
    [Theory]
    [InlineData(GetRange)]
    [InlineData(GetLast)]
    public async Task BothEndpointsOmitAnEmptyFilterRatherThanSendingItEmpty(string endpoint)
    {
        await using var gateway = await StartAsync(endpoint);
        await using var client = ClientFor(gateway);
        await DrainAsync(client, endpoint, [], []);

        gateway.ThrowIfRejected();
        var form = Assert.Single(gateway.Requests).Form;

        Assert.DoesNotContain("countries", form.Keys);
        Assert.DoesNotContain("security_types", form.Keys);
    }

    /// <summary>
    /// <c>bool.ToString()</c> is <c>True</c> and <c>False</c>; upstream's <c>bool::to_string</c> is
    /// lower case. The difference is invisible in C# and load-bearing on the wire, so both
    /// spellings are asserted on both endpoints rather than only the default on one.
    /// </summary>
    [Theory]
    [InlineData(GetRange, true, "true")]
    [InlineData(GetRange, false, "false")]
    [InlineData(GetLast, true, "true")]
    [InlineData(GetLast, false, "false")]
    public async Task BothEndpointsRenderAllocateIsinsLowerCase(string endpoint, bool allocate, string expected)
    {
        await using var gateway = await StartAsync(endpoint);
        await using var client = ClientFor(gateway);
        await DrainAsync(client, endpoint, allocateIsins: allocate);

        gateway.ThrowIfRejected();
        Assert.Equal(expected, Assert.Single(gateway.Requests).Form["allocate_isins"]);
    }

    /// <summary>
    /// <c>compression=zstd</c> is on every request and is not caller-settable: neither parameter
    /// type has a property for it, so the only way to observe the value is the wire, and the only
    /// way to change it would be to edit the library.
    /// </summary>
    [Theory]
    [InlineData(GetRange)]
    [InlineData(GetLast)]
    public async Task BothEndpointsAlwaysSendZstdAndOfferNoWayToChangeIt(string endpoint)
    {
        Assert.Null(typeof(SecurityMasterGetRangeParams).GetProperty("Compression"));
        Assert.Null(typeof(SecurityMasterGetLastParams).GetProperty("Compression"));

        await using var gateway = await StartAsync(endpoint);
        await using var client = ClientFor(gateway);
        await DrainAsync(client, endpoint, [Country.From("US")], null, allocateIsins: false);

        gateway.ThrowIfRejected();
        Assert.Equal(
            SecurityMasterClient.RequestCompression,
            Assert.Single(gateway.Requests).Form["compression"]);
    }

    /// <summary>
    /// The forms are upstream's push order (<c>security.rs:36-46</c> and <c>:66-73</c>). Asserted
    /// against the rendered list rather than the decoded dictionary, which does not preserve order.
    /// </summary>
    [Fact]
    public void ToFormParameters_UsesUpstreamsPushOrder()
    {
        var range = (Range() with
        {
            DateTimeRange = ReferenceDateTimeRange.Between(Start, End),
            Countries = [Country.From("US")],
            SecurityTypes = [SecurityType.From("EQS")],
        }).ToFormParameters();

        Assert.Equal(
            ["index", "stype_in", "symbols", "allocate_isins", "compression", "start", "end", "countries", "security_types"],
            range.Select(field => field.Key));

        var last = (Last() with
        {
            Countries = [Country.From("US")],
            SecurityTypes = [SecurityType.From("EQS")],
        }).ToFormParameters();

        Assert.Equal(
            ["stype_in", "symbols", "allocate_isins", "compression", "countries", "security_types"],
            last.Select(field => field.Key));
    }

    /* ------------------------------------------------------------------ *
     * The index's wire spellings.
     * ------------------------------------------------------------------ */

    /// <summary>
    /// The two spellings are upstream's <c>Display</c> impl, and they are the names of the response
    /// fields they filter on — so this also pins them to <see cref="SecurityMaster.TsEffective"/>
    /// and <see cref="SecurityMaster.TsRecord"/>.
    /// </summary>
    [Theory]
    [InlineData(SecurityMasterIndex.TsEffective, "ts_effective")]
    [InlineData(SecurityMasterIndex.TsRecord, "ts_record")]
    public void ToWireString_SpellsBothIndexes(SecurityMasterIndex index, string expected) =>
        Assert.Equal(expected, index.ToWireString());

    /// <summary>
    /// And refuses a value outside the set rather than inventing a spelling for it — the contract
    /// every <c>ToWireString</c> in this codebase holds to.
    /// </summary>
    [Fact]
    public void ToWireString_RefusesAnUndefinedIndex() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ((SecurityMasterIndex)99).ToWireString());

    /// <summary>
    /// <see cref="SecurityMasterIndex.TsEffective"/> is the zero value, so a caller who never
    /// touches the property sends what upstream's <c>#[default]</c> would.
    /// </summary>
    [Fact]
    public void TsEffective_IsTheDefaultIndex()
    {
        Assert.Equal(SecurityMasterIndex.TsEffective, default);
        Assert.Equal(SecurityMasterIndex.TsEffective, Range().Index);
    }

    /* ------------------------------------------------------------------ *
     * When the request is made, and when it is not.
     * ------------------------------------------------------------------ */

    /// <summary>
    /// Nothing is sent until the enumeration starts. Both endpoints bill, so a caller who builds a
    /// query and never enumerates must not be charged for it.
    /// </summary>
    [Theory]
    [InlineData(GetRange)]
    [InlineData(GetLast)]
    public async Task NeitherEndpointSendsAnythingUntilTheEnumerationStarts(string endpoint)
    {
        await using var gateway = await StartAsync(endpoint);
        await using var client = ClientFor(gateway);

        var rows = endpoint == GetLast
            ? client.SecurityMaster.GetLastAsync(Last(), Cancel)
            : client.SecurityMaster.GetRangeAsync(Range(), Cancel);

        Assert.Empty(gateway.Requests);

        await DrainAsync(rows);
        Assert.Single(gateway.Requests);
    }

    /// <summary>
    /// A bad argument faults at the call, not at the first <c>MoveNextAsync</c>. Inside an iterator
    /// these checks would be deferred — or, for a caller who never enumerates, skipped entirely.
    /// </summary>
    [Fact]
    public async Task BothEndpointsValidateEagerlyRatherThanAtTheFirstStep()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        await using var client = ClientFor(gateway);

        Assert.Throws<ArgumentNullException>(() => client.SecurityMaster.GetRangeAsync(null!, Cancel));
        Assert.Throws<ArgumentNullException>(() => client.SecurityMaster.GetLastAsync(null!, Cancel));

        // A `required` property stops a caller omitting Symbols; it does not stop them assigning
        // default, and ToApiString refuses to render one.
        Assert.Throws<InvalidOperationException>(
            () => client.SecurityMaster.GetRangeAsync(Range() with { Symbols = default }, Cancel));
        Assert.Throws<InvalidOperationException>(
            () => client.SecurityMaster.GetLastAsync(Last() with { Symbols = default }, Cancel));

        Assert.Throws<InvalidOperationException>(
            () => client.SecurityMaster.GetRangeAsync(Range() with { DateTimeRange = default }, Cancel));

        // And the index, which only get_range has: an undefined value is refused where it was
        // assigned rather than rendered as a number the API would not recognise.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => client.SecurityMaster.GetRangeAsync(Range() with { Index = (SecurityMasterIndex)99 }, Cancel));

        Assert.Empty(gateway.Requests);
    }

    /// <summary>
    /// A disposed client refuses at the call too, for the same placement reason: the transport is
    /// reached before the iterator is built, so the mistake surfaces where it was made rather than
    /// at the first row.
    /// </summary>
    [Fact]
    public async Task BothEndpointsRefuseADisposedClientAtTheCall()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        var client = ClientFor(gateway);
        await client.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => client.SecurityMaster.GetRangeAsync(Range(), Cancel));
        Assert.Throws<ObjectDisposedException>(() => client.SecurityMaster.GetLastAsync(Last(), Cancel));

        Assert.Empty(gateway.Requests);
    }

    [Fact]
    public async Task SecurityMaster_IsBuiltOnceAndCached()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        await using var client = ClientFor(gateway);

        Assert.Same(client.SecurityMaster, client.SecurityMaster);
        Assert.NotSame(client.SecurityMaster, (object)client.AdjustmentFactors);
    }

    /* ------------------------------------------------------------------ *
     * What comes back.
     * ------------------------------------------------------------------ */

    /// <summary>
    /// The model has exactly fifty public properties, which is the count the issue's scope names.
    /// A field silently dropped from the model is skipped without complaint by
    /// <see cref="System.Text.Json"/>, so the count is asserted rather than inferred from the
    /// assertions below.
    /// </summary>
    [Fact]
    public void SecurityMaster_HasFiftyProperties() =>
        Assert.Equal(50, typeof(SecurityMaster).GetProperties().Length);

    /// <summary>
    /// All fifty fields, from a fully populated row, through <b>both</b> endpoints — which is also
    /// the assertion that they share one model and one serializer context. Upstream's own test is
    /// parameterised over the same two endpoints for the same reason (<c>security.rs:324</c>).
    /// </summary>
    [Theory]
    [InlineData(GetRange)]
    [InlineData(GetLast)]
    public async Task BothEndpointsReadAllFiftyFields(string endpoint)
    {
        await using var gateway = await StartAsync(endpoint);
        await using var client = ClientFor(gateway);
        var row = Assert.Single(await DrainAsync(client, endpoint));

        gateway.ThrowIfRejected();

        // Identifiers.
        Assert.Equal(Instant.FromUtc(2009, 5, 12, 13, 44, 5), row.TsRecord);
        Assert.Equal(Instant.FromUtc(2000, 7, 4, 0, 0, 0), row.TsEffective);
        Assert.Equal("L-211", row.ListingId);
        Assert.Equal("LG-81068", row.ListingGroupId);
        Assert.Equal("S-516531", row.SecurityId);
        Assert.Equal("I-2112", row.IssuerId);

        // Listing.
        Assert.Equal(ListingStatus.Listed, row.ListingStatus);
        Assert.Equal(ListingSource.Main, row.ListingSource);
        Assert.Equal(new LocalDate(2001, 1, 6), row.ListingCreatedDate);
        Assert.Equal(new LocalDate(1996, 9, 30), row.ListingDate);
        Assert.Equal(new LocalDate(2019, 3, 15), row.DelistingDate);

        // Exchange.
        Assert.Equal("Sun Life Financial Services of Canada Inc.", row.IssuerName);
        Assert.Equal(SecurityType.From("EQS"), row.SecurityType);
        Assert.Equal("Ordinary Shares", row.SecurityDescription);
        Assert.Equal("CATSE", row.PrimaryExchange);
        Assert.Equal("USNYSE", row.Exchange);
        Assert.Equal("XBEY", row.OperatingMic);

        // Symbology.
        Assert.Equal("SLF", row.Symbol);
        Assert.Equal("SLF", row.NasdaqSymbol);
        Assert.Equal("SOLA", row.LocalCode);
        Assert.Equal("CA8667961053", row.Isin);
        Assert.Equal("866796105", row.UsCode);
        Assert.Equal("BBG000BRM1N5", row.BbgCompId);
        Assert.Equal("SLF LB", row.BbgCompTicker);
        Assert.Equal("BBG000BRM1Y3", row.Figi);
        Assert.Equal("SLF LB", row.FigiTicker);
        Assert.Equal("SUN LIFE FINL/SH", row.Fisn);
        Assert.Equal("549300FTHOEC5AV6QO23", row.Lei);
        Assert.Equal("CDA", row.Sic);
        Assert.Equal("Share Depository Certificate", row.Cik);
        Assert.Equal("40301040", row.Gics);
        Assert.Equal("524113", row.Naics);
        Assert.Equal("USD", row.Cic);
        Assert.Equal("I", row.Cfi);

        // Country.
        Assert.Equal(Country.From("CA"), row.IncorporationCountry);
        Assert.Equal(Country.From("LB"), row.ListingCountry);
        Assert.Equal(Country.From("LB"), row.RegisterCountry);
        Assert.Equal(Currency.From("USD"), row.TradingCurrency);
        Assert.False(row.MultiCurrency);

        // Financials.
        Assert.Equal("NYSE Equities", row.SegmentMicName);
        Assert.Equal("XNYS", row.SegmentMic);
        Assert.Equal("Ordinary", row.Structure);
        Assert.Equal<uint?>(1, row.LotSize);
        Assert.Equal(1.5m, row.ParValue);
        Assert.Equal(Currency.From("CAD"), row.ParValueCurrency);
        Assert.Equal(Voting.Multiple, row.Voting);
        Assert.Equal(1m, row.VotePerSec);
        Assert.Equal<ulong?>(14_920_000, row.SharesOutstanding);
        Assert.Equal(new LocalDate(2000, 7, 4), row.SharesOutstandingDate);

        Assert.Equal(Instant.FromUnixTimeTicks(0), row.TsCreated);
    }

    /// <summary>
    /// Upstream's fixture row read verbatim, including its twelve <c>null</c>s, and asserting the
    /// five things upstream's own test asserts about it (<c>security.rs:427-431</c>). This is the
    /// closest thing to a shared oracle the two libraries have on this endpoint: a value read
    /// differently here than there shows up as a disagreement rather than as two consistent
    /// mistakes.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_ReadsUpstreamsFixtureRowAsUpstreamDoes()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetRange, MockHistoricalResponse.ZstdJsonLines(UpstreamRow));

        await using var client = ClientFor(gateway);
        var row = Assert.Single(await DrainAsync(client.SecurityMaster.GetRangeAsync(Range(), Cancel)));

        gateway.ThrowIfRejected();

        Assert.Equal(ListingStatus.Listed, row.ListingStatus);
        Assert.Equal(ListingSource.Main, row.ListingSource);
        Assert.False(row.SecurityType.HasValue);
        Assert.Equal(Country.From("CA"), row.IncorporationCountry);
        Assert.Equal(Voting.Multiple, row.Voting);
    }

    /// <summary>
    /// The thirty-five optional fields, absent. A model that only ever sees a fully populated row
    /// proves nothing about <c>Option</c>: a property wrongly marked <see langword="required"/>
    /// passes every assertion above and throws here.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_ReadsARowWithEveryOptionalFieldAbsent()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetRange, MockHistoricalResponse.ZstdJsonLines(SparseRow));

        await using var client = ClientFor(gateway);
        var row = Assert.Single(await DrainAsync(client.SecurityMaster.GetRangeAsync(Range(), Cancel)));

        gateway.ThrowIfRejected();
        AssertEveryOptionalFieldIsAbsent(row);

        // And the fifteen that are not optional still arrived.
        Assert.Equal("L-1", row.ListingId);
        Assert.Equal("LG-1", row.ListingGroupId);
        Assert.Equal("S-1", row.SecurityId);
        Assert.Equal("I-1", row.IssuerId);
        Assert.Equal(ListingStatus.New, row.ListingStatus);
        Assert.Equal(ListingSource.Secondary, row.ListingSource);
        Assert.Equal(new LocalDate(2024, 4, 1), row.ListingCreatedDate);
        Assert.Equal("Acme", row.IssuerName);
        Assert.Equal("Ordinary Shares", row.SecurityDescription);
        Assert.Equal("USNYSE", row.Exchange);
        Assert.Equal(Country.From("US"), row.IncorporationCountry);
        Assert.True(row.MultiCurrency);
        Assert.Equal(Instant.FromUtc(2024, 5, 1, 12, 0, 0), row.TsRecord);
        Assert.Equal(Instant.FromUtc(2024, 5, 1, 0, 0, 0), row.TsEffective);
        Assert.Equal(Instant.FromUtc(2024, 5, 1, 12, 0, 0), row.TsCreated);
    }

    /// <summary>
    /// The same thirty-five, present and explicitly <c>null</c>. Distinct from the absent case: a
    /// converter that rejects a null token passes the test above and fails here, which makes this
    /// the test that pins <c>ReferenceCodeJsonConverter</c>'s null handling — the framework's own
    /// default rather than something <c>HandleNull</c> switches on (#60) — and the only check that
    /// <see cref="System.Text.Json"/> really does answer <c>null</c> for a <c>Voting?</c> without
    /// reaching <see cref="Json.VotingJsonConverter"/>, which would reject it.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_ReadsARowWithEveryOptionalFieldExplicitlyNull()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetRange, MockHistoricalResponse.ZstdJsonLines(NulledRow));

        await using var client = ClientFor(gateway);
        var row = Assert.Single(await DrainAsync(client.SecurityMaster.GetRangeAsync(Range(), Cancel)));

        gateway.ThrowIfRejected();
        AssertEveryOptionalFieldIsAbsent(row);

        Assert.Equal(ListingStatus.Delisted, row.ListingStatus);
        Assert.Equal(ListingSource.Main, row.ListingSource);
        Assert.False(row.MultiCurrency);
    }

    /// <summary>
    /// The behavioural difference from upstream, stated as a measurement rather than as a comment.
    /// Upstream sorts its buffered <c>Vec</c> by whichever timestamp the index names
    /// (<c>security.rs:50-53</c>); a stream cannot, so these rows come out in the order the server
    /// sent them — descending here, which is the order a sort would destroy. The index is
    /// <see cref="SecurityMasterIndex.TsRecord"/> so that the request is one upstream would have
    /// sorted by <c>ts_record</c>, and it is still on the wire.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_PreservesServerOrderRatherThanSortingByTheIndex()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(
            GetRange,
            MockHistoricalResponse.ZstdJsonLines(
                RowWithTsRecord("2024-12-31T00:00:00Z"),
                RowWithTsRecord("2024-01-01T00:00:00Z"),
                RowWithTsRecord("2024-06-15T00:00:00Z")));

        await using var client = ClientFor(gateway);
        var rows = await DrainAsync(client.SecurityMaster.GetRangeAsync(
            Range() with { Index = SecurityMasterIndex.TsRecord },
            Cancel));

        gateway.ThrowIfRejected();
        Assert.Equal(
            [
                Instant.FromUtc(2024, 12, 31, 0, 0, 0),
                Instant.FromUtc(2024, 1, 1, 0, 0, 0),
                Instant.FromUtc(2024, 6, 15, 0, 0, 0),
            ],
            rows.Select(row => row.TsRecord));

        Assert.Equal("ts_record", Assert.Single(gateway.Requests).Form["index"]);
    }

    /// <summary>
    /// And <c>get_last</c>'s sort, which upstream performs unconditionally and with no request
    /// counterpart at all (<c>security.rs:77</c>). Dropping it is #52's decision restated rather
    /// than a second one — there is no <c>index</c> here to have justified it.
    /// </summary>
    [Fact]
    public async Task GetLastAsync_PreservesServerOrderRatherThanSortingByTsEffective()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(
            GetLast,
            MockHistoricalResponse.ZstdJsonLines(
                RowWithTsEffective("2024-12-31T00:00:00Z"),
                RowWithTsEffective("2024-01-01T00:00:00Z"),
                RowWithTsEffective("2024-06-15T00:00:00Z")));

        await using var client = ClientFor(gateway);
        var rows = await DrainAsync(client.SecurityMaster.GetLastAsync(Last(), Cancel));

        gateway.ThrowIfRejected();
        Assert.Equal(
            [
                Instant.FromUtc(2024, 12, 31, 0, 0, 0),
                Instant.FromUtc(2024, 1, 1, 0, 0, 0),
                Instant.FromUtc(2024, 6, 15, 0, 0, 0),
            ],
            rows.Select(row => row.TsEffective));

        Assert.DoesNotContain("index", Assert.Single(gateway.Requests).Form.Keys);
    }

    /// <summary>
    /// An unmodelled <c>security_type</c> is carried, not lost — the property upstream gives up by
    /// typing its own field as an enum over 30 of the dictionary's 64 codes. The same holds for a
    /// country, which is why both are asserted: they are different types behind one converter.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_KeepsCodesThisLibraryDoesNotKnow()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(
            GetRange,
            MockHistoricalResponse.ZstdJsonLines(
                FullRow
                    .Replace(""""security_type":"EQS"""", """"security_type":"ZZZ"""", StringComparison.Ordinal)
                    .Replace(""""incorporation_country":"CA"""", """"incorporation_country":"QX"""", StringComparison.Ordinal)));

        await using var client = ClientFor(gateway);
        var row = Assert.Single(await DrainAsync(client.SecurityMaster.GetRangeAsync(Range(), Cancel)));

        gateway.ThrowIfRejected();

        Assert.Equal("ZZZ", row.SecurityType.Code);
        Assert.True(row.SecurityType.HasValue);
        Assert.False(row.SecurityType.IsKnown);

        Assert.Equal("QX", row.IncorporationCountry.Code);
        Assert.True(row.IncorporationCountry.HasValue);
        Assert.False(row.IncorporationCountry.IsKnown);
    }

    /// <summary>
    /// An unmodelled <c>listing_status</c> is an error, because that field is one of the nine
    /// closed alphabets: a new value in it would be a wire-format change rather than a dictionary
    /// entry. The pair with the test above is the whole of #50 and #51 restated on one row.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_RejectsAListingStatusOutsideItsAlphabet()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(
            GetRange,
            MockHistoricalResponse.ZstdJsonLines(
                FullRow.Replace(""""listing_status":"L"""", """"listing_status":"Z"""", StringComparison.Ordinal)));

        await using var client = ClientFor(gateway);

        var thrown = await Assert.ThrowsAsync<JsonException>(
            async () => await DrainAsync(client.SecurityMaster.GetRangeAsync(Range(), Cancel)));

        // The offending code, named. A bare Contains("Z") would also pass on a message that merely
        // said the row was bad, which is the assertion this is not.
        Assert.Contains("has no wire code 'Z'", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <em>blank</em> <c>voting</c> is rejected too, and that is a different claim from the null
    /// one. <see cref="System.Text.Json"/> answers a null token for the <c>Voting?</c> itself; the
    /// empty string reaches <see cref="Json.VotingJsonConverter"/>, and the <c>VOTING</c> group of
    /// <c>corporate_actions.list_enums</c> lists no blank entry — so it is a malformed response
    /// rather than a third spelling of "no value".
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_RejectsABlankVotingBecauseTheDictionaryListsNone()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(
            GetRange,
            MockHistoricalResponse.ZstdJsonLines(
                // Escaped rather than raw literals: the replacement ends in two quote
                // characters, which no raw string literal can close cleanly.
                FullRow.Replace("\"voting\":\"M\"", "\"voting\":\"\"", StringComparison.Ordinal)));

        await using var client = ClientFor(gateway);

        var thrown = await Assert.ThrowsAsync<JsonException>(
            async () => await DrainAsync(client.SecurityMaster.GetRangeAsync(Range(), Cancel)));

        Assert.Contains("Voting has no blank value", thrown.Message, StringComparison.Ordinal);
    }

    /* ------------------------------------------------------------------ *
     * The types the Definition of done names.
     * ------------------------------------------------------------------ */

    /// <summary>
    /// The four dates are <see cref="LocalDate"/> and the three timestamps are <see cref="Instant"/>
    /// — never a BCL <c>DateTime</c>, <c>DateOnly</c> or <c>DateTimeOffset</c>. The build already
    /// refuses those (<c>BannedSymbols.txt</c>, RS0030); what this adds is that the two NodaTime
    /// types have not been swapped for each other, which no analyzer would notice.
    /// </summary>
    [Fact]
    public void TheDatesAreLocalDatesAndTheTimestampsAreInstants()
    {
        foreach (var name in new[] { "ListingCreatedDate", "ListingDate", "DelistingDate", "SharesOutstandingDate" })
        {
            var type = typeof(SecurityMaster).GetProperty(name)!.PropertyType;
            Assert.Equal(typeof(LocalDate), Nullable.GetUnderlyingType(type) ?? type);
        }

        foreach (var name in new[] { "TsRecord", "TsEffective", "TsCreated" })
        {
            Assert.Equal(typeof(Instant), typeof(SecurityMaster).GetProperty(name)!.PropertyType);
        }
    }

    /// <summary>
    /// <c>par_value</c> and <c>vote_per_sec</c> are <see langword="decimal"/>, which #53 settled
    /// and this issue does not re-open. Asserted as behaviour rather than as a type check: a wire
    /// value of more than seventeen significant digits survives here and would not through a
    /// <see langword="double"/>. See <see cref="AdjustmentFactor.Factor"/> for the measurement.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_KeepsARateTooPreciseForBinaryFloatingPoint()
    {
        const string Precise = "1.2345678901234567890123456789";

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(
            GetRange,
            MockHistoricalResponse.ZstdJsonLines(
                FullRow
                    .Replace(""""par_value":1.5"""", $""""par_value":{Precise}"""", StringComparison.Ordinal)
                    .Replace(""""vote_per_sec":1"""", $""""vote_per_sec":{Precise}"""", StringComparison.Ordinal)));

        await using var client = ClientFor(gateway);
        var row = Assert.Single(await DrainAsync(client.SecurityMaster.GetRangeAsync(Range(), Cancel)));

        gateway.ThrowIfRejected();

        var expected = decimal.Parse(Precise, CultureInfo.InvariantCulture);
        Assert.Equal(expected, row.ParValue);
        Assert.Equal(expected, row.VotePerSec);
        Assert.Equal(Precise, row.ParValue!.Value.ToString(CultureInfo.InvariantCulture));

        // The same text through a double loses eleven digits.
        Assert.NotEqual(
            Precise,
            double.Parse(Precise, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// <c>shares_outstanding</c> is <b>not</b> a rate, so it stays upstream's <c>u64</c>: a share
    /// count above <see cref="long.MaxValue"/> is exact here and would not be through a
    /// <see langword="double"/> or a <see langword="long"/>.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_ReadsASharesOutstandingTooLargeForASignedLong()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(
            GetRange,
            MockHistoricalResponse.ZstdJsonLines(
                FullRow.Replace(
                    """"shares_outstanding":14920000"""",
                    """"shares_outstanding":18446744073709551615"""",
                    StringComparison.Ordinal)));

        await using var client = ClientFor(gateway);
        var row = Assert.Single(await DrainAsync(client.SecurityMaster.GetRangeAsync(Range(), Cancel)));

        gateway.ThrowIfRejected();
        Assert.Equal<ulong?>(ulong.MaxValue, row.SharesOutstanding);
    }

    /* ------------------------------------------------------------------ *
     * Helpers.
     * ------------------------------------------------------------------ */

    private static void AssertEveryOptionalFieldIsAbsent(SecurityMaster row)
    {
        Assert.Null(row.ListingDate);
        Assert.Null(row.DelistingDate);
        Assert.Null(row.PrimaryExchange);
        Assert.Null(row.OperatingMic);
        Assert.Null(row.Symbol);
        Assert.Null(row.NasdaqSymbol);
        Assert.Null(row.LocalCode);
        Assert.Null(row.Isin);
        Assert.Null(row.UsCode);
        Assert.Null(row.BbgCompId);
        Assert.Null(row.BbgCompTicker);
        Assert.Null(row.Figi);
        Assert.Null(row.FigiTicker);
        Assert.Null(row.Fisn);
        Assert.Null(row.Lei);
        Assert.Null(row.Sic);
        Assert.Null(row.Cik);
        Assert.Null(row.Gics);
        Assert.Null(row.Naics);
        Assert.Null(row.Cic);
        Assert.Null(row.Cfi);
        Assert.Null(row.SegmentMicName);
        Assert.Null(row.SegmentMic);
        Assert.Null(row.Structure);
        Assert.Null(row.LotSize);
        Assert.Null(row.ParValue);
        Assert.Null(row.VotePerSec);
        Assert.Null(row.SharesOutstanding);
        Assert.Null(row.SharesOutstandingDate);

        // The one closed enum among the optional fields spells absence as a Nullable; the five
        // reference codes spell it as `default`, whose HasValue is false. One way to say nothing
        // per field — see SecurityMaster's remarks.
        Assert.Null(row.Voting);

        Assert.False(row.SecurityType.HasValue);
        Assert.Null(row.SecurityType.Code);
        Assert.False(row.ListingCountry.HasValue);
        Assert.False(row.RegisterCountry.HasValue);
        Assert.False(row.TradingCurrency.HasValue);
        Assert.False(row.ParValueCurrency.HasValue);
    }

    private static SecurityMasterGetRangeParams Range() => new()
    {
        Symbols = Symbols.From("MSFT"),
        DateTimeRange = ReferenceDateTimeRange.StartingAt(Start),
    };

    private static SecurityMasterGetLastParams Last() => new()
    {
        Symbols = Symbols.From("MSFT"),
    };

    private static string RowWithTsRecord(string tsRecord) =>
        FullRow.Replace(""""ts_record":"2009-05-12T13:44:05Z"""", $""""ts_record":"{tsRecord}"""", StringComparison.Ordinal);

    private static string RowWithTsEffective(string tsEffective) =>
        FullRow.Replace(""""ts_effective":"2000-07-04T00:00:00Z"""", $""""ts_effective":"{tsEffective}"""", StringComparison.Ordinal);

    private static async Task<MockHistoricalGateway> StartAsync(string endpoint)
    {
        var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(endpoint, MockHistoricalResponse.ZstdJsonLines(FullRow));
        return gateway;
    }

    private static ReferenceClient ClientFor(MockHistoricalGateway gateway) => new()
    {
        ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey),
        BaseUrl = gateway.BaseUrl,
    };

    private static Task<List<SecurityMaster>> DrainAsync(
        ReferenceClient client,
        string endpoint,
        IReadOnlyList<Country>? countries = null,
        IReadOnlyList<SecurityType>? securityTypes = null,
        bool allocateIsins = true) =>
        DrainAsync(
            endpoint == GetLast
                ? client.SecurityMaster.GetLastAsync(
                    Last() with
                    {
                        Countries = countries,
                        SecurityTypes = securityTypes,
                        AllocateIsins = allocateIsins,
                    },
                    Cancel)
                : client.SecurityMaster.GetRangeAsync(
                    Range() with
                    {
                        Countries = countries,
                        SecurityTypes = securityTypes,
                        AllocateIsins = allocateIsins,
                    },
                    Cancel));

    private static async Task<List<SecurityMaster>> DrainAsync(IAsyncEnumerable<SecurityMaster> rows)
    {
        var drained = new List<SecurityMaster>();

        await foreach (var row in rows.WithCancellation(Cancel).ConfigureAwait(false))
        {
            drained.Add(row);
        }

        return drained;
    }
}
