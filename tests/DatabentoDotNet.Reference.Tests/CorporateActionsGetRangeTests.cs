using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Historical;
using DatabentoDotNet.Historical.Tests;
using NodaTime;

namespace DatabentoDotNet.Reference.Tests;

/// <summary>
/// Tests for <see cref="CorporateActionsClient.GetRangeAsync"/> — the form it posts, its four
/// filters, and the hundred-and-four-field row it reads back.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="CorporateActionsClientTests"/>, which covers the same class's two
/// <c>list_*</c> endpoints, because they have nothing in common but the client that reaches them:
/// a <c>POST</c> with a form body answering in zstd-framed JSON lines against two bare <c>GET</c>s
/// answering with one plain object. See <see cref="CorporateActionsClient"/>.
/// </para>
/// <para>
/// These drive a real <see cref="ReferenceClient"/> at <see cref="MockHistoricalGateway"/> over a
/// real socket, as <see cref="SecurityMasterClientTests"/> does and for the same reason: an
/// <c>HttpMessageHandler</c> stub never opens one, and half of what is under test is what
/// <see cref="HttpClient"/> itself does with a form body.
/// </para>
/// <para>
/// <b>The gateway is an independent oracle for the request and only a mirror for the response.</b>
/// It was written from Databento's HTTP documentation, before any reference client existed, and it
/// decodes the form body itself — so what <see cref="RecordedRequest.Form"/> reports is a genuine
/// second reading of what went on the wire. The response half is not: <see cref="FullRow"/>,
/// <see cref="SparseRow"/> and <see cref="NulledRow"/> are strings this file wrote, and a
/// misreading of the reference API's JSON shape would sit in both this fixture and the model
/// without either noticing. <see cref="UpstreamRow"/> is the partial exception — it is Databento's
/// own, transcribed rather than invented. Only #57 settles the rest, against real rows.
/// </para>
/// </remarks>
public class CorporateActionsGetRangeTests
{
    private const string GetRange = "corporate_actions.get_range";

    /// <summary>
    /// Upstream's own test fixture row (<c>corporate.rs:540-556</c>), transcribed verbatim — nulls,
    /// scrambled values and all, and minified rather than reflowed. A reverse split on a Nasdaq
    /// listing: thirty-eight of its hundred and four fields are <c>null</c>, its
    /// <see cref="CorporateAction.DateInfo"/> and <see cref="CorporateAction.EventInfo"/> are empty,
    /// and its <see cref="CorporateAction.RateInfo"/> carries the two keys <c>list_events</c>
    /// documents for an <c>RSPLT</c> — both with a <c>null</c> value, which is the shape that
    /// separates "the server said nothing about this" from "the server said this is not set".
    /// </summary>
    private const string UpstreamRow = """
        {"ts_record":"2023-10-10T03:37:14Z","event_unique_id":"U-40179751345-16556634","event_id":"E-9751345-RSPLT","listing_id":"L-16556634","listing_group_id":"LG-6556634","security_id":"S-4633970","issuer_id":"I-175515","event_action":"U","event":"RSPLT","event_subtype":"CONSD","event_date_label":"ex_date","event_date":"2023-10-10","event_created_date":"1929-09-30","effective_date":null,"ex_date":"2023-10-10","record_date":null,"record_date_id":"D-9751345","related_event":null,"related_event_id":null,"global_status":"A","listing_status":"L","listing_source":"M","listing_date":"2015-10-29","delisting_date":null,"issuer_name":"Borqs Technologies Inc","security_type":"EQS","security_description":"Ordinary Shares","primary_exchange":"USNASD","exchange":"USNASD","operating_mic":"XNAS","symbol":"BRQS","nasdaq_symbol":"BRQS","local_code":"BRQS","isin":"VGG1466B1452","us_code":"G1466B145","bbg_comp_id":"BBG00B9RG1J6","bbg_comp_ticker":"BRQS US","figi":"BBG00B9RG1W1","figi_ticker":"BRQS UR","listing_country":"US","register_country":"VG","trading_currency":"USD","multi_currency":false,"segment_mic_name":"Capital Market","segment_mic":"XNCM","mand_volu_flag":"M","rd_priority":1,"lot_size":100,"par_value":null,"par_value_currency":"USD","payment_date":null,"duebills_redemption_date":null,"from_date":null,"to_date":null,"registration_date":null,"start_date":null,"end_date":null,"open_date":null,"close_date":null,"start_subscription_date":null,"end_subscription_date":null,"option_election_date":null,"withdrawal_rights_from_date":null,"withdrawal_rights_to_date":null,"notification_date":null,"financial_year_end_date":null,"exp_completion_date":null,"payment_type":"S","option_id":"1","serial_id":"1","default_option_flag":true,"rate_currency":"USD","ratio_old":12.0,"ratio_new":1.0,"fraction":"U","outturn_style":"NEWO","outturn_security_type":"EQS","outturn_security_id":"S-4633970","outturn_isin":"VGG1466B1452","outturn_us_code":"G1466B145","outturn_local_code":"BRQS","outturn_bbg_comp_id":"BBG00B9RG1J6","outturn_bbg_comp_ticker":"BRQS US","outturn_figi":"BBG00B9RG1W1","outturn_figi_ticker":"BRQS UR","min_offer_qty":null,"max_offer_qty":null,"min_qualify_qty":null,"max_qualify_qty":null,"min_accept_qty":null,"max_accept_qty":null,"tender_strike_price":null,"tender_price_step":null,"option_expiry_time":null,"option_expiry_tz":null,"withdrawal_rights_flag":null,"withdrawal_rights_expiry_time":null,"withdrawal_rights_expiry_tz":null,"expiry_time":null,"expiry_tz":null,"date_info":{},"rate_info":{"par_value_old":null,"par_value_new":null},"event_info":{},"ts_created":"1970-01-01T00:00:00.000000000Z"}
        """;

