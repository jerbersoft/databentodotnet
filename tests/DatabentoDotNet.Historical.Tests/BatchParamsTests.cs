using DatabentoDotNet.Dbn;
using NodaTime;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// Tests for the three <c>batch.*</c> parameter types in isolation — what they render, and what
/// they refuse.
/// </summary>
/// <remarks>
/// The wire-level pair to <c>BatchClientTests</c>, which asserts the same rendering after it has
/// been through the transport and Kestrel. Together they catch an encoding applied twice or not at
/// all; <c>GetRangeParamsTests</c> and <c>MetadataParamsTests</c> make the same split for their own
/// types.
/// </remarks>
public sealed class BatchParamsTests
{
    private static readonly DateTimeRange Range = DateTimeRange.Between(
        Instant.FromUtc(2023, 6, 14, 0, 0, 0), Instant.FromUtc(2023, 6, 17, 0, 0, 0));

    /// <summary>
    /// Every field a submitted job carries, in upstream's push order and with its documented
    /// spelling, asserted as a sequence rather than field by field.
    /// </summary>
    /// <remarks>
    /// This is #39's "every submitted-job parameter appears in the form with its documented
    /// spelling, table-driven". A sequence rather than a set because the order is upstream's
    /// (<c>batch.rs:68-89</c>) and is what makes the rendered body byte-comparable with its output;
    /// the three optional fields trail the required ones because upstream appends them through
    /// separate <c>add_to_form</c> calls.
    /// </remarks>
    [Fact]
    public void SubmitJob_RendersUpstreamsFieldsInUpstreamsOrder()
    {
        var rendered = Full().ToFormParameters();

        Assert.Equal(
            [
                "dataset", "schema", "encoding", "compression", "pretty_px", "pretty_ts",
                "map_symbols", "split_symbols", "delivery", "stype_in", "stype_out", "symbols",
                "start", "end", "limit", "split_size", "split_duration",
            ],
            rendered.Select(pair => pair.Key));

        Assert.Equal(
            [
                "XNAS.ITCH", "trades", "csv", "zstd", "true", "true",
                "false", "true", "download", "raw_symbol", "instrument_id", "TSLA,MSFT",
                "1686700800000000000", "1686960000000000000", "7", "2000000000", "week",
            ],
            rendered.Select(pair => pair.Value));
    }

    /// <summary>
    /// The four defaults a minimal job carries. A job built from only its
    /// <see langword="required"/> properties is the one upstream's builder would submit.
    /// </summary>
    [Fact]
    public void SubmitJob_DefaultsMatchUpstreamsBuilderDefaults()
    {
        var rendered = Minimal().ToFormParameters().ToDictionary(pair => pair.Key, pair => pair.Value);

        Assert.Equal("dbn", rendered["encoding"]);
        Assert.Equal("zstd", rendered["compression"]);
        Assert.Equal("false", rendered["pretty_px"]);
        Assert.Equal("false", rendered["pretty_ts"]);
        Assert.Equal("false", rendered["split_symbols"]);
        Assert.Equal("download", rendered["delivery"]);
        Assert.Equal("raw_symbol", rendered["stype_in"]);
        Assert.Equal("instrument_id", rendered["stype_out"]);
        Assert.Equal("day", rendered["split_duration"]);

        // The two that are omitted rather than defaulted, and would be read as values if sent empty.
        Assert.DoesNotContain("limit", rendered.Keys);
        Assert.DoesNotContain("split_size", rendered.Keys);
    }

