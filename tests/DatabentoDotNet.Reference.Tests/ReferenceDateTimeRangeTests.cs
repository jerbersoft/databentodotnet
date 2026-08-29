using System.Net;
using DatabentoDotNet.Historical;
using DatabentoDotNet.Historical.Tests;
using NodaTime;

namespace DatabentoDotNet.Reference.Tests;

/// <summary>
/// Conformance tests for <see cref="ReferenceDateTimeRange"/>, the range the three reference
/// <c>get_range</c> endpoints take.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rendering half of these does not assert on <see cref="ReferenceDateTimeRange.ToFormParameters"/>
/// alone.</b> The property under test is what arrives at the server, and a list of key/value pairs
/// is one refactor away from that: <c>FormUrlEncodedContent</c> is what turns pairs into a body, and
/// nothing in a unit assertion on the list would notice if it wrote an empty <c>end=</c> anyway.
/// So the open-versus-closed key set is asserted on <see cref="MockHistoricalGateway"/>'s recorded
/// form, over a real socket, exactly as the issue's definition of done specifies.
/// </para>
/// <para>
/// The slug is <c>adjustment_factors.get_range</c>, a real one, though no client sends it until
/// <see href="https://github.com/jerbersoft/databentodotnet/issues/53">#53</see>. Requests go
/// through <see cref="ReferenceClient.Transport"/> for the same reason
/// <c>ReferenceClientTests</c> does: this milestone has no endpoint to call yet.
/// </para>
/// </remarks>
public class ReferenceDateTimeRangeTests
{
    private const string GetRange = "adjustment_factors.get_range";

    /// <summary>
    /// 2020-12-28T13:00:00Z plus one nanosecond — the value CLAUDE.md uses to show that a
    /// <c>DateTime</c> tick (100 ns) cannot represent a DBN timestamp. Through a
    /// <c>DateTimeOffset</c> it renders as <c>…000</c>; through <see cref="Instant"/> it survives.
    /// </summary>
    private const long OddNanosecond = 1_609_160_400_000_000_001;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------------------------
    // The definition of done: what reaches the wire, asserted on the recorded form.
    // ------------------------------------------------------------------------------------

    [Fact]
    public async Task OpenRange_SendsStartAndNoEndAtAll()
    {
        var recorded = await SendAsync(ReferenceDateTimeRange.StartingAt(Instant.FromUtc(2024, 3, 15, 9, 30)));

        // The key set, not the value: an `end=` with an empty value is a different request, and it
        // would satisfy any assertion that only looked at what `end` mapped to.
        Assert.Equal(["start"], recorded.Form.Keys.Order());
        Assert.Equal("1710495000000000000", recorded.Form["start"]);
    }

    [Fact]
    public async Task ClosedRange_SendsBothEnds()
    {
        var recorded = await SendAsync(ReferenceDateTimeRange.Between(
            Instant.FromUtc(2024, 3, 15, 9, 30),
            Instant.FromUtc(2024, 3, 15, 16, 0)));

        Assert.Equal(["end", "start"], recorded.Form.Keys.Order());
        Assert.Equal("1710495000000000000", recorded.Form["start"]);
        Assert.Equal("1710518400000000000", recorded.Form["end"]);
    }

    [Fact]
    public async Task NanosecondStart_ReachesTheServerUntruncated()
    {
        var recorded = await SendAsync(ReferenceDateTimeRange.FromUnixNanoseconds(OddNanosecond, null));

        // …001, not …000. This is the assertion DateRangeTests makes for the historical types and
        // LiveClientSubscriptionTests makes for the live gateway's `start`.
        Assert.Equal("1609160400000000001", recorded.Form["start"]);
    }

    private static async Task<RecordedRequest> SendAsync(ReferenceDateTimeRange range)
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(GetRange, MockHistoricalResponse.Json("[]"));