    /// <summary>
    /// A dividend with every one of the hundred and four fields carrying a value, so that each is
    /// read with one at least once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A <c>DIV</c> rather than <see cref="UpstreamRow"/>'s <c>RSPLT</c>, and that is what makes
    /// the maps honest.</b> <c>corporate_actions.list_events</c> documents no
    /// <see cref="CorporateAction.DateInfo"/> or <see cref="CorporateAction.EventInfo"/> key at all
    /// for a reverse split — its only two documented keys are the <c>rate_info</c> pair upstream's
    /// row carries. A dividend documents five <c>date_info</c> keys, two <c>rate_info</c> keys and
    /// twenty-two <c>event_info</c> keys, so a populated-map fixture can draw every key it uses
    /// from the server's own documentation instead of inventing one. The keys here are real; the
    /// values are plausible.
    /// </para>
    /// <para>
    /// The sixty-six fields that are not about the event are upstream's, unchanged. The
    /// thirty-eight it leaves <c>null</c> are filled, because no fixture anywhere carries them, and
    /// they are plausible rather than authoritative — which is enough for what this row is for:
    /// proving that a field is read at all. What each field <em>means</em> is upstream's doc
    /// comment, and what a real one holds is #57's to find out.
    /// </para>
    /// </remarks>
    private const string FullRow = """
        {"ts_record":"2023-10-10T03:37:14Z","event_unique_id":"U-40179751345-16556634","event_id":"E-9751345-DIV","listing_id":"L-16556634","listing_group_id":"LG-6556634","security_id":"S-4633970","issuer_id":"I-175515","event_action":"U","event":"DIV","event_subtype":"CAPGAIN","event_date_label":"ex_date","event_date":"2023-10-10","event_created_date":"1929-09-30","effective_date":"2023-10-11","ex_date":"2023-10-10","record_date":"2023-10-11","record_date_id":"D-9751345","related_event":"DIVIF","related_event_id":"E-9751345-DIVIF","global_status":"A","listing_status":"L","listing_source":"M","listing_date":"2015-10-29","delisting_date":"2024-06-28","issuer_name":"Borqs Technologies Inc","security_type":"EQS","security_description":"Ordinary Shares","primary_exchange":"USNASD","exchange":"USNASD","operating_mic":"XNAS","symbol":"BRQS","nasdaq_symbol":"BRQS","local_code":"BRQS","isin":"VGG1466B1452","us_code":"G1466B145","bbg_comp_id":"BBG00B9RG1J6","bbg_comp_ticker":"BRQS US","figi":"BBG00B9RG1W1","figi_ticker":"BRQS UR","listing_country":"US","register_country":"VG","trading_currency":"USD","multi_currency":true,"segment_mic_name":"Capital Market","segment_mic":"XNCM","mand_volu_flag":"M","rd_priority":1,"lot_size":100,"par_value":0.0001,"par_value_currency":"USD","payment_date":"2023-10-25","duebills_redemption_date":"2023-10-13","from_date":"2023-10-02","to_date":"2023-10-31","registration_date":"2023-10-12","start_date":"2023-10-10","end_date":"2023-10-24","open_date":"2023-10-10","close_date":"2023-10-24","start_subscription_date":"2023-10-10","end_subscription_date":"2023-10-20","option_election_date":"2023-10-18","withdrawal_rights_from_date":"2023-10-11","withdrawal_rights_to_date":"2023-10-19","notification_date":"2023-09-28","financial_year_end_date":"2023-12-31","exp_completion_date":"2023-10-26","payment_type":"S","option_id":"1","serial_id":"1","default_option_flag":true,"rate_currency":"USD","ratio_old":12.0,"ratio_new":1.0,"fraction":"U","outturn_style":"NEWO","outturn_security_type":"EQS","outturn_security_id":"S-4633970","outturn_isin":"VGG1466B1452","outturn_us_code":"G1466B145","outturn_local_code":"BRQS","outturn_bbg_comp_id":"BBG00B9RG1J6","outturn_bbg_comp_ticker":"BRQS US","outturn_figi":"BBG00B9RG1W1","outturn_figi_ticker":"BRQS UR","min_offer_qty":100,"max_offer_qty":1000000,"min_qualify_qty":1,"max_qualify_qty":18446744073709551615,"min_accept_qty":500,"max_accept_qty":2000000,"tender_strike_price":12.75,"tender_price_step":0.05,"option_expiry_time":"16:00:00","option_expiry_tz":"America/New_York","withdrawal_rights_flag":true,"withdrawal_rights_expiry_time":"17:00:00","withdrawal_rights_expiry_tz":"America/New_York","expiry_time":"21:00:00","expiry_tz":"UTC","date_info":{"declaration_date":"2023-09-28T00:00:00Z","periodend_date":"2023-09-30T00:00:00Z","foreign_ex_date":"2023-10-09T00:00:00Z","ex_date2":"2023-10-10T00:00:00Z","pay_date2":"2023-10-25T00:00:00Z"},"rate_info":{"gross_dividend":0.145,"net_dividend":0.12325},"event_info":{"marker":"C","frequency":"QTR","declared_currency":"USD","declared_gross_amount":"0.145","tax_rate":"15"},"ts_created":"2023-10-10T03:37:14.123456789Z"}
        """;

    /// <summary>The twenty-three required fields and nothing else — the eighty-one optional ones absent.</summary>
    /// <remarks>
    /// The three maps are among the twenty-three: they are required upstream, so they appear here as
    /// <c>{}</c> rather than being left out. <see cref="GetRangeAsync_RefusesARowWithAMissingMap"/>
    /// is the row that leaves one out.
    /// </remarks>
    private const string SparseRow = """
        {"ts_record":"2024-05-01T12:00:00Z","event_unique_id":"U-40179751345-16556634","event_id":"E-9751345-RSPLT","listing_id":"L-16556634","listing_group_id":"LG-6556634","security_id":"S-4633970","issuer_id":"I-175515","event_action":"U","event":"RSPLT","event_date_label":"ex_date","event_created_date":"2024-04-01","global_status":"A","listing_status":"L","listing_source":"M","issuer_name":"Borqs Technologies Inc","security_description":"Ordinary Shares","exchange":"USNASD","multi_currency":false,"mand_volu_flag":"M","date_info":{},"rate_info":{},"event_info":{},"ts_created":"2024-05-01 12:00:00"}
        """;

