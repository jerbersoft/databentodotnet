using DatabentoDotNet.Dbn;
using NodaTime;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// Tests for <see cref="GetRangeParams"/> in isolation — the rendering, the zero-limit guard, and
/// the two conversions it feeds.
/// </summary>
/// <remarks>
/// The wire-level pair to <c>TimeseriesClientTests</c>, which asserts the same rendering after it
/// has been through the transport and Kestrel. Together they catch an encoding applied twice or
/// not at all; <c>MetadataParamsTests</c> makes the same split for the billing type.
/// </remarks>
public sealed class GetRangeParamsTests
{
    private static readonly DateTimeRange Range = DateTimeRange.Between(
        Instant.FromUtc(2023, 7, 4, 0, 0, 0), Instant.FromUtc(2023, 7, 5, 0, 0, 0));

    /// <summary>
    /// Upstream's push order (<c>timeseries.rs:128-138</c>), asserted as a sequence rather than
    /// field by field. The order makes no difference to the API and every difference to telling
    /// this rendering apart from a plausible one.
    /// </summary>
    [Fact]
    public void ToFormParameters_RendersUpstreamsFieldsInUpstreamsOrder()
    {
        var rendered = Params().ToFormParameters();

        Assert.Equal(
            ["dataset", "schema", "encoding", "compression", "stype_in", "stype_out", "symbols", "start", "end"],
            rendered.Select(pair => pair.Key));

        Assert.Equal(
            ["GLBX.MDP3", "trades", "dbn", "zstd", "raw_symbol", "instrument_id", "ESH4",
             "1688428800000000000", "1688515200000000000"],
            rendered.Select(pair => pair.Value));
    }

    /// <summary>
    /// <c>limit</c> is appended last when set and omitted entirely when not — never sent empty,
    /// which the API would read as a value.
    /// </summary>
    [Fact]
    public void ToFormParameters_AppendsLimitLastOrNotAtAll()
    {
        Assert.DoesNotContain("limit", Params().ToFormParameters().Select(pair => pair.Key));

        var withLimit = (Params() with { Limit = 42 }).ToFormParameters();
        Assert.Equal("limit", withLimit[^1].Key);
        Assert.Equal("42", withLimit[^1].Value);
    }

    /// <summary>
    /// Zero is refused at construction. #38 probed both endpoints and they disagree about it: the
    /// billing endpoints answer <c>422 Input should be greater than 0</c>, while
    /// <c>timeseries.get_range</c> accepts it, returns the data anyway, and warns that it found
    /// none. Refusing here is what stops one request behaving two ways.
    /// </summary>
    [Fact]
    public void Limit_RejectsZeroButAcceptsOneAndUnset()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Params() with { Limit = 0 });

        Assert.Equal(1UL, (Params() with { Limit = 1 }).Limit);
        Assert.Null(Params().Limit);
    }

    /// <summary>
    /// <see cref="GetRangeParams.ToQuery"/> carries every billable field across and drops exactly
    /// one — so a caller prices the request they are about to send.
    /// </summary>
    [Fact]
    public void ToQuery_CarriesEveryBillableFieldAndDropsOnlyStypeOut()
    {
        var request = Params() with { StypeOut = SType.RawSymbol, StypeIn = SType.Continuous, Limit = 9 };

        var priced = request.ToQuery();

        Assert.Equal(request.Dataset, priced.Dataset);
        Assert.Equal(request.Symbols, priced.Symbols);
        Assert.Equal(request.Schema, priced.Schema);
        Assert.Equal(request.DateTimeRange, priced.DateTimeRange);
        Assert.Equal(request.StypeIn, priced.StypeIn);
        Assert.Equal(request.Limit, priced.Limit);

        // The billing form is upstream's, and it never carried stype_out, encoding or compression.
        var keys = priced.ToFormParameters().Select(pair => pair.Key).ToList();
        Assert.DoesNotContain("stype_out", keys);
        Assert.DoesNotContain("encoding", keys);
        Assert.DoesNotContain("compression", keys);
    }

    /// <summary>
    /// The narrowed query renders the same range and symbols as the download it came from. A
    /// conversion that priced a different window would be worse than no conversion, since it would
    /// look authoritative.
    /// </summary>
    [Fact]
    public void ToQuery_PricesTheSameWindowAsTheDownloadSends()
    {
        var request = Params() with { Limit = 5 };

        var download = request.ToFormParameters().ToDictionary(pair => pair.Key, pair => pair.Value);
        var priced = request.ToQuery().ToFormParameters().ToDictionary(pair => pair.Key, pair => pair.Value);

        foreach (var field in new[] { "dataset", "schema", "stype_in", "symbols", "start", "end", "limit" })
        {
            Assert.Equal(download[field], priced[field]);
        }
    }

    /// <summary>
    /// The overload #38 added reads <c>stype_out</c> off the request instead of taking it as an
    /// argument — which is the drift it exists to remove.
    /// </summary>
    [Fact]
    public void ResolveParamsFromQuery_ReadsStypeOutFromTheRequest()
    {
        var request = Params() with { StypeOut = SType.RawSymbol, StypeIn = SType.Continuous };

        var resolve = ResolveParams.FromQuery(request);

        Assert.Equal(request.Dataset, resolve.Dataset);
        Assert.Equal(request.Symbols, resolve.Symbols);
        Assert.Equal(SType.Continuous, resolve.StypeIn);
        Assert.Equal(SType.RawSymbol, resolve.StypeOut);
        Assert.Equal(request.DateTimeRange.ToDateRange(), resolve.DateRange);
    }

    /// <summary>
    /// The two overloads agree when handed the same values, so preferring the new one is a
    /// convenience rather than a behaviour change.
    /// </summary>
    [Fact]
    public void BothResolveOverloads_AgreeWhenGivenTheSameStypeOut()
    {
        var request = Params() with { StypeOut = SType.RawSymbol };

        Assert.Equal(
            ResolveParams.FromQuery(request.ToQuery(), SType.RawSymbol),
            ResolveParams.FromQuery(request));
    }

    /// <summary>Both conversions guard their argument.</summary>
    [Fact]
    public void ResolveParamsFromQuery_RejectsNull() =>
        Assert.Throws<ArgumentNullException>(() => ResolveParams.FromQuery((GetRangeParams)null!));

    private static GetRangeParams Params() => new()
    {
        Dataset = "GLBX.MDP3",
        Symbols = Symbols.From("ESH4"),
        Schema = Schema.Trades,
        DateTimeRange = Range,
    };
}