    /// <summary>
    /// <c>map_symbols</c> left unset is not <see langword="false"/>: it follows the encoding, which
    /// is upstream's <c>unwrap_or(encoding != Encoding::Dbn)</c>. A caller who sets it explicitly
    /// gets what they asked for.
    /// </summary>
    [Theory]
    [InlineData(Encoding.Dbn, null, "false")]
    [InlineData(Encoding.Csv, null, "true")]
    [InlineData(Encoding.Json, null, "true")]
    [InlineData(Encoding.Csv, false, "false")]
    [InlineData(Encoding.Dbn, true, "true")]
    public void SubmitJob_MapSymbolsFollowsTheEncodingUnlessItIsSet(
        Encoding encoding, bool? mapSymbols, string expected)
    {
        var rendered = (Minimal() with { Encoding = encoding, MapSymbols = mapSymbols })
            .ToFormParameters()
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        Assert.Equal(expected, rendered["map_symbols"]);
    }

    /// <summary>
    /// Flags render as <c>true</c> and <c>false</c>, which is upstream's <c>bool::to_string</c> and
    /// not <see cref="bool.ToString()"/>'s <c>True</c>.
    /// </summary>
    [Fact]
    public void SubmitJob_RendersFlagsInLowerCase()
    {
        var rendered = (Minimal() with { SplitSymbols = true }).ToFormParameters();

        var flags = rendered
            .Where(pair => pair.Key is "pretty_px" or "pretty_ts" or "map_symbols" or "split_symbols")
            .ToList();

        Assert.Equal(4, flags.Count);
        Assert.All(flags, pair => Assert.True(
            pair.Value is "true" or "false",
            $"'{pair.Key}' rendered as '{pair.Value}'. bool.ToString() gives 'True', which is what "
            + "this asserts against."));
    }

    /// <summary>
    /// Splitting by raw symbol needs raw symbols to split by, so the whole-dataset set is refused —
    /// at render time, because the rule reads two properties and an initializer can only see one.
    /// </summary>
    [Fact]
    public void SubmitJob_RefusesSplitSymbolsWithTheWholeDataset()
    {
        var job = Minimal() with { Symbols = Symbols.All, SplitSymbols = true };

        var thrown = Assert.Throws<InvalidOperationException>(() => job.ToFormParameters());
        Assert.Contains("Symbols.All", thrown.Message, StringComparison.Ordinal);

        // Either half alone is fine.
        Assert.NotEmpty((Minimal() with { Symbols = Symbols.All }).ToFormParameters());
        Assert.NotEmpty((Minimal() with { SplitSymbols = true }).ToFormParameters());
    }

    /// <summary>
    /// The <c>pretty_*</c> flags are text-encoding options, and DBN has no other spelling for a
    /// price or a timestamp.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SubmitJob_RefusesThePrettyFlagsWithDbn(bool prettyPx, bool prettyTs)
    {
        var job = Minimal() with { Encoding = Encoding.Dbn, PrettyPx = prettyPx, PrettyTs = prettyTs };

        Assert.Throws<InvalidOperationException>(() => job.ToFormParameters());

        // The same flags are fine on a text encoding, which is what makes this a combination rule
        // rather than a rule about the flags.
        var csv = job with { Encoding = Encoding.Csv };
        Assert.NotEmpty(csv.ToFormParameters());
    }

    /// <summary>
    /// The message names the flag that is wrong, because a job usually sets only one of the two and
    /// "a pretty flag" would leave the caller checking both.
    /// </summary>
    [Fact]
    public void SubmitJob_NamesTheOffendingPrettyFlag()
    {
        var px = Assert.Throws<InvalidOperationException>(
            () => (Minimal() with { PrettyPx = true }).ToFormParameters());
        Assert.Contains("PrettyPx is", px.Message, StringComparison.Ordinal);

        var ts = Assert.Throws<InvalidOperationException>(
            () => (Minimal() with { PrettyTs = true }).ToFormParameters());
        Assert.Contains("PrettyTs is", ts.Message, StringComparison.Ordinal);

        var both = Assert.Throws<InvalidOperationException>(
            () => (Minimal() with { PrettyPx = true, PrettyTs = true }).ToFormParameters());
        Assert.Contains("PrettyPx and PrettyTs are", both.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The split size is bounded at construction, because that rule reads one property. Zero is
    /// below the minimum, which is what makes the non-zero constraint C# has no type for redundant.
    /// </summary>
    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(999_999_999UL)]
    [InlineData(10_000_000_001UL)]
    [InlineData(ulong.MaxValue)]
    public void SubmitJob_RefusesASplitSizeOutsideOneToTenGigabytes(ulong size) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Minimal() with { SplitSize = size });

