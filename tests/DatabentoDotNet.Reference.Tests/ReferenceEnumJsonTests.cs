using System.Text.Json;
using System.Text.Json.Serialization;
using DatabentoDotNet.Historical;
using DatabentoDotNet.Historical.Tests;
using DatabentoDotNet.Reference.Json;

namespace DatabentoDotNet.Reference.Tests;

/// <summary>
/// Tests the eleven converters that carry the nine closed reference enums: nine for the enums
/// themselves and two more for the pair the dictionary says may be blank.
/// </summary>
/// <remarks>
/// <para>
/// <b>The happy path runs over a socket, not over a string literal.</b> A converter that reads a
/// literal correctly and a converter that reads a real response body correctly are not the same
/// claim: the body arrives chunked, through <see cref="System.Net.Http.HttpClient"/>, and for the
/// <c>get_range</c> endpoints through a zstd frame as well. <see cref="MockHistoricalGateway"/> is
/// what closes that gap, and it is the same harness <see cref="ReferenceClientTests"/> drives — the
/// reference API is the historical transport with a different set of slugs.
/// </para>
/// <para>
/// <b>The failure paths are asserted on a literal, deliberately.</b> Nine enums times four ways to
/// be wrong is thirty-six sockets to prove one thing that has nothing to do with transport, and the
/// gateway test below already establishes that a converter reached over the wire is the same
/// converter. What matters in each is the <em>message</em>: an unrecognised code must name itself,
/// because a caller who cannot see which code broke the row cannot report it.
/// </para>
/// <para>
/// <b>The DTO and its context are nested and private</b>, in the style of <c>TestJson</c> in
/// <c>HistoricalClientTests</c>. The response models arrive with #53–#55; a reader test that waited
/// for one would be testing the wrong thing, and what this needs is only a type with a
/// source-generated <c>JsonTypeInfo</c>, which is the sole shape either reader accepts.
/// </para>
/// </remarks>
public partial class ReferenceEnumJsonTests
{
    private const string GetLast = "security_master.get_last";
    private const string GetRange = "corporate_actions.get_range";

    /// <summary>
    /// One row with every one of the nine set, and none of them to its first member.
    /// </summary>
    /// <remarks>
    /// Every code here is transcribed from the fixture's descriptions rather than from the enum
    /// declarations, and none of the eleven is the alphabetically first member of its enum — so a
    /// converter that silently produced <c>default</c>, or one wired to the wrong enum, fails.
    /// </remarks>
    private const string EveryEnumJson = """
        {"action":"Q","adjustmentStatus":"R","fraction":"D","globalStatus":"I","listingSource":"S",
         "listingStatus":"V","mandVolu":"W","paymentType":"B","voting":"L",
         "optionalFraction":"U","optionalPaymentType":"T"}
        """;

    private static readonly KeyValuePair<string, string>[] RequestForm = [new("index", "ts_effective")];

    /// <summary>The nine enums, by the property that carries one and the name in its messages.</summary>
    public static TheoryData<string, string> NineEnums => new()
    {
        { "action", "Action" },
        { "adjustmentStatus", "AdjustmentStatus" },
        { "fraction", "Fraction" },
        { "globalStatus", "GlobalStatus" },
        { "listingSource", "ListingSource" },
        { "listingStatus", "ListingStatus" },
        { "mandVolu", "MandVolu" },
        { "paymentType", "PaymentType" },
        { "voting", "Voting" },
    };

    /// <summary>The two properties whose converter reads a blank as no value.</summary>
    public static TheoryData<string> BlankLegalProperties => ["optionalFraction", "optionalPaymentType"];

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------------------------
    // Over the wire.
    // ------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryEnum_ArrivesFromAJsonBodyTheGatewayServes()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetLast, MockHistoricalResponse.Json("[" + EveryEnumJson + "]"));

        await using var client = ClientFor(gateway);
        using var response = await client.Transport.SendAsync(
            HttpMethod.Post, GetLast, RequestForm, cancellationToken: Cancel);

        var rows = await HistoricalClient.ReadJsonAsync(response, TestJson.Default.ListEnumRow, Cancel);

