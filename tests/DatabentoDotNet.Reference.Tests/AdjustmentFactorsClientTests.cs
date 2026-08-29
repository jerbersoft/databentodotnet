using System.Globalization;
using System.Text.Json;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Historical;
using DatabentoDotNet.Historical.Tests;
using NodaTime;

namespace DatabentoDotNet.Reference.Tests;

/// <summary>
/// Tests for <see cref="AdjustmentFactorsClient"/> — the form it posts, and the twenty-eight-field
/// row it reads back.
/// </summary>
/// <remarks>
/// <para>
/// These drive a real <see cref="ReferenceClient"/> at <see cref="MockHistoricalGateway"/> over a
/// real socket, as <see cref="ReferenceClientTests"/> does and for the same reason: an
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
public class AdjustmentFactorsClientTests
{
    private const string GetRange = "adjustment_factors.get_range";

    /// <summary>
    /// Upstream's own test fixture row (<c>adjustment.rs:196-226</c>), transcribed rather than
    /// invented so that the field names, the code spellings and the magnitudes are the ones a real
    /// response used — the closest thing to a real row available without billing for one.
    /// </summary>
    private const string FullRow = """
        {"security_id":"S-1318698","event_id":"E-3287361-DIV","event":"DIV","issuer_name":"VanEck ETF Trust","security_type":"ETF","primary_exchange":"USBATS","exchange":"USBATS","operating_mic":"BATS","symbol":"HYD","nasdaq_symbol":"HYD","local_code":"HYD","local_code_resulting":null,"isin":"US92189H4092","isin_resulting":null,"us_code":"92189H409","status":"A","ex_date":"2024-05-01","factor":0.995833170541121,"close":51.19,"currency":"USD","sentiment":0.998241844110178,"reason":17,"gross_dividend":0.2133,"dividend_currency":"USD","frequency":"MNT","option":1,"detail":"INT Dividend (cash) of USD0.2133/ETF","ts_created":"1970-01-01T00:00:00.000000000Z"}
        """;