    /// <summary>Both ends of the range are accepted, and so is leaving it unset.</summary>
    [Fact]
    public void SubmitJob_AcceptsBothEndsOfTheSplitSizeRange()
    {
        Assert.Equal(
            SubmitJobParams.MinimumSplitSize,
            (Minimal() with { SplitSize = SubmitJobParams.MinimumSplitSize }).SplitSize);

        Assert.Equal(
            SubmitJobParams.MaximumSplitSize,
            (Minimal() with { SplitSize = SubmitJobParams.MaximumSplitSize }).SplitSize);

        Assert.Null(Minimal().SplitSize);
    }

    /// <summary>Zero is refused for the reason <c>GetRangeParams.Limit</c> gives at length.</summary>
    [Fact]
    public void SubmitJob_RefusesALimitOfZero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Minimal() with { Limit = 0 });
        Assert.Equal(1UL, (Minimal() with { Limit = 1 }).Limit);
    }

    /// <summary>
    /// <see cref="SubmitJobParams.ToQuery"/> prices the job that is about to be submitted: every
    /// billable field carries across, and the formatting and splitting options — which cannot
    /// affect a price — do not.
    /// </summary>
    [Fact]
    public void SubmitJob_ToQueryCarriesEveryBillableFieldAndNothingElse()
    {
        var job = Full();

        var priced = job.ToQuery();

        Assert.Equal(job.Dataset, priced.Dataset);
        Assert.Equal(job.Symbols, priced.Symbols);
        Assert.Equal(job.Schema, priced.Schema);
        Assert.Equal(job.DateTimeRange, priced.DateTimeRange);
        Assert.Equal(job.StypeIn, priced.StypeIn);
        Assert.Equal(job.Limit, priced.Limit);

        var keys = priced.ToFormParameters().Select(pair => pair.Key).ToList();
        foreach (var absent in new[]
                 {
                     "stype_out", "encoding", "compression", "pretty_px", "pretty_ts",
                     "map_symbols", "split_symbols", "split_size", "split_duration", "delivery",
                 })
        {
            Assert.DoesNotContain(absent, keys);
        }
    }

    /// <summary>
    /// The quote covers the same window and symbols the job asks for. A conversion that priced a
    /// different request would be worse than none, because it would look authoritative.
    /// </summary>
    [Fact]
    public void SubmitJob_ToQueryPricesTheSameWindowTheJobSubmits()
    {
        var job = Full();

        var submitted = job.ToFormParameters().ToDictionary(pair => pair.Key, pair => pair.Value);
        var priced = job.ToQuery().ToFormParameters().ToDictionary(pair => pair.Key, pair => pair.Value);

        foreach (var field in new[] { "dataset", "schema", "stype_in", "symbols", "start", "end", "limit" })
        {
            Assert.Equal(submitted[field], priced[field]);
        }
    }

    /// <summary>A default <see cref="ListJobsParams"/> filters nothing and renders nothing.</summary>
    [Fact]
    public void ListJobs_RendersNothingWhenNothingIsFiltered() =>
        Assert.Empty(new ListJobsParams().ToQueryParameters());

    /// <summary>
    /// States render as one comma-separated parameter, which is upstream's spelling and the one
    /// #39 confirmed the API reads.
    /// </summary>
    [Fact]
    public void ListJobs_RendersStatesAsOneCommaSeparatedParameter()
    {
        var rendered = new ListJobsParams
        {
            States = [JobState.Done, JobState.Queued, JobState.Finalizing],
        }.ToQueryParameters();

        Assert.Equal(new KeyValuePair<string, string>("states", "done,queued,finalizing"), Assert.Single(rendered));
    }

    /// <summary>
    /// <c>since</c> goes out as Unix nanoseconds, which is what the API filters on — measured, not
    /// assumed: #39 sent a <c>since</c> past two of four jobs' receipt times and got the other two.
    /// </summary>
    [Fact]
    public void ListJobs_RendersSinceAsUnixNanoseconds()
    {
        var rendered = new ListJobsParams
        {
            Since = Instant.FromUtc(2026, 8, 27, 0, 0, 0),
        }.ToQueryParameters();

        Assert.Equal(new KeyValuePair<string, string>("since", "1787788800000000000"), Assert.Single(rendered));
    }

    /// <summary>
    /// Nanoseconds, not ticks: an instant carrying sub-microsecond precision renders it, which is
    /// the whole reason this repo bans the BCL date types.
    /// </summary>
    [Fact]
    public void ListJobs_RendersSinceWithNanosecondPrecision()
    {
        var rendered = new ListJobsParams
        {
            Since = Instant.FromUnixTimeSeconds(1_787_788_800) + Duration.FromNanoseconds(1L),
        }.ToQueryParameters();

        Assert.Equal("1787788800000000001", rendered[0].Value);
    }

    /// <summary>Both filters together, in declaration order.</summary>
    [Fact]
    public void ListJobs_RendersBothFiltersStatesFirst()
    {
        var rendered = new ListJobsParams
        {
            States = [JobState.Expired],
            Since = Instant.FromUtc(2026, 8, 27, 0, 0, 0),
        }.ToQueryParameters();

        Assert.Equal(["states", "since"], rendered.Select(pair => pair.Key));
    }

    /// <summary>
    /// An empty state list is treated as unset. An empty <c>states=</c> is not a request for no
    /// states; it is a malformed one.
    /// </summary>
    [Fact]
    public void ListJobs_TreatsAnEmptyStateListAsUnset() =>
        Assert.Empty(new ListJobsParams { States = [] }.ToQueryParameters());

    /// <summary>An undefined state is refused rather than rendered as a number.</summary>
    [Fact]
    public void ListJobs_RefusesAnUndefinedState() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ListJobsParams { States = [(JobState)99] }.ToQueryParameters());

    /// <summary>
    /// <see cref="DownloadParams.MaximumConcurrency"/> defaults to more than one — the departure
    /// from upstream — and refuses less than one, which would transfer nothing.
    /// </summary>
    [Fact]
    public void Download_BoundsConcurrencyAtOneOrMore()
    {
        var parameters = new DownloadParams { OutputDirectory = "/tmp", JobId = BatchFixture.JobId };

        Assert.Equal(DownloadParams.DefaultMaximumConcurrency, parameters.MaximumConcurrency);
        Assert.True(parameters.MaximumConcurrency > 1, "The default is the departure from upstream's sequential loop.");

        Assert.Equal(1, (parameters with { MaximumConcurrency = 1 }).MaximumConcurrency);
        Assert.Throws<ArgumentOutOfRangeException>(() => parameters with { MaximumConcurrency = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => parameters with { MaximumConcurrency = -1 });
    }

    private static SubmitJobParams Minimal() => new()
    {
        Dataset = "XNAS.ITCH",
        Symbols = Symbols.From("TSLA"),
        Schema = Schema.Trades,
        DateTimeRange = Range,
    };

    private static SubmitJobParams Full() => new()
    {
        Dataset = "XNAS.ITCH",
        Symbols = Symbols.From(["TSLA", "MSFT"]),
        Schema = Schema.Trades,
        DateTimeRange = Range,
        Encoding = Encoding.Csv,
        Compression = Compression.Zstd,
        PrettyPx = true,
        PrettyTs = true,
        MapSymbols = false,
        SplitSymbols = true,
        SplitDuration = SplitDuration.Week,
        SplitSize = 2_000_000_000,
        Delivery = Delivery.Download,
        StypeIn = SType.RawSymbol,
        StypeOut = SType.InstrumentId,
        Limit = 7,
    };
}