    /// <summary>The same twenty-three, with all eighty-one optional fields present and explicitly <c>null</c>.</summary>
    private const string NulledRow = """
        {"ts_record":"2024-05-01T12:00:00Z","event_unique_id":"U-40179751345-16556634","event_id":"E-9751345-RSPLT","listing_id":"L-16556634","listing_group_id":"LG-6556634","security_id":"S-4633970","issuer_id":"I-175515","event_action":"U","event":"RSPLT","event_subtype":null,"event_date_label":"ex_date","event_date":null,"event_created_date":"2024-04-01","effective_date":null,"ex_date":null,"record_date":null,"record_date_id":null,"related_event":null,"related_event_id":null,"global_status":"A","listing_status":"L","listing_source":"M","listing_date":null,"delisting_date":null,"issuer_name":"Borqs Technologies Inc","security_type":null,"security_description":"Ordinary Shares","primary_exchange":null,"exchange":"USNASD","operating_mic":null,"symbol":null,"nasdaq_symbol":null,"local_code":null,"isin":null,"us_code":null,"bbg_comp_id":null,"bbg_comp_ticker":null,"figi":null,"figi_ticker":null,"listing_country":null,"register_country":null,"trading_currency":null,"multi_currency":false,"segment_mic_name":null,"segment_mic":null,"mand_volu_flag":"M","rd_priority":null,"lot_size":null,"par_value":null,"par_value_currency":null,"payment_date":null,"duebills_redemption_date":null,"from_date":null,"to_date":null,"registration_date":null,"start_date":null,"end_date":null,"open_date":null,"close_date":null,"start_subscription_date":null,"end_subscription_date":null,"option_election_date":null,"withdrawal_rights_from_date":null,"withdrawal_rights_to_date":null,"notification_date":null,"financial_year_end_date":null,"exp_completion_date":null,"payment_type":null,"option_id":null,"serial_id":null,"default_option_flag":null,"rate_currency":null,"ratio_old":null,"ratio_new":null,"fraction":null,"outturn_style":null,"outturn_security_type":null,"outturn_security_id":null,"outturn_isin":null,"outturn_us_code":null,"outturn_local_code":null,"outturn_bbg_comp_id":null,"outturn_bbg_comp_ticker":null,"outturn_figi":null,"outturn_figi_ticker":null,"min_offer_qty":null,"max_offer_qty":null,"min_qualify_qty":null,"max_qualify_qty":null,"min_accept_qty":null,"max_accept_qty":null,"tender_strike_price":null,"tender_price_step":null,"option_expiry_time":null,"option_expiry_tz":null,"withdrawal_rights_flag":null,"withdrawal_rights_expiry_time":null,"withdrawal_rights_expiry_tz":null,"expiry_time":null,"expiry_tz":null,"date_info":{},"rate_info":{},"event_info":{},"ts_created":"2024-05-01T12:00:00Z"}
        """;

    /// <summary>
    /// <c>2023-10-10T00:00:00Z</c> in Unix nanoseconds — the start upstream's own test uses
    /// (<c>corporate.rs:534</c>). Written as the integer rather than derived from <see cref="Start"/>,
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

    [Fact]
    public async Task GetRangeAsync_PostsToItsVersionedSlug()
    {
        await using var gateway = await StartAsync();
        await using var client = ClientFor(gateway);
        await DrainAsync(client.CorporateActions.GetRangeAsync(Range(), Cancel));

        gateway.ThrowIfRejected();
        var recorded = Assert.Single(gateway.Requests);
        Assert.Equal("POST", recorded.Method);
        Assert.Equal("/v0/" + GetRange, recorded.Path);
    }