    /// <summary>
    /// <c>2023-10-10T00:00:00Z</c> in Unix nanoseconds — the start upstream's own test uses
    /// (<c>adjustment.rs:194</c>). Written as the integer rather than derived from
    /// <see cref="Start"/>, so the assertion on the <c>start</c> form field compares the wire
    /// against a literal instead of against the arithmetic that produced it.
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
    public async Task GetRangeAsync_PostsToTheVersionedSlug()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetRange, MockHistoricalResponse.ZstdJsonLines(FullRow));

        await using var client = ClientFor(gateway);
        await DrainAsync(client, Open());

        gateway.ThrowIfRejected();
        var recorded = Assert.Single(gateway.Requests);
        Assert.Equal("POST", recorded.Method);
        Assert.Equal("/v0/" + GetRange, recorded.Path);
    }

    /// <summary>
    /// The Definition of done's headline assertion: an unfiltered open range sends five fields and
    /// no more. <c>end</c>, <c>countries</c> and <c>security_types</c> are <em>absent</em>, not
    /// empty — <c>countries=</c> is a different request from no <c>countries</c> at all.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_SendsExactlyFiveFieldsForAnUnfilteredOpenRange()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetRange, MockHistoricalResponse.ZstdJsonLines(FullRow));

        await using var client = ClientFor(gateway);
        await DrainAsync(client, Open());

        gateway.ThrowIfRejected();
        var form = Assert.Single(gateway.Requests).Form;

        Assert.Equal(
            ["allocate_isins", "compression", "start", "stype_in", "symbols"],
            form.Keys.OrderBy(key => key, StringComparer.Ordinal));

        Assert.Equal("raw_symbol", form["stype_in"]);
        Assert.Equal("MSFT", form["symbols"]);
        Assert.Equal("true", form["allocate_isins"]);
        Assert.Equal("zstd", form["compression"]);
        Assert.Equal(StartUnixNanoseconds, long.Parse(form["start"], CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task GetRangeAsync_SendsTheEndOnlyWhenTheRangeIsClosed()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetRange, MockHistoricalResponse.ZstdJsonLines(FullRow));

        await using var client = ClientFor(gateway);
        await DrainAsync(client, Open() with { DateTimeRange = ReferenceDateTimeRange.Between(Start, End) });

        gateway.ThrowIfRejected();
        var form = Assert.Single(gateway.Requests).Form;

        Assert.Equal(EndUnixNanoseconds, long.Parse(form["end"], CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task GetRangeAsync_JoinsTheTwoFiltersWithCommas()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetRange, MockHistoricalResponse.ZstdJsonLines(FullRow));

        await using var client = ClientFor(gateway);
        await DrainAsync(
            client,
            Open() with
            {
                Countries = [Country.From("US"), Country.From("GB")],
                SecurityTypes = [SecurityType.From("EQS"), SecurityType.From("ETF")],
            });

        gateway.ThrowIfRejected();
        var recorded = Assert.Single(gateway.Requests);

        Assert.Equal("US,GB", recorded.Form["countries"]);
        Assert.Equal("EQS,ETF", recorded.Form["security_types"]);
    }

    /// <summary>
    /// An empty filter list means the same thing as no filter: the parameter is left out. Asserted
    /// separately from the <see langword="null"/> case because a rendering that special-cased only
    /// <see langword="null"/> would pass that test and send <c>countries=</c> here.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_OmitsAnEmptyFilterRatherThanSendingItEmpty()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetRange, MockHistoricalResponse.ZstdJsonLines(FullRow));

        await using var client = ClientFor(gateway);
        await DrainAsync(client, Open() with { Countries = [], SecurityTypes = [] });

        gateway.ThrowIfRejected();
        var form = Assert.Single(gateway.Requests).Form;

        Assert.DoesNotContain("countries", form.Keys);
        Assert.DoesNotContain("security_types", form.Keys);
    }

    /// <summary>
    /// <c>bool.ToString()</c> is <c>True</c> and <c>False</c>; upstream's <c>bool::to_string</c> is
    /// lower case. The difference is invisible in C# and load-bearing on the wire, so both
    /// spellings are asserted rather than only the default.
    /// </summary>
    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public async Task GetRangeAsync_RendersAllocateIsinsLowerCase(bool allocate, string expected)
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetRange, MockHistoricalResponse.ZstdJsonLines(FullRow));

        await using var client = ClientFor(gateway);
        await DrainAsync(client, Open() with { AllocateIsins = allocate });

        gateway.ThrowIfRejected();
        Assert.Equal(expected, Assert.Single(gateway.Requests).Form["allocate_isins"]);
    }

    /// <summary>
    /// <c>compression=zstd</c> is on every request and is not caller-settable: the parameter type
    /// has no property for it, so the only way to observe the value is the wire, and the only way
    /// to change it would be to edit the library.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_AlwaysSendsZstdAndOffersNoWayToChangeIt()
    {
        Assert.Null(typeof(AdjustmentFactorsGetRangeParams).GetProperty("Compression"));

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetRange, MockHistoricalResponse.ZstdJsonLines(FullRow));

        await using var client = ClientFor(gateway);
        await DrainAsync(client, Open() with { AllocateIsins = false, Countries = [Country.From("US")] });

        gateway.ThrowIfRejected();
        Assert.Equal(
            AdjustmentFactorsClient.RequestCompression,
            Assert.Single(gateway.Requests).Form["compression"]);
    }

    /// <summary>
    /// The form is upstream's push order (<c>adjustment.rs:32-41</c>). Asserted against the raw
    /// body rather than the decoded dictionary, which does not preserve order.
    /// </summary>
    [Fact]
    public void ToFormParameters_UsesUpstreamsPushOrder()
    {
        var form = (Open() with
        {
            DateTimeRange = ReferenceDateTimeRange.Between(Start, End),
            Countries = [Country.From("US")],
            SecurityTypes = [SecurityType.From("EQS")],
        }).ToFormParameters();

        Assert.Equal(
            ["stype_in", "symbols", "allocate_isins", "compression", "start", "end", "countries", "security_types"],
            form.Select(field => field.Key));
    }

    /* ------------------------------------------------------------------ *
     * When the request is made, and when it is not.
     * ------------------------------------------------------------------ */

    /// <summary>
    /// Nothing is sent until the enumeration starts. This endpoint bills, so a caller who builds a
    /// query and never enumerates must not be charged for it.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_SendsNothingUntilTheEnumerationStarts()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetRange, MockHistoricalResponse.ZstdJsonLines(FullRow));

        await using var client = ClientFor(gateway);
        var rows = client.AdjustmentFactors.GetRangeAsync(Open(), Cancel);

        Assert.Empty(gateway.Requests);

        await DrainAsync(rows);
        Assert.Single(gateway.Requests);
    }

    /// <summary>
    /// A bad argument faults at the call, not at the first <c>MoveNextAsync</c>. Inside an iterator
    /// these checks would be deferred — or, for a caller who never enumerates, skipped entirely.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_ValidatesEagerlyRatherThanAtTheFirstStep()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        await using var client = ClientFor(gateway);

        Assert.Throws<ArgumentNullException>(
            () => client.AdjustmentFactors.GetRangeAsync(null!, Cancel));

        // A `required` property stops a caller omitting Symbols; it does not stop them assigning
        // default, and ToApiString refuses to render one.
        Assert.Throws<InvalidOperationException>(
            () => client.AdjustmentFactors.GetRangeAsync(
                new AdjustmentFactorsGetRangeParams
                {
                    Symbols = default,
                    DateTimeRange = ReferenceDateTimeRange.StartingAt(Start),
                },
                Cancel));

        Assert.Throws<InvalidOperationException>(
            () => client.AdjustmentFactors.GetRangeAsync(
                new AdjustmentFactorsGetRangeParams
                {
                    Symbols = Symbols.From("MSFT"),
                    DateTimeRange = default,
                },
                Cancel));

        Assert.Empty(gateway.Requests);
    }

    /// <summary>
    /// A disposed client refuses at the call too, for the same placement reason: the transport is
    /// reached before the iterator is built, so the mistake surfaces where it was made rather than
    /// at the first row.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_RefusesADisposedClientAtTheCall()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        var client = ClientFor(gateway);
        await client.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(
            () => client.AdjustmentFactors.GetRangeAsync(Open(), Cancel));

        Assert.Empty(gateway.Requests);
    }

    [Fact]
    public async Task AdjustmentFactors_IsBuiltOnceAndCached()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        await using var client = ClientFor(gateway);

        Assert.Same(client.AdjustmentFactors, client.AdjustmentFactors);
    }

    /* ------------------------------------------------------------------ *
     * What comes back.
     * ------------------------------------------------------------------ */

    /// <summary>
    /// All twenty-eight fields, from a fully populated row. A model that reads twenty-seven of them
    /// and silently drops one is the failure this exists to catch: an unmatched JSON property is
    /// skipped without complaint, in both languages.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_ReadsAllTwentyEightFields()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetRange, MockHistoricalResponse.ZstdJsonLines(FullRow));

        await using var client = ClientFor(gateway);
        var row = Assert.Single(await DrainAsync(client, Open()));

        gateway.ThrowIfRejected();

        Assert.Equal("S-1318698", row.SecurityId);
        Assert.Equal("E-3287361-DIV", row.EventId);
        Assert.Equal(Event.From("DIV"), row.Event);
        Assert.Equal("VanEck ETF Trust", row.IssuerName);
        Assert.Equal(SecurityType.From("ETF"), row.SecurityType);
        Assert.Equal("USBATS", row.PrimaryExchange);
        Assert.Equal("USBATS", row.Exchange);
        Assert.Equal("BATS", row.OperatingMic);
        Assert.Equal("HYD", row.Symbol);
        Assert.Equal("HYD", row.NasdaqSymbol);
        Assert.Equal("HYD", row.LocalCode);
        Assert.Null(row.LocalCodeResulting);
        Assert.Equal("US92189H4092", row.Isin);
        Assert.Null(row.IsinResulting);
        Assert.Equal("92189H409", row.UsCode);
        Assert.Equal(AdjustmentStatus.Apply, row.Status);
        Assert.Equal(new LocalDate(2024, 5, 1), row.ExDate);
        Assert.Equal(0.995833170541121m, row.Factor);
        Assert.Equal(51.19m, row.Close);
        Assert.Equal("USD", row.Currency);
        Assert.Equal(0.998241844110178m, row.Sentiment);
        Assert.Equal(17u, row.Reason);
        Assert.Equal(0.2133m, row.GrossDividend);
        Assert.Equal(Currency.From("USD"), row.DividendCurrency);
        Assert.Equal(Frequency.From("MNT"), row.Frequency);
        Assert.Equal(1u, row.Option);
        Assert.Equal("INT Dividend (cash) of USD0.2133/ETF", row.Detail);
        Assert.Equal(Instant.FromUnixTimeTicks(0), row.TsCreated);
    }

    /// <summary>
    /// The fourteen optional fields, absent. A model that only ever sees a fully populated row
    /// proves nothing about <c>Option</c>: a property wrongly marked <see langword="required"/>
    /// passes every assertion above and throws here.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_ReadsARowWithEveryOptionalFieldAbsent()
    {
        const string Sparse = """
            {"security_id":"S-1","event_id":"E-1","event":"DIV","issuer_name":"Acme","security_type":"EQS","operating_mic":"XNAS","status":"P","ex_date":"2024-05-01","factor":1,"sentiment":1,"reason":0,"option":0,"detail":"","ts_created":"2024-05-01 12:00:00"}
            """;

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetRange, MockHistoricalResponse.ZstdJsonLines(Sparse));

        await using var client = ClientFor(gateway);
        var row = Assert.Single(await DrainAsync(client, Open()));

        gateway.ThrowIfRejected();

        Assert.Null(row.PrimaryExchange);
        Assert.Null(row.Exchange);
        Assert.Null(row.Symbol);
        Assert.Null(row.NasdaqSymbol);
        Assert.Null(row.LocalCode);
        Assert.Null(row.LocalCodeResulting);
        Assert.Null(row.Isin);
        Assert.Null(row.IsinResulting);
        Assert.Null(row.UsCode);
        Assert.Null(row.Close);
        Assert.Null(row.Currency);
        Assert.Null(row.GrossDividend);

        // The two reference codes spell absence as `default`, not as null — one way to say nothing
        // per field, which is what IReferenceCode already defines. See AdjustmentFactor's remarks.
        Assert.False(row.DividendCurrency.HasValue);
        Assert.Null(row.DividendCurrency.Code);
        Assert.False(row.Frequency.HasValue);
        Assert.Null(row.Frequency.Code);

        // And the fourteen that are not optional still arrived.
        Assert.Equal("S-1", row.SecurityId);
        Assert.Equal(AdjustmentStatus.Pending, row.Status);
        Assert.Equal(1m, row.Factor);
    }

    /// <summary>
    /// The same fourteen, present and explicitly <c>null</c>. Distinct from the absent case: a
    /// converter that rejects a null token passes the test above and fails here, which makes this
    /// the test that pins <c>ReferenceCodeJsonConverter</c>'s null handling. That handling is the
    /// framework's own default rather than something <c>HandleNull</c> switches on — #60 settled it
    /// by deleting the override and watching this stay green.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_ReadsARowWithEveryOptionalFieldExplicitlyNull()
    {
        const string Nulled = """
            {"security_id":"S-1","event_id":"E-1","event":"DIV","issuer_name":"Acme","security_type":"EQS","primary_exchange":null,"exchange":null,"operating_mic":"XNAS","symbol":null,"nasdaq_symbol":null,"local_code":null,"local_code_resulting":null,"isin":null,"isin_resulting":null,"us_code":null,"status":"R","ex_date":"2024-05-01","factor":2,"close":null,"currency":null,"sentiment":1,"reason":0,"gross_dividend":null,"dividend_currency":null,"frequency":null,"option":0,"detail":"","ts_created":"2024-05-01T12:00:00Z"}
            """;

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetRange, MockHistoricalResponse.ZstdJsonLines(Nulled));

        await using var client = ClientFor(gateway);
        var row = Assert.Single(await DrainAsync(client, Open()));

        gateway.ThrowIfRejected();

        Assert.Null(row.PrimaryExchange);
        Assert.Null(row.Close);
        Assert.Null(row.Currency);
        Assert.Null(row.GrossDividend);
        Assert.False(row.DividendCurrency.HasValue);
        Assert.False(row.Frequency.HasValue);
        Assert.Equal(AdjustmentStatus.Rescind, row.Status);
        Assert.Equal(2m, row.Factor);
    }

    /// <summary>
    /// The behavioural difference from upstream, stated as a measurement rather than as a comment.
    /// Upstream sorts its buffered <c>Vec</c> by <c>ex_date</c> (<c>adjustment.rs:51</c>); a stream
    /// cannot, so these rows come out in the order the server sent them — descending here, which is
    /// the order a sort would destroy.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_PreservesServerOrderRatherThanSortingByExDate()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(
            GetRange,
            MockHistoricalResponse.ZstdJsonLines(
                RowWithExDate("2024-12-31"),
                RowWithExDate("2024-01-01"),
                RowWithExDate("2024-06-15")));

        await using var client = ClientFor(gateway);
        var rows = await DrainAsync(client, Open());

        gateway.ThrowIfRejected();
        Assert.Equal(
            [new LocalDate(2024, 12, 31), new LocalDate(2024, 1, 1), new LocalDate(2024, 6, 15)],
            rows.Select(row => row.ExDate));
    }

    /// <summary>
    /// An unmodelled <c>security_type</c> is carried, not lost — the property upstream gives up by
    /// typing the field as a bare enum over 30 of the dictionary's 64 codes.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_KeepsASecurityTypeThisLibraryDoesNotKnow()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(
            GetRange,
            MockHistoricalResponse.ZstdJsonLines(FullRow.Replace(""""security_type":"ETF"""", """"security_type":"ZZZ"""", StringComparison.Ordinal)));

        await using var client = ClientFor(gateway);
        var row = Assert.Single(await DrainAsync(client, Open()));

        gateway.ThrowIfRejected();
        Assert.Equal("ZZZ", row.SecurityType.Code);
        Assert.True(row.SecurityType.HasValue);
        Assert.False(row.SecurityType.IsKnown);
    }

    /// <summary>
    /// An unmodelled <c>status</c> is an error, because that field is one of the nine closed
    /// alphabets: a new value in it would be a wire-format change rather than a dictionary entry.
    /// The pair with the test above is the whole of #50 and #51 restated on one row.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_RejectsAnAdjustmentStatusOutsideItsAlphabet()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(
            GetRange,
            MockHistoricalResponse.ZstdJsonLines(FullRow.Replace(""""status":"A"""", """"status":"Z"""", StringComparison.Ordinal)));

        await using var client = ClientFor(gateway);

        var thrown = await Assert.ThrowsAsync<JsonException>(async () => await DrainAsync(client, Open()));

        // The offending code, named. A bare Contains("Z") would also pass on a message that merely
        // said the row was bad, which is the assertion this is not.
        Assert.Contains("has no wire code 'Z'", thrown.Message, StringComparison.Ordinal);
    }

    /* ------------------------------------------------------------------ *
     * The decimal decision, as executable assertions.
     * ------------------------------------------------------------------ */

    /// <summary>
    /// What <see langword="decimal"/> buys, measured on the value it was chosen for. A wire value
    /// of more than seventeen significant digits survives here and would not through a
    /// <see langword="double"/>.
    /// </summary>
    /// <remarks>
    /// The seventeen-digit qualifier matters and is why this test uses a long value rather than
    /// upstream's. <c>System.Text.Json</c> writes a <see langword="double"/> in shortest-round-trip
    /// form, so a shorter wire value round-trips through <em>either</em> type — the argument for
    /// <see langword="decimal"/> is about the value used in arithmetic, not the text echoed back.
    /// See <see cref="AdjustmentFactor.Factor"/>.
    /// </remarks>
    [Fact]
    public async Task GetRangeAsync_KeepsAFactorTooPreciseForBinaryFloatingPoint()
    {
        const string Precise = "1.2345678901234567890123456789";

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(
            GetRange,
            MockHistoricalResponse.ZstdJsonLines(
                FullRow.Replace(""""factor":0.995833170541121"""", $""""factor":{Precise}"""", StringComparison.Ordinal)));

        await using var client = ClientFor(gateway);
        var row = Assert.Single(await DrainAsync(client, Open()));

        gateway.ThrowIfRejected();
        Assert.Equal(decimal.Parse(Precise, CultureInfo.InvariantCulture), row.Factor);
        Assert.Equal(Precise, row.Factor.ToString(CultureInfo.InvariantCulture));

        // The same text through a double loses eleven digits. Asserted here rather than described,
        // because this is the whole of what the decision bought.
        Assert.NotEqual(Precise, double.Parse(Precise, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// What <see langword="decimal"/> costs, on the loud side. Above
    /// <see cref="decimal.MaxValue"/> the row fails with a
    /// <see cref="JsonException"/> naming the property, where upstream would have returned an
    /// approximation. Documented on <see cref="AdjustmentFactor.Factor"/>; asserted here so that a
    /// framework change turning this into something quieter breaks a test.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_FailsLoudlyOnAFactorLargerThanDecimalCanHold()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(
            GetRange,
            MockHistoricalResponse.ZstdJsonLines(
                FullRow.Replace(""""factor":0.995833170541121"""", """"factor":1e29"""", StringComparison.Ordinal)));

        await using var client = ClientFor(gateway);

        var thrown = await Assert.ThrowsAsync<JsonException>(async () => await DrainAsync(client, Open()));

        // The JSON path, so the failure names the field rather than only the row.
        Assert.Contains("$.factor", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the quiet side, which is the half worth knowing about. Below roughly 10^-28 the value
    /// does <em>not</em> throw — it reads as zero. No price, dividend or split factor reaches that
    /// magnitude, but a test that asserted only the loud failure would leave the impression that
    /// <see langword="decimal"/>'s cost is always loud.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_FlushesAFactorSmallerThanDecimalCanHoldToZeroWithoutThrowing()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(
            GetRange,
            MockHistoricalResponse.ZstdJsonLines(
                FullRow.Replace(""""factor":0.995833170541121"""", """"factor":1e-30"""", StringComparison.Ordinal)));

        await using var client = ClientFor(gateway);
        var row = Assert.Single(await DrainAsync(client, Open()));

        gateway.ThrowIfRejected();
        Assert.Equal(0m, row.Factor);
    }

    /* ------------------------------------------------------------------ *
     * Helpers.
     * ------------------------------------------------------------------ */

    private static AdjustmentFactorsGetRangeParams Open() => new()
    {
        Symbols = Symbols.From("MSFT"),
        DateTimeRange = ReferenceDateTimeRange.StartingAt(Start),
    };

    private static string RowWithExDate(string exDate) =>
        FullRow.Replace(""""ex_date":"2024-05-01"""", $""""ex_date":"{exDate}"""", StringComparison.Ordinal);

    private static ReferenceClient ClientFor(MockHistoricalGateway gateway) => new()
    {
        ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey),
        BaseUrl = gateway.BaseUrl,
    };

    private static Task<List<AdjustmentFactor>> DrainAsync(
        ReferenceClient client,
        AdjustmentFactorsGetRangeParams parameters) =>
        DrainAsync(client.AdjustmentFactors.GetRangeAsync(parameters, Cancel));

    private static async Task<List<AdjustmentFactor>> DrainAsync(IAsyncEnumerable<AdjustmentFactor> rows)
    {
        var drained = new List<AdjustmentFactor>();

        await foreach (var row in rows.WithCancellation(Cancel).ConfigureAwait(false))
        {
            drained.Add(row);
        }

        return drained;
    }
}
