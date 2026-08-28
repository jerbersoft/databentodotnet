using DatabentoDotNet.Dbn;
using NodaTime;
using Xunit;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// How the three parameter types render onto the wire.
/// </summary>
/// <remarks>
/// These assert the rendering in isolation. <c>MetadataClientPostTests</c> asserts the same
/// rendering after it has been through the transport and Kestrel, which is the pair that catches
/// an encoding applied twice or not at all.
/// </remarks>
public sealed class MetadataParamsTests
{
    private static readonly DateTimeRange Range = DateTimeRange.Between(
        Instant.FromUtc(2023, 7, 4, 0, 0, 0), Instant.FromUtc(2023, 7, 5, 0, 0, 0));

    private static MetadataQueryParams Params() => new()
    {
        Dataset = "XNAS.ITCH",
        Symbols = Symbols.From(["AAPL", "MSFT"]),
        Schema = Schema.Trades,
        DateTimeRange = Range,
    };

    /// <summary>
    /// The order is upstream's push order (<c>metadata.rs:462-471</c>), which puts
    /// <c>stype_in</c> before <c>symbols</c> even though the struct declares them the other way
    /// round.
    /// </summary>
    [Fact]
    public void MetadataQueryParams_RendersUpstreamsFieldsInUpstreamsOrder()
    {
        Assert.Equal(
            [
                new("dataset", "XNAS.ITCH"),
                new("schema", "trades"),
                new("stype_in", "raw_symbol"),
                new("symbols", "AAPL,MSFT"),
                new("start", "1688428800000000000"),
                new("end", "1688515200000000000"),
            ],
            Params().ToFormParameters());
    }

    /// <summary>
    /// Upstream omits the field entirely rather than sending an empty or zero one
    /// (<c>historical.rs:388-396</c>).
    /// </summary>
    [Fact]
    public void MetadataQueryParams_OmitsLimitWhenItIsNotSet_AndAppendsItLastWhenItIs()
    {
        Assert.DoesNotContain(Params().ToFormParameters(), p => p.Key == "limit");

        var limited = (Params() with { Limit = 1_000UL }).ToFormParameters();

        Assert.Equal(new KeyValuePair<string, string>("limit", "1000"), limited[^1]);
    }

    /// <summary>
    /// <c>stype_in</c> defaults to <c>raw_symbol</c> (<c>metadata.rs:340-342</c>) and is always
    /// sent — upstream pushes it unconditionally, so it is on the wire even when the caller never
    /// named it.
    /// </summary>
    [Fact]
    public void MetadataQueryParams_DefaultsStypeInToRawSymbol_AndSendsItEvenSo()
    {
        Assert.Equal(SType.RawSymbol, Params().StypeIn);
        Assert.Contains(Params().ToFormParameters(), p => p is { Key: "stype_in", Value: "raw_symbol" });

        var overridden = (Params() with { StypeIn = SType.InstrumentId }).ToFormParameters();

        Assert.Contains(overridden, p => p is { Key: "stype_in", Value: "instrument_id" });
    }

    /// <summary>
    /// <see cref="Symbols.ToApiString"/> never chunks, unlike the live protocol's
    /// <see cref="Symbols.ToChunks"/>. A form field carries no length restriction, so a set larger
    /// than the live chunk size still renders as one comma-joined value.
    /// </summary>
    [Fact]
    public void MetadataQueryParams_RendersSymbolsUnchunked_EvenBeyondTheLiveChunkSize()
    {
        var many = Enumerable.Range(0, Symbols.ChunkSize + 1).Select(i => $"SYM{i}").ToArray();

        var rendered = Assert.Single(
            (Params() with { Symbols = Symbols.From(many) }).ToFormParameters(),
            p => p.Key == "symbols");

        Assert.Equal(string.Join(',', many), rendered.Value);
    }

    [Fact]
    public void MetadataQueryParams_RendersAllSymbolsAsTheWireSentinel()
    {
        Assert.Contains(
            (Params() with { Symbols = Symbols.All }).ToFormParameters(),
            p => p is { Key: "symbols", Value: Symbols.AllWireValue });
    }