        await using var client = new ReferenceClient
        {
            ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey),
            BaseUrl = gateway.BaseUrl,
        };

        using var response = await client.Transport.SendAsync(
            HttpMethod.Post, GetRange, range.ToFormParameters(), cancellationToken: Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single(gateway.Requests);
    }

    // ------------------------------------------------------------------------------------
    // The rendering itself, without a socket in the way.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void ToFormParameters_OpenRange_IsOneField()
    {
        var fields = ReferenceDateTimeRange.StartingAt(NodaConstants.UnixEpoch).ToFormParameters();

        var only = Assert.Single(fields);
        Assert.Equal("start", only.Key);
        Assert.Equal("0", only.Value);
    }

    [Fact]
    public void ToFormParameters_ClosedRange_IsStartThenEnd()
    {
        var fields = ReferenceDateTimeRange.FromUnixNanoseconds(1, 2).ToFormParameters();

        // Order is upstream's push order (reference.rs:234-250): Start, then End.
        Assert.Equal([new KeyValuePair<string, string>("start", "1"), new("end", "2")], fields);
    }

    [Fact]
    public void EndUnixNanoseconds_IsNullExactlyWhenTheRangeIsOpen()
    {
        Assert.Null(ReferenceDateTimeRange.StartingAt(NodaConstants.UnixEpoch).EndUnixNanoseconds);
        Assert.Equal(2, ReferenceDateTimeRange.FromUnixNanoseconds(1, 2).EndUnixNanoseconds);
    }

    // ------------------------------------------------------------------------------------
    // The row only Instant can pass: two Unix-nanosecond integers one apart round-trip back
    // exactly. Through DateTimeOffset (100 ns resolution) they would collapse to one value.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void FromUnixNanoseconds_OneNanosecondApart_RoundTripsExactlyThroughInstant()
    {
        var range = ReferenceDateTimeRange.FromUnixNanoseconds(OddNanosecond, OddNanosecond + 1);

        Assert.Equal(OddNanosecond, range.StartUnixNanoseconds);
        Assert.Equal(OddNanosecond + 1, range.EndUnixNanoseconds);
        Assert.NotEqual(range.Start, range.End);
    }

    [Fact]
    public void FromUnixNanoseconds_OpenRange_RoundTripsItsStart()
    {
        var range = ReferenceDateTimeRange.FromUnixNanoseconds(OddNanosecond, null);

        Assert.Equal(OddNanosecond, range.StartUnixNanoseconds);
        Assert.Null(range.End);
        Assert.Null(range.EndUnixNanoseconds);
    }

    // ------------------------------------------------------------------------------------
    // Construction validation: an inverted range is rejected, an absent end is not.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void Between_InvertedRange_ThrowsNamingBothInstants()
    {
        var start = Instant.FromUtc(2024, 3, 15, 16, 0);
        var end = Instant.FromUtc(2024, 3, 15, 9, 30);

        var error = Assert.Throws<ArgumentException>(() => ReferenceDateTimeRange.Between(start, end));

        Assert.Equal("end", error.ParamName);
        Assert.Contains(start.ToString(), error.Message, StringComparison.Ordinal);
        Assert.Contains(end.ToString(), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Between_EmptyRange_Throws()
    {
        var instant = Instant.FromUtc(2024, 3, 15, 9, 30);

        var error = Assert.Throws<ArgumentException>(() => ReferenceDateTimeRange.Between(instant, instant));

        Assert.Equal("end", error.ParamName);
    }

    [Fact]
    public void FromUnixNanoseconds_InvertedRange_Throws()
    {
        var error = Assert.Throws<ArgumentException>(() => ReferenceDateTimeRange.FromUnixNanoseconds(2, 1));

        Assert.Equal("endUnixNanoseconds", error.ParamName);
    }

    [Fact]
    public void StartingAt_AbsentEnd_IsNotAnInvertedRange()
    {
        // The whole point of the type: the one construction DateTimeRange cannot express is also
        // the one this must never refuse. Asserted at both extremes of the representable timeline
        // rather than at a comfortable value in the middle.
        Assert.Null(ReferenceDateTimeRange.StartingAt(NodaConstants.UnixEpoch).End);
        Assert.Null(ReferenceDateTimeRange.StartingAt(Instant.FromUtc(2262, 1, 1, 0, 0)).End);
        Assert.Null(ReferenceDateTimeRange.StartingAt(Instant.FromUtc(1677, 12, 31, 0, 0)).End);
    }

    // ------------------------------------------------------------------------------------
    // The conversion from the M3 type.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void From_DateTimeRange_KeepsBothEndsExactly()
    {
        var source = DateTimeRange.FromUnixNanoseconds(OddNanosecond, OddNanosecond + 1);

        var range = ReferenceDateTimeRange.From(source);

        Assert.Equal(source.Start, range.Start);
        Assert.Equal(source.End, range.End);
        Assert.Equal(source.StartUnixNanoseconds, range.StartUnixNanoseconds);
        Assert.Equal(source.EndUnixNanoseconds, range.EndUnixNanoseconds);
    }

    [Fact]
    public void From_DateTimeRange_ProducesARangeThatIsNeverOpen()
    {
        var range = ReferenceDateTimeRange.From(DateTimeRange.OnDay(new LocalDate(2024, 3, 15)));

        Assert.NotNull(range.End);
        Assert.Equal(2, range.ToFormParameters().Count);
    }

    [Fact]
    public void From_DefaultDateTimeRange_ThrowsRatherThanConvertingNothing()
    {
        // default(DateTimeRange) has Start == End == the Unix epoch, which Validate would reject
        // anyway — but with a message about an inverted range, which is not what went wrong.
        var error = Assert.Throws<ArgumentException>(() => ReferenceDateTimeRange.From(default));

        Assert.Equal("range", error.ParamName);
        Assert.Contains("default DateTimeRange", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------
    // A default(ReferenceDateTimeRange) — the struct's implicit parameterless constructor, which
    // cannot be suppressed. Unlike DateTimeRange, its field values are indistinguishable from a
    // legitimately open range, which is why the type carries a construction flag.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void Default_RefusesToRenderWireValues()
    {
        var range = default(ReferenceDateTimeRange);

        Assert.Throws<InvalidOperationException>(() => range.StartUnixNanoseconds);
        Assert.Throws<InvalidOperationException>(() => range.EndUnixNanoseconds);
        Assert.Throws<InvalidOperationException>(range.ToFormParameters);
    }

    [Fact]
    public void Default_IsNotEqualToTheOpenRangeItsFieldValuesLookLike()
    {
        // Both carry Start == the Unix epoch and End == null. Were they equal, a default value
        // would render as a request for every adjustment factor recorded since 1970 — against an
        // endpoint that bills by what it returns.
        var range = default(ReferenceDateTimeRange);
        var epoch = ReferenceDateTimeRange.StartingAt(NodaConstants.UnixEpoch);

        Assert.Equal(range.Start, epoch.Start);
        Assert.Equal(range.End, epoch.End);
        Assert.NotEqual(range, epoch);
    }

    [Fact]
    public void Default_EqualityHashingAndToStringAreDeliberatelyLeftUnguarded()
    {
        var range = default(ReferenceDateTimeRange);

        Assert.Equal(default, range);
        _ = range.GetHashCode();
        Assert.Equal("ReferenceDateTimeRange { (default) }", range.ToString());
    }

    [Fact]
    public void ToString_SaysWhichEndIsOpen()
    {
        Assert.Contains("(open)", ReferenceDateTimeRange.StartingAt(NodaConstants.UnixEpoch).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("(open)", ReferenceDateTimeRange.FromUnixNanoseconds(1, 2).ToString(), StringComparison.Ordinal);
    }
}
