using System.Text.Json;
using System.Text.Json.Serialization;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Historical.Json;
using NodaTime;
using Xunit;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// The converters that bridge the <c>metadata.*</c> wire spellings to this library's types.
/// </summary>
/// <remarks>
/// <para>
/// Every case here failed on a first attempt at this code, which is why each is written down. The
/// two that matter most: a <see cref="Schema"/>-keyed dictionary does not read wire-string keys
/// without a converter that overrides <c>ReadAsPropertyName</c>, and it <em>does</em> read C# enum
/// names — so a test written with <c>"Ohlcv1S"</c> instead of <c>"ohlcv-1s"</c> passes while the
/// real API's response throws. And no single NodaTime pattern parses every timestamp shape
/// upstream accepts.
/// </para>
/// </remarks>
public sealed partial class MetadataJsonConverterTests
{
    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
        Converters = [
            typeof(SchemaJsonConverter),
            typeof(FeedModeJsonConverter),
            typeof(DatasetConditionJsonConverter),
            typeof(InstantJsonConverter),
            typeof(LocalDateJsonConverter),
            typeof(DateTimeRangeJsonConverter),
        ])]
    [JsonSerializable(typeof(List<Schema>))]
    [JsonSerializable(typeof(Dictionary<Schema, decimal>))]
    [JsonSerializable(typeof(FeedMode))]
    [JsonSerializable(typeof(DatasetCondition))]
    [JsonSerializable(typeof(Instant))]
    [JsonSerializable(typeof(LocalDate))]
    [JsonSerializable(typeof(LocalDate?))]
    [JsonSerializable(typeof(DateTimeRange))]
    private sealed partial class Json : JsonSerializerContext
    {
    }

    // ---------------------------------------------------------------- Schema

    [Fact]
    public void Schema_ReadsWireStringsInTheValuePosition()
    {
        var actual = JsonSerializer.Deserialize("""["mbo","ohlcv-1s","cmbp-1"]""", Json.Default.ListSchema);

        Assert.Equal([Schema.Mbo, Schema.Ohlcv1S, Schema.Cmbp1], actual);
    }

    /// <summary>
    /// The trap this converter exists for. Without <c>ReadAsPropertyName</c>, this input throws
    /// <see cref="JsonException"/> — while <c>{"Ohlcv1S":0.1}</c>, which the API never sends,
    /// succeeds. A test written with C# enum names would pass against a converter that is wrong.
    /// </summary>
    [Fact]
    public void Schema_ReadsWireStringsInTheKeyPosition()
    {
        var actual = JsonSerializer.Deserialize(
            """{"ohlcv-1s":0.0000019,"mbp-1":0.0000032}""", Json.Default.DictionarySchemaDecimal);

        Assert.Equal(0.0000019m, actual![Schema.Ohlcv1S]);
        Assert.Equal(0.0000032m, actual[Schema.Mbp1]);
    }

    /// <summary>
    /// The issue's Definition of done: "A schema the codec cannot name must be an error at the
    /// boundary, not an unmapped enum value that reaches a caller as <c>0</c>." Asserted in both
    /// positions, because they are two different code paths.
    /// </summary>
    [Fact]
    public void Schema_ThrowsForAnUnknownName_InEitherPosition()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize("""["not-a-schema"]""", Json.Default.ListSchema));
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize("""{"not-a-schema":1.0}""", Json.Default.DictionarySchemaDecimal));
    }

    [Fact]
    public void Schema_WritesTheCodecsWireString()
    {
        var actual = JsonSerializer.Serialize(
            new Dictionary<Schema, decimal> { [Schema.Ohlcv1S] = 0.25m }, Json.Default.DictionarySchemaDecimal);

        Assert.Equal("""{"ohlcv-1s":0.25}""", actual);
    }

    // ------------------------------------------------------- FeedMode, condition

    [Fact]
    public void FeedMode_ReadsItsHyphenatedSpelling()
    {
        Assert.Equal(
            FeedMode.HistoricalStreaming,
            JsonSerializer.Deserialize("\"historical-streaming\"", Json.Default.FeedMode));
    }

    [Fact]
    public void DatasetCondition_ReadsItsSpelling()
    {
        Assert.Equal(
            DatasetCondition.Degraded,
            JsonSerializer.Deserialize("\"degraded\"", Json.Default.DatasetCondition));
    }

    [Fact]
    public void FeedModeAndCondition_ThrowForAnUnknownName()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize("\"historical_streaming\"", Json.Default.FeedMode));
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize("\"AVAILABLE\"", Json.Default.DatasetCondition));
    }

    // --------------------------------------------------------------- Instant

    /// <summary>
    /// All six shapes upstream's <c>deserialize_date_time</c> accepts
    /// (<c>databento-rs/src/deserialize.rs:7-19</c>). The first two are the ISO 8601 branch — and
    /// they need two different NodaTime patterns, because <c>InstantPattern.ExtendedIso</c>
    /// rejects a value with no zone designator while <c>LocalDateTimePattern.ExtendedIso</c>
    /// rejects one that has a <c>Z</c>. The last four are the legacy branch, which NodaTime cannot
    /// express as one pattern because it has no optional-section syntax.
    /// </summary>
    [Fact]
    public void Instant_ReadsEverySpellingUpstreamAccepts()
    {
        var expected = Instant.FromUtc(2023, 6, 14, 10, 0, 0);

        Assert.Equal(expected, Read("2023-06-14T10:00:00.000000000Z"));
        Assert.Equal(expected, Read("2023-06-14T10:00:00Z"));
        Assert.Equal(expected, Read("2023-06-14T10:00:00"));
        Assert.Equal(expected, Read("2023-06-14 10:00:00"));
        Assert.Equal(expected, Read("2023-06-14 10:00:00+00:00"));
        Assert.Equal(expected, Read("2023-06-14 10:00:00.000000+00:00"));

        static Instant Read(string value) =>
            JsonSerializer.Deserialize($"\"{value}\"", Json.Default.Instant);
    }

    /// <summary>
    /// A zone-less ISO value is assumed UTC, exactly as upstream's
    /// <c>PrimitiveDateTime::parse(...).assume_utc()</c> does — not read in the machine's local
    /// zone, which would make the result depend on where the test ran.
    /// </summary>
    [Fact]
    public void Instant_AssumesUtcForAZonelessValue()
    {
        Assert.Equal(
            Instant.FromUtc(2023, 6, 14, 10, 0, 0).Plus(Duration.FromNanoseconds(123_456_000)),
            JsonSerializer.Deserialize("\"2023-06-14T10:00:00.123456\"", Json.Default.Instant));
    }

    [Fact]
    public void Instant_KeepsSubsecondPrecisionFromTheLegacySpelling()
    {
        Assert.Equal(
            Instant.FromUtc(2023, 6, 14, 10, 0, 0).Plus(Duration.FromNanoseconds(123_456_000)),
            JsonSerializer.Deserialize("\"2023-06-14 10:00:00.123456\"", Json.Default.Instant));
    }

    [Fact]
    public void Instant_ThrowsForAnUnparseableValue()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize("\"14/06/2023 10:00\"", Json.Default.Instant));
    }

    // ------------------------------------------------------------- LocalDate

    [Fact]
    public void LocalDate_ReadsTheDateFormatUpstreamUses()
    {
        Assert.Equal(
            new LocalDate(2023, 6, 14),
            JsonSerializer.Deserialize("\"2023-06-14\"", Json.Default.LocalDate));
    }

    /// <summary>
    /// <c>last_modified_date</c> is <see langword="null"/> when the condition is
    /// <see cref="DatasetCondition.Missing"/> (<c>metadata.rs:301-302</c>), so the nullable case
    /// is the one that actually arrives.
    /// </summary>
    [Fact]
    public void LocalDate_ReadsNullAsNoDate()
    {
        Assert.Null(JsonSerializer.Deserialize("null", Json.Default.NullableLocalDate));
    }

    [Fact]
    public void LocalDate_ThrowsForATimestamp()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize("\"2023-06-14T10:00:00Z\"", Json.Default.LocalDate));
    }

    // --------------------------------------------------------- DateTimeRange

    /// <summary>
    /// <see cref="DateTimeRange"/> was built in #33 purely to render onto a request. It is read
    /// back out of a body for the first time here: <c>DatasetRange.schema</c> maps each schema to
    /// one of these (<c>metadata.rs:317</c>).
    /// </summary>
    [Fact]
    public void DateTimeRange_ReadsAStartAndEndObject()
    {
        var actual = JsonSerializer.Deserialize(
            """{"start":"2023-06-14T00:00:00Z","end":"2023-06-15T00:00:00Z"}""",
            Json.Default.DateTimeRange);

        Assert.Equal(Instant.FromUtc(2023, 6, 14, 0, 0, 0), actual.Start);
        Assert.Equal(Instant.FromUtc(2023, 6, 15, 0, 0, 0), actual.End);
    }

    /// <summary>
    /// The range types reject an empty or inverted range at construction (#33), and reading one
    /// off the wire must not be a way around that.
    /// </summary>
    [Fact]
    public void DateTimeRange_ThrowsForAnInvertedRange()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            """{"start":"2023-06-15T00:00:00Z","end":"2023-06-14T00:00:00Z"}""",
            Json.Default.DateTimeRange));
    }
}