    /// <summary>Upstream's <c>NonZeroU64</c> (<c>metadata.rs:345</c>), which C# has no type for.</summary>
    [Fact]
    public void MetadataQueryParams_RejectsAZeroLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Params() with { Limit = 0UL });
    }

    [Fact]
    public void ListFieldsParams_RendersEncodingAndSchema_AndOmitsAnAbsentDataset()
    {
        var required = new ListFieldsParams { Encoding = Encoding.Dbn, Schema = Schema.Mbp10 };

        Assert.Equal(
            [new("encoding", "dbn"), new("schema", "mbp-10")],
            required.ToQueryParameters());

        Assert.Equal(
            [new("encoding", "dbn"), new("schema", "mbp-10"), new("dataset", "XNAS.ITCH")],
            (required with { Dataset = "XNAS.ITCH" }).ToQueryParameters());
    }

    /// <summary>
    /// The rendered <c>end_date</c> is the day <em>before</em> the range's exclusive
    /// <see cref="DateRange.End"/>, because this endpoint reads <c>end_date</c> as inclusive
    /// (#44 verified it against the real API; #45 is the conversion). Every other endpoint taking
    /// a <see cref="DateRange"/> sends <c>End</c> verbatim — see
    /// <c>MetadataClientGetTests.ListDatasets_SendsTheExclusiveEndVerbatim…</c> for the other half.
    /// </summary>
    [Fact]
    public void GetDatasetConditionParams_RendersTheInclusiveEndDate_AndOmitsAnAbsentRange()
    {
        var required = new GetDatasetConditionParams { Dataset = "XNAS.ITCH" };

        Assert.Equal([new("dataset", "XNAS.ITCH")], required.ToQueryParameters());

        var ranged = required with
        {
            DateRange = DateRange.Between(new LocalDate(2023, 7, 4), new LocalDate(2023, 7, 6)),
        };

        Assert.Equal(
            [
                new("dataset", "XNAS.ITCH"),
                new("start_date", "2023-07-04"),
                new("end_date", "2023-07-05"),
            ],
            ranged.ToQueryParameters());
    }

    /// <summary>
    /// The single-day case, which is the one a caller is most likely to write and the one the
    /// defect was most visible in: <c>OnDay(d)</c> is <c>(d, d + 1)</c>, and against an endpoint
    /// with an inclusive <c>end_date</c> that has to render as <c>d</c> on both parameters.
    /// A range this narrow is also the only place the conversion could invert the range, and it
    /// cannot: <c>end_date</c> lands exactly on <c>start_date</c> rather than before it.
    /// </summary>
    [Fact]
    public void GetDatasetConditionParams_ForASingleDay_SendsThatDayAsBothDates()
    {
        var parameters = new GetDatasetConditionParams
        {
            Dataset = "XNAS.ITCH",
            DateRange = DateRange.OnDay(new LocalDate(2023, 7, 4)),
        };

        Assert.Equal(
            [
                new("dataset", "XNAS.ITCH"),
                new("start_date", "2023-07-04"),
                new("end_date", "2023-07-04"),
            ],
            parameters.ToQueryParameters());
    }

    /// <summary>
    /// A default <see cref="DateRange"/> still cannot reach the wire through this endpoint's
    /// renderer. Worth its own test rather than assumed from the other endpoints': the inclusive
    /// path formats <c>End.PlusDays(-1)</c> directly instead of reading the guarded
    /// <see cref="DateRange.EndIsoDate"/>, so it carries its own <c>EnsureUsable()</c> call and
    /// that call is the only thing standing between a never-constructed range and a
    /// plausible-looking <c>"0000-12-31"</c>.
    /// </summary>
    [Fact]
    public void GetDatasetConditionParams_WithADefaultDateRange_Throws()
    {
        var parameters = new GetDatasetConditionParams
        {
            Dataset = "XNAS.ITCH",
            DateRange = default(DateRange),
        };

        Assert.Throws<InvalidOperationException>(() => parameters.ToQueryParameters());
    }
}