        gateway.ThrowIfRejected();
        AssertIsTheEveryEnumRow(Assert.Single(rows));
    }

    [Fact]
    public async Task EveryEnum_ArrivesFromAZstdJsonLinesBodyTheGatewayServes()
    {
        // The realistic shape for these enums: all three reference `get_range` endpoints answer
        // with a zstd-framed JSONL body, and that is where a CorporateAction row will carry them.
        var line = string.Concat(EveryEnumJson.Split('\n').Select(l => l.Trim()));

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetRange, MockHistoricalResponse.ZstdJsonLines(line, line));

        await using var client = ClientFor(gateway);
        using var response = await client.Transport.SendAsync(
            HttpMethod.Post, GetRange, RequestForm, cancellationToken: Cancel);

        var rows = await HistoricalClient.ReadZstdJsonLinesAsync(response, TestJson.Default.EnumRow, Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(2, rows.Count);
        foreach (var row in rows)
        {
            AssertIsTheEveryEnumRow(row);
        }
    }

    [Fact]
    public async Task AnUnrecognisedCode_ThrowsOverTheWireToo()
    {
        // The same rejection as the literal-driven tests below, reached the way a caller reaches
        // it — so the converter the wire path resolves really is the one they assert against.
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetLast, MockHistoricalResponse.Json("""[{"action":"Z"}]"""));

        await using var client = ClientFor(gateway);
        using var response = await client.Transport.SendAsync(
            HttpMethod.Post, GetLast, RequestForm, cancellationToken: Cancel);

        var error = await Assert.ThrowsAsync<JsonException>(() =>
            HistoricalClient.ReadJsonAsync(response, TestJson.Default.ListEnumRow, Cancel));

        gateway.ThrowIfRejected();
        Assert.Contains("'Z'", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(Action), error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------
    // Reading a code.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void EveryDefinedCode_ReadsBackAsItsMember()
    {
        // The 42, one property at a time, against a hand-typed expectation. Generating the JSON
        // from Enum.GetValues would check the converters against the very tables they read.
        AssertReads("action", "C", row => row.Action, Action.Cancelled);
        AssertReads("action", "D", row => row.Action, Action.Deleted);
        AssertReads("action", "I", row => row.Action, Action.Inserted);
        AssertReads("action", "P", row => row.Action, Action.PaymentDetailsCancelledByIssuer);
        AssertReads("action", "Q", row => row.Action, Action.PaymentDetailsDeletedBySupplier);
        AssertReads("action", "U", row => row.Action, Action.Updated);

        AssertReads("adjustmentStatus", "A", row => row.AdjustmentStatus, AdjustmentStatus.Apply);
        AssertReads("adjustmentStatus", "R", row => row.AdjustmentStatus, AdjustmentStatus.Rescind);
        AssertReads("adjustmentStatus", "P", row => row.AdjustmentStatus, AdjustmentStatus.Pending);

        AssertReads("fraction", "C", row => row.Fraction, Fraction.Cash);
        AssertReads("fraction", "D", row => row.Fraction, Fraction.RoundDown);
        AssertReads("fraction", "F", row => row.Fraction, Fraction.Fractions);
        AssertReads("fraction", "U", row => row.Fraction, Fraction.RoundUp);

        AssertReads("globalStatus", "A", row => row.GlobalStatus, GlobalStatus.Active);
        AssertReads("globalStatus", "D", row => row.GlobalStatus, GlobalStatus.InDefault);
        AssertReads("globalStatus", "I", row => row.GlobalStatus, GlobalStatus.Inactive);

        AssertReads("listingSource", "M", row => row.ListingSource, ListingSource.Main);
        AssertReads("listingSource", "S", row => row.ListingSource, ListingSource.Secondary);

        AssertReads("listingStatus", "D", row => row.ListingStatus, ListingStatus.Delisted);
        AssertReads("listingStatus", "G", row => row.ListingStatus, ListingStatus.RpoListed);
        AssertReads("listingStatus", "H", row => row.ListingStatus, ListingStatus.RpoDelisted);
        AssertReads("listingStatus", "I", row => row.ListingStatus, ListingStatus.RpoSuspended);
        AssertReads("listingStatus", "L", row => row.ListingStatus, ListingStatus.Listed);
        AssertReads("listingStatus", "N", row => row.ListingStatus, ListingStatus.New);
        AssertReads("listingStatus", "P", row => row.ListingStatus, ListingStatus.Pending);
        AssertReads("listingStatus", "R", row => row.ListingStatus, ListingStatus.Resumed);
        AssertReads("listingStatus", "S", row => row.ListingStatus, ListingStatus.Suspended);
        AssertReads("listingStatus", "T", row => row.ListingStatus, ListingStatus.TpListed);
        AssertReads("listingStatus", "U", row => row.ListingStatus, ListingStatus.TpDelisted);
        AssertReads("listingStatus", "V", row => row.ListingStatus, ListingStatus.TpSuspended);

        AssertReads("mandVolu", "M", row => row.MandVolu, MandVolu.Mandatory);
        AssertReads("mandVolu", "V", row => row.MandVolu, MandVolu.Voluntary);
        AssertReads("mandVolu", "W", row => row.MandVolu, MandVolu.MandVolu);

        AssertReads("paymentType", "B", row => row.PaymentType, PaymentType.CashAndStock);
        AssertReads("paymentType", "C", row => row.PaymentType, PaymentType.Cash);
        AssertReads("paymentType", "D", row => row.PaymentType, PaymentType.DissentersRights);
        AssertReads("paymentType", "S", row => row.PaymentType, PaymentType.Stock);
        AssertReads("paymentType", "T", row => row.PaymentType, PaymentType.Tba);

        AssertReads("voting", "L", row => row.Voting, Voting.Limited);
        AssertReads("voting", "M", row => row.Voting, Voting.Multiple);
        AssertReads("voting", "N", row => row.Voting, Voting.No);
        AssertReads("voting", "V", row => row.Voting, Voting.Voting);
    }

    [Fact]
    public void AnAbsentProperty_LeavesAnUndefinedValueRatherThanTheFirstMember()
    {
        // The byte backing, end to end. `{}` never reaches a converter at all, so what the caller
        // sees is C#'s own default — which for these enums is a value no alphabet defines.
        var row = Deserialize("{}");

        Assert.False(Enum.IsDefined(row.Action));
        Assert.False(Enum.IsDefined(row.PaymentType));
        Assert.Null(row.OptionalFraction);
        Assert.Null(row.OptionalPaymentType);
    }

    // ------------------------------------------------------------------------------------
    // Rejecting a code. The message has to name what arrived.
    // ------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(NineEnums))]
    public void AnUnrecognisedCode_ThrowsWithTheCodeInTheMessage(string property, string name)
    {
        // 'Z' is in none of the nine alphabets, which ReferenceEnumTests asserts against the
        // fixture rather than leaving to this comment.
        var error = Assert.Throws<JsonException>(() => Deserialize($$"""{"{{property}}":"Z"}"""));

        Assert.Contains("'Z'", error.Message, StringComparison.Ordinal);
        Assert.Contains(name, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(NineEnums))]
    public void AStringThatIsNotOneCharacter_IsUnrecognisedAndTheMessageSaysWhatArrived(string property, string name)
    {
        // The difference from the codec's char enums, made concrete: on the DBN wire the value is
        // a byte and cannot have a wrong length, and here it is a string that can.
        var error = Assert.Throws<JsonException>(() => Deserialize($$"""{"{{property}}":"CD"}"""));

        Assert.Contains("'CD'", error.Message, StringComparison.Ordinal);
        Assert.Contains(name, error.Message, StringComparison.Ordinal);
        Assert.Contains("2", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(NineEnums))]
    public void ATokenThatIsNotAString_IsRejectedByName(string property, string name)
    {
        // Without an explicit token check this arrives as System.Text.Json's own generic "could
        // not be converted" message, which names the type and loses everything else.
        var error = Assert.Throws<JsonException>(() => Deserialize($$"""{"{{property}}":67}"""));

        Assert.Contains(name, error.Message, StringComparison.Ordinal);
        Assert.Contains("Number", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(NineEnums))]
    public void ABlankOnANonNullableField_IsRejectedByName(string property, string name)
    {
        // True of all nine, including the two the dictionary says may be blank: a blank means "no
        // value", and a non-nullable field has nowhere to put that. The two differ only in what
        // their message then tells the caller to do about it, which the next test checks.
        foreach (var blank in (string[])["null", "\"\""])
        {
            var error = Assert.Throws<JsonException>(() => Deserialize($$"""{"{{property}}":{{blank}}}"""));
            Assert.Contains(name, error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ABlankOnTheTwoBlankLegalEnums_PointsAtTheNullableConverter()
    {
        // A blank really is a value for these two, so the message is a wiring instruction rather
        // than a report of a malformed response — and it names the converter to use.
        var fraction = Assert.Throws<JsonException>(() => Deserialize("""{"fraction":""}"""));
        Assert.Contains(nameof(NullableFractionJsonConverter), fraction.Message, StringComparison.Ordinal);

        var payment = Assert.Throws<JsonException>(() => Deserialize("""{"paymentType":""}"""));
        Assert.Contains(nameof(NullablePaymentTypeJsonConverter), payment.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlankOnTheSevenOtherEnums_NamesTheGroupThatSaysSo()
    {
        // The other seven say why instead: the dictionary lists no blank for them. AdjustmentStatus
        // has no group at all, so it cites upstream's non-optional field declaration.
        Assert.Contains("ACTION", Assert.Throws<JsonException>(() => Deserialize("""{"action":""}""")).Message, StringComparison.Ordinal);
        Assert.Contains("VOTING", Assert.Throws<JsonException>(() => Deserialize("""{"voting":""}""")).Message, StringComparison.Ordinal);
        Assert.Contains("adjustment.rs", Assert.Throws<JsonException>(() => Deserialize("""{"adjustmentStatus":""}""")).Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------
    // The two nullable converters.
    // ------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(BlankLegalProperties))]
    public void ANullableFieldReadsEveryBlank_AsNoValue(string property)
    {
        // Three spellings of the same absence. JSON null is the one System.Text.Json would answer
        // on its own for a Nullable<T>; the empty string is the one that needs this converter, and
        // it is the one the reference API actually sends.
        foreach (var blank in (string[])["null", "\"\""])
        {
            var row = Deserialize($$"""{"{{property}}":{{blank}}}""");
            Assert.Null(ValueOf(row, property));
        }

        Assert.Null(ValueOf(Deserialize("{}"), property));
    }

    [Fact]
    public void ANullableField_StillReadsARealCode()
    {
        var row = Deserialize("""{"optionalFraction":"F","optionalPaymentType":"D"}""");

        Assert.Equal(Fraction.Fractions, row.OptionalFraction);
        Assert.Equal(PaymentType.DissentersRights, row.OptionalPaymentType);
    }

    [Fact]
    public void ANullableField_StillRejectsAnUnrecognisedCode()
    {
        // Being blank-legal is not being lenient. The alphabet is as closed as the other seven.
        var error = Assert.Throws<JsonException>(() => Deserialize("""{"optionalFraction":"Z"}"""));

        Assert.Contains("'Z'", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(Fraction), error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------
    // Writing. Nothing in this library calls it; a consumer's own serialization might.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void EveryEnum_WritesBackAsAOneCharacterString()
    {
        var written = JsonSerializer.Serialize(Deserialize(EveryEnumJson), TestJson.Default.EnumRow);

        Assert.Contains("""" "action":"Q" """".Trim(), written, StringComparison.Ordinal);
        Assert.Contains("""" "mandVolu":"W" """".Trim(), written, StringComparison.Ordinal);
        Assert.Contains("""" "optionalFraction":"U" """".Trim(), written, StringComparison.Ordinal);

        // Round-tripped through the writer, the row is the row it started as.
        AssertIsTheEveryEnumRow(Deserialize(written));
    }

    [Fact]
    public void ANullableFieldWithNoValue_WritesJsonNullRatherThanTheEmptyString()
    {
        // Both are blanks the API sends, and null is the one a reader that is not this converter
        // also understands.
        var written = JsonSerializer.Serialize(Deserialize("{}"), TestJson.Default.EnumRow);

        Assert.Contains("""" "optionalFraction":null """".Trim(), written, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------

    private static void AssertIsTheEveryEnumRow(EnumRow row)
    {
        Assert.Equal(Action.PaymentDetailsDeletedBySupplier, row.Action);
        Assert.Equal(AdjustmentStatus.Rescind, row.AdjustmentStatus);
        Assert.Equal(Fraction.RoundDown, row.Fraction);
        Assert.Equal(GlobalStatus.Inactive, row.GlobalStatus);
        Assert.Equal(ListingSource.Secondary, row.ListingSource);
        Assert.Equal(ListingStatus.TpSuspended, row.ListingStatus);
        Assert.Equal(MandVolu.MandVolu, row.MandVolu);
        Assert.Equal(PaymentType.CashAndStock, row.PaymentType);
        Assert.Equal(Voting.Limited, row.Voting);
        Assert.Equal(Fraction.RoundUp, row.OptionalFraction);
        Assert.Equal(PaymentType.Tba, row.OptionalPaymentType);
    }

    private static void AssertReads<T>(string property, string code, Func<EnumRow, T> read, T expected)
    {
        var row = Deserialize($$"""{"{{property}}":"{{code}}"}""");

        Assert.Equal(expected, read(row));
    }

    private static EnumRow Deserialize(string json) =>
        JsonSerializer.Deserialize(json, TestJson.Default.EnumRow)
        ?? throw new InvalidOperationException("The literal is not the JSON null.");

    private static object? ValueOf(EnumRow row, string property) => property switch
    {
        "optionalFraction" => row.OptionalFraction,
        "optionalPaymentType" => row.OptionalPaymentType,
        _ => throw new ArgumentOutOfRangeException(nameof(property), property, null),
    };

    private static ReferenceClient ClientFor(MockHistoricalGateway gateway) => new()
    {
        ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey),
        BaseUrl = gateway.BaseUrl,
    };

    /// <summary>A row carrying all nine enums, plus the two that may also be blank.</summary>
    /// <remarks>
    /// Not one of the endpoint response types — those arrive with #53–#55. What it has to be is a
    /// type with a source-generated <c>JsonTypeInfo</c>, since that is the only shape
    /// <c>HistoricalClient</c>'s two readers accept, and the assembly they live in is trim- and
    /// AOT-analysed with warnings as errors so the reflection overloads do not compile there.
    /// </remarks>
    private sealed class EnumRow
    {
        /// <summary>The <c>Action</c> column.</summary>
        public Action Action { get; set; }

        /// <summary>The <c>AdjustmentStatus</c> column.</summary>
        public AdjustmentStatus AdjustmentStatus { get; set; }

        /// <summary>The <c>Fraction</c> column, as a field that may not be blank.</summary>
        public Fraction Fraction { get; set; }

        /// <summary>The <c>GlobalStatus</c> column.</summary>
        public GlobalStatus GlobalStatus { get; set; }

        /// <summary>The <c>ListingSource</c> column.</summary>
        public ListingSource ListingSource { get; set; }

        /// <summary>The <c>ListingStatus</c> column.</summary>
        public ListingStatus ListingStatus { get; set; }

        /// <summary>The <c>MandVolu</c> column.</summary>
        public MandVolu MandVolu { get; set; }

        /// <summary>The <c>PaymentType</c> column, as a field that may not be blank.</summary>
        public PaymentType PaymentType { get; set; }

        /// <summary>The <c>Voting</c> column.</summary>
        public Voting Voting { get; set; }

        /// <summary>
        /// The <c>Fraction</c> column as #53–#55 will declare it: nullable, with the nullable
        /// converter named on the property rather than inherited from the type.
        /// </summary>
        [JsonConverter(typeof(NullableFractionJsonConverter))]
        public Fraction? OptionalFraction { get; set; }

        /// <summary>The <c>PaymentType</c> column, likewise.</summary>
        [JsonConverter(typeof(NullablePaymentTypeJsonConverter))]
        public PaymentType? OptionalPaymentType { get; set; }
    }

    /// <summary>The source-generated context these tests read and write through.</summary>
    /// <remarks>
    /// Nested and private, so the next file in this project declares its own rather than adding a
    /// <c>[JsonSerializable]</c> here and coupling two files that share nothing else. The camel-case
    /// policy is what maps <c>ListingSource</c> to the wire's <c>listingSource</c>.
    /// </remarks>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(EnumRow))]
    [JsonSerializable(typeof(List<EnumRow>))]
    private sealed partial class TestJson : JsonSerializerContext
    {
    }
}