    /// <summary>
    /// <b>The Definition of done's first assertion.</b> An unfiltered open range sends six fields
    /// and no more. <c>end</c> and all four filters are <em>absent</em>, not empty — <c>events=</c>
    /// is a different request from no <c>events</c> at all.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_SendsExactlySixFieldsForAnUnfilteredOpenRange()
    {
        await using var gateway = await StartAsync();
        await using var client = ClientFor(gateway);
        await DrainAsync(client.CorporateActions.GetRangeAsync(Range(), Cancel));

        gateway.ThrowIfRejected();
        var form = Assert.Single(gateway.Requests).Form;

        Assert.Equal(
            ["allocate_isins", "compression", "index", "start", "stype_in", "symbols"],
            form.Keys.OrderBy(key => key, StringComparer.Ordinal));

        Assert.Equal("event_date", form["index"]);
        Assert.Equal("raw_symbol", form["stype_in"]);
        Assert.Equal("MSFT", form["symbols"]);
        Assert.Equal("true", form["allocate_isins"]);
        Assert.Equal("zstd", form["compression"]);
        Assert.Equal(StartUnixNanoseconds, long.Parse(form["start"], CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// <b>The Definition of done's second assertion.</b> One event, one country, one exchange and
    /// one security type add exactly four keys to the six above — not three, not five, and none of
    /// the six changes value.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_AddsExactlyFourKeysForOneOfEachFilter()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetRange, MockHistoricalResponse.ZstdJsonLines(FullRow));

        await using var client = ClientFor(gateway);
        await DrainAsync(client.CorporateActions.GetRangeAsync(Range(), Cancel));
        await DrainAsync(client.CorporateActions.GetRangeAsync(
            Range() with
            {
                Events = [Event.From("DIV")],
                Countries = [Country.From("US")],
                Exchanges = ["USNASD"],
                SecurityTypes = [SecurityType.From("EQS")],
            },
            Cancel));

        gateway.ThrowIfRejected();
        Assert.Equal(2, gateway.Requests.Count);

        var unfiltered = gateway.Requests[0].Form;
        var filtered = gateway.Requests[1].Form;

        Assert.Equal(
            ["countries", "events", "exchanges", "security_types"],
            filtered.Keys.Except(unfiltered.Keys, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal));
        Assert.Empty(unfiltered.Keys.Except(filtered.Keys, StringComparer.Ordinal));

        // Every key they share carries the same value too, so "it adds four keys" is a claim about
        // the whole body rather than only about its key set.
        foreach (var key in unfiltered.Keys)
        {
            Assert.Equal(unfiltered[key], filtered[key]);
        }

        Assert.Equal("DIV", filtered["events"]);
        Assert.Equal("US", filtered["countries"]);
        Assert.Equal("USNASD", filtered["exchanges"]);
        Assert.Equal("EQS", filtered["security_types"]);
    }

    /// <summary>
    /// <b>The Definition of done's third assertion</b> — each of the four is comma-joined, and the
    /// order is the caller's rather than sorted.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_JoinsEachFilterWithCommas()
    {
        await using var gateway = await StartAsync();
        await using var client = ClientFor(gateway);
        await DrainAsync(client.CorporateActions.GetRangeAsync(
            Range() with
            {
                Events = [Event.From("DIV"), Event.From("RSPLT")],
                Countries = [Country.From("US"), Country.From("CA")],
                Exchanges = ["USNASD", "USNYSE"],
                SecurityTypes = [SecurityType.From("EQS"), SecurityType.From("PRF")],
            },
            Cancel));

        gateway.ThrowIfRejected();
        var form = Assert.Single(gateway.Requests).Form;

        Assert.Equal("DIV,RSPLT", form["events"]);
        Assert.Equal("US,CA", form["countries"]);
        Assert.Equal("USNASD,USNYSE", form["exchanges"]);
        Assert.Equal("EQS,PRF", form["security_types"]);
    }

    /// <summary>
    /// An empty list means the same as <see langword="null"/>: the parameter is left out. Upstream's
    /// <c>AddToForm</c> impls push nothing for an empty <c>Vec</c>, and a caller who built a filter
    /// list conditionally should not accidentally send <c>events=</c>.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_OmitsAFilterWhoseListIsEmpty()
    {
        await using var gateway = await StartAsync();
        await using var client = ClientFor(gateway);
        await DrainAsync(client.CorporateActions.GetRangeAsync(
            Range() with { Events = [], Countries = [], Exchanges = [], SecurityTypes = [] },
            Cancel));

        gateway.ThrowIfRejected();
        var form = Assert.Single(gateway.Requests).Form;

        Assert.Equal(
            ["allocate_isins", "compression", "index", "start", "stype_in", "symbols"],
            form.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task GetRangeAsync_SendsTheEndOnlyWhenTheRangeIsClosed()
    {
        await using var gateway = await StartAsync();
        await using var client = ClientFor(gateway);
        await DrainAsync(client.CorporateActions.GetRangeAsync(
            Range() with { DateTimeRange = ReferenceDateTimeRange.Between(Start, End) },
            Cancel));

        gateway.ThrowIfRejected();
        var form = Assert.Single(gateway.Requests).Form;

        Assert.Equal(StartUnixNanoseconds, long.Parse(form["start"], CultureInfo.InvariantCulture));
        Assert.Equal(EndUnixNanoseconds, long.Parse(form["end"], CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(CorporateActionIndex.EventDate, "event_date")]
    [InlineData(CorporateActionIndex.ExDate, "ex_date")]
    [InlineData(CorporateActionIndex.TsRecord, "ts_record")]
    public async Task GetRangeAsync_SendsTheIndexAsItsWireString(CorporateActionIndex index, string expected)
    {
        await using var gateway = await StartAsync();
        await using var client = ClientFor(gateway);
        await DrainAsync(client.CorporateActions.GetRangeAsync(Range() with { Index = index }, Cancel));

        gateway.ThrowIfRejected();
        Assert.Equal(expected, Assert.Single(gateway.Requests).Form["index"]);
    }

    /// <summary>
    /// Lower case, as upstream's <c>bool::to_string</c> is. <c>bool.ToString()</c> would send
    /// <c>True</c>, which is invisible in C# and load-bearing on the wire.
    /// </summary>
    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public async Task GetRangeAsync_SendsAllocateIsinsInLowerCase(bool allocateIsins, string expected)
    {
        await using var gateway = await StartAsync();
        await using var client = ClientFor(gateway);
        await DrainAsync(client.CorporateActions.GetRangeAsync(
            Range() with { AllocateIsins = allocateIsins }, Cancel));

        gateway.ThrowIfRejected();
        Assert.Equal(expected, Assert.Single(gateway.Requests).Form["allocate_isins"]);
    }

    /// <summary>
    /// <b>The porting note's case, and the reason <see cref="Event"/> is not a C# enum.</b> A caller
    /// who read an unrecognised event code out of a response can turn round and filter on it, and
    /// the code reaches the server verbatim. A plain enum would have had nothing to put in the list.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_SendsAnEventCodeThisLibraryHasNeverSeen()
    {
        await using var gateway = await StartAsync();
        await using var client = ClientFor(gateway);
        await DrainAsync(client.CorporateActions.GetRangeAsync(
            Range() with { Events = [Event.From("ZZZQQ"), Event.From("DIV")] }, Cancel));

        gateway.ThrowIfRejected();
        Assert.Equal("ZZZQQ,DIV", Assert.Single(gateway.Requests).Form["events"]);
        Assert.False(Event.From("ZZZQQ").IsKnown);
    }

    /// <summary>
    /// A <see langword="default"/> in a filter list is a caller mistake rather than an empty filter:
    /// dropping it would silently widen the query and sending it would produce a stray comma.
    /// </summary>
    [Fact]
    public void ToFormParameters_RefusesADefaultCodeInAFilter()
    {
        Assert.Throws<ArgumentException>(() =>
            (Range() with { Events = [default] }).ToFormParameters());
        Assert.Throws<ArgumentException>(() =>
            (Range() with { Countries = [default] }).ToFormParameters());
        Assert.Throws<ArgumentException>(() =>
            (Range() with { SecurityTypes = [default] }).ToFormParameters());
    }

    /// <summary>
    /// The same rule for the one filter whose values are not codes. Upstream joins without checking,
    /// which is behaviour rather than contract — it has no empty <c>String</c> in its own tests.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ToFormParameters_RefusesABlankExchange(string exchange)
    {
        Assert.Throws<ArgumentException>(() =>
            (Range() with { Exchanges = ["USNASD", exchange] }).ToFormParameters());
    }

    /// <summary>
    /// <see langword="required"/> forces a caller to assign the property but does not stop them
    /// assigning <see langword="default"/>, and the accessors this reads refuse to render one.
    /// </summary>
    [Fact]
    public void ToFormParameters_RefusesADefaultRangeOrSymbols()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new CorporateActionsGetRangeParams
            {
                Symbols = Symbols.From("MSFT"),
                DateTimeRange = default,
            }.ToFormParameters());

        Assert.Throws<InvalidOperationException>(() =>
            new CorporateActionsGetRangeParams
            {
                Symbols = default,
                DateTimeRange = ReferenceDateTimeRange.StartingAt(Start),
            }.ToFormParameters());
    }

    [Fact]
    public async Task GetRangeAsync_RefusesNullParameters()
    {
        await using var gateway = await StartAsync();
        await using var client = ClientFor(gateway);

        Assert.Throws<ArgumentNullException>(() => client.CorporateActions.GetRangeAsync(null!, Cancel));
    }

    /// <summary>
    /// <b>A caller who never enumerates never bills.</b> Building the query sends nothing; the
    /// request goes out on the first <c>MoveNextAsync</c>. That is what makes it safe for the
    /// argument checks above to run at the call rather than at the first step.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_SendsNothingUntilTheEnumerationStarts()
    {
        await using var gateway = await StartAsync();
        await using var client = ClientFor(gateway);

        var rows = client.CorporateActions.GetRangeAsync(Range(), Cancel);
        Assert.Empty(gateway.Requests);

        await DrainAsync(rows);
        Assert.Single(gateway.Requests);
    }

    /* ------------------------------------------------------------------ *
     * What comes back: the hundred and four fields.
     * ------------------------------------------------------------------ */

    /// <summary>
    /// Upstream's own fixture row, asserted on the six values upstream's own test asserts on
    /// (<c>corporate.rs:597-604</c>) — so a disagreement here is a disagreement with Databento's
    /// client rather than with this file.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_ReadsUpstreamsOwnFixtureRow()
    {
        var row = await SingleRowAsync(UpstreamRow);

        Assert.Equal("RSPLT", row.Event.Code);
        Assert.Equal(Action.Updated, row.EventAction);
        Assert.Equal(GlobalStatus.Active, row.GlobalStatus);
        Assert.Equal(new LocalDate(2015, 10, 29), row.ListingDate);
        Assert.Equal("EQS", row.SecurityType.Code);
        Assert.Equal("VG", row.RegisterCountry.Code);
        Assert.True(row.RateInfo.ContainsKey("par_value_old"));
        Assert.True(row.RateInfo.ContainsKey("par_value_new"));
    }

    /// <summary>
    /// <b>The Definition of done's "all 104 fields deserialize", asserted as a property of the
    /// whole model rather than as a hundred and four hand-written lines.</b>
    /// <see cref="FullRow"/> is built so that no field's value is its type's default, so a property
    /// that silently failed to bind — a renamed C# property, a converter that answered
    /// <see langword="null"/>, a field left out of the model — shows up as a default here and is
    /// named in the failure message. The spot assertions below then check that the values are the
    /// right ones and not merely present.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_BindsEveryOneOfTheHundredAndFourFields()
    {
        var row = await SingleRowAsync(FullRow);

        var properties = typeof(CorporateAction).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.Equal(104, properties.Length);

        var unbound = properties
            .Where(property => IsDefaultOrEmpty(property.GetValue(row)))
            .Select(property => property.Name)
            .ToList();

        Assert.Empty(unbound);
    }

    /// <summary>
    /// One assertion per distinct C# type on the record, so that "it bound" above is backed by "it
    /// bound to the right value" for every conversion the model performs.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_ReadsOneFieldOfEveryTypeCorrectly()
    {
        var row = await SingleRowAsync(FullRow);

        // The two timestamps, one of them with nanosecond precision a DateTime tick cannot hold.
        Assert.Equal(Instant.FromUtc(2023, 10, 10, 3, 37, 14), row.TsRecord);
        Assert.Equal(
            Instant.FromUtc(2023, 10, 10, 3, 37, 14) + Duration.FromNanoseconds(123_456_789L),
            row.TsCreated);

        Assert.Equal(new LocalDate(2023, 10, 10), row.EventDate);
        Assert.Equal("U-40179751345-16556634", row.EventUniqueId);
        Assert.True(row.MultiCurrency);
        Assert.Equal<uint?>(1, row.RdPriority);
        Assert.Equal<ulong?>(100, row.MinOfferQty);
        Assert.Equal(0.0001m, row.ParValue);

        // The closed enums, including the two that a blank is legal for.
        Assert.Equal(Action.Updated, row.EventAction);
        Assert.Equal(GlobalStatus.Active, row.GlobalStatus);
        Assert.Equal(ListingStatus.Listed, row.ListingStatus);
        Assert.Equal(ListingSource.Main, row.ListingSource);
        Assert.Equal(MandVolu.Mandatory, row.MandVoluFlag);
        Assert.NotNull(row.PaymentType);
        Assert.NotNull(row.Fraction);

        // The open carriers, including the two Events and the two SecurityTypes.
        Assert.Equal("DIV", row.Event.Code);
        Assert.Equal("DIVIF", row.RelatedEvent.Code);
        Assert.Equal("CAPGAIN", row.EventSubtype.Code);
        Assert.Equal("EQS", row.SecurityType.Code);
        Assert.Equal("EQS", row.OutturnSecurityType.Code);
        Assert.Equal("NEWO", row.OutturnStyle.Code);
        Assert.Equal("US", row.ListingCountry.Code);
        Assert.Equal("USD", row.TradingCurrency.Code);

        // And the three maps.
        Assert.Equal(Instant.FromUtc(2023, 9, 28, 0, 0), row.DateInfo["declaration_date"]);
        Assert.Equal(0.145m, row.RateInfo["gross_dividend"]);
        Assert.Equal("QTR", row.EventInfo["frequency"]);
    }

    /// <summary>
    /// <b>Every property has to match a wire field, and every wire field a property.</b> The model
    /// carries no <c>[JsonPropertyName]</c> at all, so a C# property renamed by a refactoring would
    /// quietly stop matching its field and read as <see langword="null"/> forever. This compares the
    /// hundred and four names the naming policy produces against the hundred and four keys
    /// Databento's own fixture row carries, in both directions.
    /// </summary>
    [Fact]
    public void EveryPropertyNamesAWireFieldOfUpstreamsOwnRow()
    {
        using var document = JsonDocument.Parse(UpstreamRow);
        var wire = document.RootElement.EnumerateObject().Select(field => field.Name).ToHashSet(StringComparer.Ordinal);

        var modelled = typeof(CorporateAction)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(104, wire.Count);
        Assert.Equal(104, modelled.Count);
        Assert.Empty(modelled.Except(wire, StringComparer.Ordinal));
        Assert.Empty(wire.Except(modelled, StringComparer.Ordinal));
    }

    /// <summary>
    /// <b>The Definition of done's date and timestamp counts, asserted against the model rather than
    /// read off it.</b> Twenty-four dates and two timestamps, none of them a banned BCL type —
    /// <c>BannedSymbols.txt</c> already fails the build for one in the source, and this fails the
    /// test suite for one that arrived some other way.
    /// </summary>
    [Fact]
    public void CarriesTwentyFourLocalDatesAndTwoInstantsAndNoBclDateType()
    {
        var types = typeof(CorporateAction)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.PropertyType)
            .ToList();

        Assert.Equal(24, types.Count(type => type == typeof(LocalDate) || type == typeof(LocalDate?)));
        Assert.Equal(2, types.Count(type => type == typeof(Instant)));

        Assert.DoesNotContain(types, type =>
            (Nullable.GetUnderlyingType(type) ?? type).FullName is
                "System.DateTime" or "System.DateTimeOffset" or "System.DateOnly"
                or "System.TimeOnly" or "System.TimeSpan");
    }

    /// <summary>
    /// The twenty-three required fields and nothing else. Every optional field reads as absent, in
    /// whichever of the three spellings its type uses.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_ReadsARowWithEveryOptionalFieldAbsent()
    {
        var row = await SingleRowAsync(SparseRow);

        AssertEveryOptionalFieldIsAbsent(row);
        Assert.Equal("RSPLT", row.Event.Code);
        Assert.Equal(Instant.FromUtc(2024, 5, 1, 12, 0), row.TsRecord);
        Assert.Equal(new LocalDate(2024, 4, 1), row.EventCreatedDate);
    }

    /// <summary>
    /// The same twenty-three, with all eighty-one optional fields present and explicitly
    /// <c>null</c>. <b>An absent field and an explicit <c>null</c> mean the same thing</b>, which is
    /// worth pinning because the two reach <see cref="System.Text.Json"/> by different paths — one
    /// never calls the converter at all, the other hands it a null token.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_ReadsAnExplicitNullExactlyAsItReadsAnAbsentField()
    {
        var absent = await SingleRowAsync(SparseRow);
        var nulled = await SingleRowAsync(NulledRow);

        AssertEveryOptionalFieldIsAbsent(nulled);

        // Property by property rather than by record equality: the three maps are reference-typed,
        // so the compiler-generated Equals compares two distinct empty dictionaries by reference and
        // would report the rows unequal for a reason that has nothing to do with what is under test.
        // They are asserted empty on both rows by AssertEveryOptionalFieldIsAbsent's caller instead.
        foreach (var property in typeof(CorporateAction).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.Name is nameof(CorporateAction.DateInfo)
                or nameof(CorporateAction.RateInfo)
                or nameof(CorporateAction.EventInfo))
            {
                continue;
            }

            Assert.Equal(property.GetValue(absent), property.GetValue(nulled));
        }

        Assert.Empty(absent.DateInfo);
        Assert.Empty(nulled.DateInfo);
    }

    /* ------------------------------------------------------------------ *
     * The three open maps.
     * ------------------------------------------------------------------ */

    /// <summary>
    /// <b>The Definition of done's open-map assertion, and the whole point of an open map.</b> A
    /// <c>date_info</c> key no fixture in this repository carries — and that
    /// <c>corporate_actions.list_events</c> does not document for any event — still deserializes,
    /// and both the key and its value reach the caller. A model that typed these three as fixed
    /// records would have dropped it.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_KeepsADateInfoKeyItHasNeverSeen()
    {
        var row = await SingleRowAsync(RowWith(
            """"date_info":{}"""",
            """"date_info":{"some_future_date":"2031-04-02T09:15:00Z"}""""));

        var entry = Assert.Single(row.DateInfo);
        Assert.Equal("some_future_date", entry.Key);
        Assert.Equal(Instant.FromUtc(2031, 4, 2, 9, 15), entry.Value);
    }

    /// <summary>
    /// <b>A key carrying <c>null</c> is a value, not an absence.</b> "This event has a
    /// <c>declaration_date</c> and it is not yet set" is a different statement from "this event has
    /// no <c>declaration_date</c>", and a caller can tell them apart with <c>ContainsKey</c> — which
    /// is exactly what upstream's <c>HashMap&lt;String, Option&lt;T&gt;&gt;</c> expresses.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_DistinguishesAKeyCarryingNullFromAnAbsentKey()
    {
        var row = await SingleRowAsync(RowWith(
            """"date_info":{}"""",
            """"date_info":{"declaration_date":null}""""));

        Assert.True(row.DateInfo.ContainsKey("declaration_date"));
        Assert.Null(row.DateInfo["declaration_date"]);

        Assert.False(row.DateInfo.ContainsKey("periodend_date"));
        Assert.False(row.DateInfo.TryGetValue("periodend_date", out _));

        // Upstream's own fixture makes the same point on the other map, and it is asserted here so
        // that the claim rests on Databento's row rather than only on one this file wrote.
        var upstream = await SingleRowAsync(UpstreamRow);
        Assert.True(upstream.RateInfo.ContainsKey("par_value_old"));
        Assert.Null(upstream.RateInfo["par_value_old"]);
    }

    /// <summary>
    /// <b>A missing map is not an empty map.</b> Upstream declares no <c>#[serde(default)]</c>
    /// anywhere in <c>corporate.rs</c>, so all three are required fields there and a row without one
    /// fails to deserialize; <see langword="required"/> makes it fail here too, rather than handing
    /// a caller an empty dictionary the server never sent.
    /// </summary>
    [Theory]
    [InlineData(""""date_info":{},"""")]
    [InlineData(""""rate_info":{"par_value_old":null,"par_value_new":null},"""")]
    [InlineData(""""event_info":{},"""")]
    public async Task GetRangeAsync_RefusesARowWithAMissingMap(string map)
    {
        await Assert.ThrowsAsync<JsonException>(() => SingleRowAsync(RowWith(map, string.Empty)));
    }

    /// <summary>An empty map, by contrast, is an ordinary answer and by far the commonest one.</summary>
    [Fact]
    public async Task GetRangeAsync_ReadsAnEmptyMapAsAnEmptyDictionary()
    {
        var row = await SingleRowAsync(UpstreamRow);

        Assert.Empty(row.DateInfo);
        Assert.Empty(row.EventInfo);
        Assert.Equal(2, row.RateInfo.Count);
    }

    /// <summary>
    /// Map keys arrive as the server wrote them and are matched ordinally: the naming policy
    /// governs the model's property names and never a dictionary key, and nothing case-folds them.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_MatchesMapKeysOrdinally()
    {
        var row = await SingleRowAsync(FullRow);

        Assert.True(row.EventInfo.ContainsKey("declared_currency"));
        Assert.False(row.EventInfo.ContainsKey("DECLARED_CURRENCY"));
        Assert.False(row.EventInfo.ContainsKey("DeclaredCurrency"));
    }

    /// <summary>
    /// <b>The one place this library reads a wider set of spellings than upstream, stated as a
    /// test.</b> Upstream parses <c>date_info</c> with an ISO-8601-only deserializer while its two
    /// fixed timestamps also accept a legacy space-separated form
    /// (<c>databento-rs/src/deserialize.rs:7-53</c>). That asymmetry looks like an oversight rather
    /// than a rule — the two formats are mutually unambiguous, so accepting both cannot change how
    /// any value reads, only whether a row is rejected. One converter serves both here, and this
    /// pins the consequence so it stays a decision rather than an accident.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_ReadsADateInfoTimestampInTheLegacySpellingUpstreamRejects()
    {
        var row = await SingleRowAsync(RowWith(
            """"date_info":{}"""",
            """"date_info":{"declaration_date":"2023-09-28 14:30:00"}""""));

        Assert.Equal(Instant.FromUtc(2023, 9, 28, 14, 30), row.DateInfo["declaration_date"]);
    }

    /* ------------------------------------------------------------------ *
     * Odds and ends the model has to get right.
     * ------------------------------------------------------------------ */

    /// <summary>
    /// <b>Upstream's second test, ported (<c>corporate.rs:687-716</c>) — and it lands differently
    /// here, which is the interesting part.</b> Upstream's fixture picks <c>CORR</c> precisely
    /// because <em>its</em> <c>Event</c> enum has no such variant, and asserts the row reads back as
    /// <c>Event::Unknown("CORR")</c>. This library knows <c>CORR</c>: its table is generated from
    /// the server's own <c>EVENT</c> dictionary group, which carries 141 codes to upstream's
    /// hand-written 60, and "Correction" is one of the 81 upstream lacks. So the row still reads
    /// back as <c>CORR</c> — the assertion that matters — but as a recognised code rather than an
    /// opaque one. That gap is exactly what #50 and #51 exist for; see
    /// <c>tests/DatabentoDotNet.Reference.Tests/Data/README.md</c>.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_ReadsARelatedEventCodeUpstreamDoesNotModel()
    {
        var row = await SingleRowAsync(RowWith(""""related_event":null,"""", """"related_event":"CORR",""""));

        Assert.Equal("CORR", row.RelatedEvent.Code);
        Assert.True(row.RelatedEvent.IsKnown);
    }

    /// <summary>
    /// And the open-carrier behaviour itself, on a code <em>neither</em> library knows: it arrives
    /// verbatim with <c>IsKnown</c> <see langword="false"/> rather than failing the row. A plain C#
    /// <see langword="enum"/> would have had to reject it or collapse it onto a
    /// <see langword="default"/>, and a caller could then not filter on it — see
    /// <see cref="GetRangeAsync_SendsAnEventCodeThisLibraryHasNeverSeen"/>, which is the other half
    /// of the same round trip.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_KeepsAnEventCodeNoDictionaryCarriesVerbatim()
    {
        var row = await SingleRowAsync(RowWith(""""related_event":null,"""", """"related_event":"ZZZQQ",""""));

        Assert.Equal("ZZZQQ", row.RelatedEvent.Code);
        Assert.False(row.RelatedEvent.IsKnown);
    }

    /// <summary>
    /// <b>A blank is a legal value for exactly two of the nine closed enums, and this proves the
    /// property-level converters that read it are actually reached.</b> The <c>FRACCD</c> and
    /// <c>PAYTYPE</c> groups of <c>corporate_actions.list_enums</c> each carry a null-code entry, so
    /// <c>""</c> means "no value" for these two fields and an exception for the other seven.
    /// <see cref="NullableFractionJsonConverter"/> anticipated that the source generator might not
    /// honour a <c>[JsonConverter]</c> on a property and might need registering in the context's
    /// options instead; it does honour it, and this is what says so — without it the non-nullable
    /// <see cref="FractionJsonConverter"/> would be reached and would throw on the empty string.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_ReadsABlankFractionAndPaymentTypeAsNoValue()
    {
        var row = await SingleRowAsync(
            RowWith(""""fraction":"U","""", """"fraction":"","""")
                .Replace(""""payment_type":"S","""", """"payment_type":"","""", StringComparison.Ordinal));

        Assert.Null(row.Fraction);
        Assert.Null(row.PaymentType);
    }

    /// <summary>
    /// <b>#52's decision, restated for the third and last endpoint it applies to.</b> Upstream sorts
    /// its buffered response by whichever date the index names; a stream has no buffer to
    /// rearrange, so rows arrive in the server's order. Three rows served in descending
    /// <c>event_date</c> come back descending.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_DoesNotSortRowsByTheIndex()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(
            GetRange,
            MockHistoricalResponse.ZstdJsonLines(
                RowWith(""""event_date":"2023-10-10","""", """"event_date":"2023-12-31",""""),
                RowWith(""""event_date":"2023-10-10","""", """"event_date":"2023-06-15",""""),
                RowWith(""""event_date":"2023-10-10","""", """"event_date":"2023-01-02","""")));

        await using var client = ClientFor(gateway);
        var rows = await DrainAsync(client.CorporateActions.GetRangeAsync(
            Range() with { Index = CorporateActionIndex.EventDate }, Cancel));

        gateway.ThrowIfRejected();
        Assert.Equal(
            [new LocalDate(2023, 12, 31), new LocalDate(2023, 6, 15), new LocalDate(2023, 1, 2)],
            rows.Select(row => row.EventDate));
    }

    /// <summary>
    /// The six quantity fields stay upstream's <c>u64</c>: a quantity above
    /// <see cref="long.MaxValue"/> is exact here and would not be through a <see langword="double"/>
    /// or a <see langword="long"/>.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_ReadsAQuantityTooLargeForASignedLong()
    {
        var row = await SingleRowAsync(FullRow);

        Assert.Equal<ulong?>(ulong.MaxValue, row.MaxQualifyQty);
    }

    /// <summary>
    /// <b>A rate that <see langword="double"/> cannot hold, in a fixed column and in
    /// <see cref="CorporateAction.RateInfo"/> alike.</b> #53 chose <see langword="decimal"/> for
    /// every rate in these three models and a map of rates is still rates, so the map's values get
    /// the same guarantee its neighbours do.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_ReadsRatesAtDecimalPrecision()
    {
        const string Precise = "1.0000000000000000000000000001";

        var row = await SingleRowAsync(
            FullRowWith(""""ratio_old":12.0,"""", $""""ratio_old":{Precise},"""")
                .Replace(""""gross_dividend":0.145,"""", $""""gross_dividend":{Precise},"""", StringComparison.Ordinal));

        Assert.Equal(decimal.Parse(Precise, CultureInfo.InvariantCulture), row.RatioOld);
        Assert.Equal(decimal.Parse(Precise, CultureInfo.InvariantCulture), row.RateInfo["gross_dividend"]);

        // The same literal through a double, which is what upstream reads it as, loses everything
        // after the leading 1 — the measurement AdjustmentFactor.Factor carries, restated here for
        // the map because nothing else in the repository covers a decimal inside one.
        Assert.NotEqual(
            Precise,
            double.Parse(Precise, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));
    }

    /* ------------------------------------------------------------------ *
     * Helpers.
     * ------------------------------------------------------------------ */

    /// <summary>
    /// Asserts that every property not marked <see langword="required"/> reads as absent, in
    /// whichever of the three spellings its type uses.
    /// </summary>
    /// <remarks>
    /// <b>Driven off <c>required</c> rather than off a hand-kept list of eighty-one names.</b> The
    /// compiler emits <see cref="System.Runtime.CompilerServices.RequiredMemberAttribute"/> on every
    /// <see langword="required"/> member, so this partition is the model's own and cannot drift from
    /// it — a field that changes optionality changes which half it is checked in, with no edit here.
    /// It is the exact complement of the predicate
    /// <see cref="GetRangeAsync_BindsEveryOneOfTheHundredAndFourFields"/> applies to a full row, run
    /// against the other twenty-three.
    /// </remarks>
    private static void AssertEveryOptionalFieldIsAbsent(CorporateAction row)
    {
        var optional = typeof(CorporateAction)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetCustomAttribute<RequiredMemberAttribute>() is null)
            .ToList();

        Assert.Equal(81, optional.Count);
        Assert.Empty(optional
            .Where(property => !IsDefaultOrEmpty(property.GetValue(row)))
            .Select(property => property.Name));
    }

    /// <summary>
    /// Whether a property value is its type's "nothing here" — <see langword="null"/>, an empty
    /// collection or string, or a struct left at <see langword="default"/>.
    /// </summary>
    /// <remarks>
    /// The three spellings <see cref="CorporateAction"/> documents, recognised without knowing which
    /// field uses which. The collection test has to come before the default test: a
    /// <see cref="Dictionary{TKey, TValue}"/> is a reference type, so comparing it against a freshly
    /// constructed one would answer by reference and never by content.
    /// </remarks>
    private static bool IsDefaultOrEmpty(object? value) => value switch
    {
        null => true,
        string text => text.Length == 0,
        System.Collections.ICollection collection => collection.Count == 0,
        _ => value.Equals(Activator.CreateInstance(value.GetType())),
    };

    private static CorporateActionsGetRangeParams Range() => new()
    {
        Symbols = Symbols.From("MSFT"),
        DateTimeRange = ReferenceDateTimeRange.StartingAt(Start),
    };

    /// <summary>
    /// <see cref="UpstreamRow"/> with one substring replaced — the base for every test that varies
    /// a single field, because it is Databento's own row and its three maps are in the shapes worth
    /// varying from.
    /// </summary>
    private static string RowWith(string find, string replacement) =>
        UpstreamRow.Replace(find, replacement, StringComparison.Ordinal);

    /// <summary>
    /// <see cref="FullRow"/> with one substring replaced, for the two tests whose field is
    /// <c>null</c> in <see cref="UpstreamRow"/> and so has nothing to vary there.
    /// </summary>
    private static string FullRowWith(string find, string replacement) =>
        FullRow.Replace(find, replacement, StringComparison.Ordinal);

    private static async Task<CorporateAction> SingleRowAsync(string row)
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetRange, MockHistoricalResponse.ZstdJsonLines(row));

        await using var client = ClientFor(gateway);
        var rows = await DrainAsync(client.CorporateActions.GetRangeAsync(Range(), Cancel));

        gateway.ThrowIfRejected();
        return Assert.Single(rows);
    }

    private static async Task<MockHistoricalGateway> StartAsync()
    {
        var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetRange, MockHistoricalResponse.ZstdJsonLines(FullRow));
        return gateway;
    }

    private static ReferenceClient ClientFor(MockHistoricalGateway gateway) => new()
    {
        ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey),
        BaseUrl = gateway.BaseUrl,
    };

    private static async Task<List<CorporateAction>> DrainAsync(IAsyncEnumerable<CorporateAction> rows)
    {
        var drained = new List<CorporateAction>();

        await foreach (var row in rows.WithCancellation(Cancel).ConfigureAwait(false))
        {
            drained.Add(row);
        }

        return drained;
    }
}
