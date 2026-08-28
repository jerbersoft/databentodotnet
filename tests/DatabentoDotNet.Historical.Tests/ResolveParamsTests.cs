using DatabentoDotNet.Dbn;
using NodaTime;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// Tests for <see cref="ResolveParams"/> — the form it renders, and the two conversions that build
/// one from something a caller already has.
/// </summary>
/// <remarks>
/// The rendering is asserted here in isolation and again over the wire in
/// <see cref="SymbologyResolveTests"/>; the pair is what catches an encoding applied twice or not
/// at all, the same split <c>MetadataParamsTests</c> and <c>MetadataClientPostTests</c> use.
/// </remarks>
public sealed class ResolveParamsTests
{
    private static readonly DateRange Range = DateRange.Between(new LocalDate(2023, 6, 14), new LocalDate(2023, 6, 17));

    /// <summary>
    /// Upstream's push order (<c>symbology.rs:30-36</c>), which puts both symbology types ahead of
    /// the symbols they describe and appends the date pair last.
    /// </summary>
    [Fact]
    public void ToFormParameters_RendersUpstreamsFieldOrder()
    {
        var parameters = new ResolveParams
        {
            Dataset = "GLBX.MDP3",
            Symbols = Symbols.From(["ES.c.0", "ES.d.0"]),
            StypeIn = SType.Continuous,
            DateRange = Range,
        }.ToFormParameters();

        Assert.Equal(
            [
                new KeyValuePair<string, string>("dataset", "GLBX.MDP3"),
                new KeyValuePair<string, string>("stype_in", "continuous"),
                new KeyValuePair<string, string>("stype_out", "instrument_id"),
                new KeyValuePair<string, string>("symbols", "ES.c.0,ES.d.0"),
                new KeyValuePair<string, string>("start_date", "2023-06-14"),
                new KeyValuePair<string, string>("end_date", "2023-06-17"),
            ],
            parameters);
    }

    /// <summary>
    /// <c>end_date</c> goes on the wire verbatim, because this endpoint reads it as exclusive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The regression this pins is picking the wrong renderer, which nothing else would
    /// catch.</b> <see cref="DateRange.ToInclusiveEndDateParameters"/> sits beside the one used
    /// here and differs only in sending the day before; swapping them produces a perfectly valid
    /// request that silently resolves one day less. #45 is the whole argument for why both exist.
    /// </para>
    /// <para>
    /// The value is a probed fact, not an inference from the type — see
    /// <see cref="ResolveParams.DateRange"/> for the three requests that settled it, one of which
    /// was the server refusing <c>start_date == end_date</c> outright.
    /// </para>
    /// </remarks>
    [Fact]
    public void ToFormParameters_ForASingleDay_SendsTheDayAfterAsTheExclusiveEnd()
    {
        var parameters = new ResolveParams
        {
            Dataset = "GLBX.MDP3",
            Symbols = Symbols.From("ESH4"),
            DateRange = DateRange.OnDay(new LocalDate(2024, 1, 2)),
        }.ToFormParameters();

        var rendered = parameters.ToDictionary(StringComparer.Ordinal);
        Assert.Equal("2024-01-02", rendered["start_date"]);
        Assert.Equal("2024-01-03", rendered["end_date"]);
    }

    /// <summary>
    /// <see langword="required"/> stops a caller forgetting a property, not assigning
    /// <see langword="default"/> to one. Both renderers refuse one, as their accessors document.
    /// </summary>
    [Fact]
    public void ToFormParameters_WithDefaultValuedMembers_Throws()
    {
        var noSymbols = new ResolveParams { Dataset = "GLBX.MDP3", Symbols = default, DateRange = Range };
        Assert.Throws<InvalidOperationException>(noSymbols.ToFormParameters);

        var noRange = new ResolveParams { Dataset = "GLBX.MDP3", Symbols = Symbols.All, DateRange = default };
        Assert.Throws<InvalidOperationException>(noRange.ToFormParameters);
    }

    /// <summary>
    /// The <c>ALL_SYMBOLS</c> workflow: metadata from a decoded historical stream becomes the
    /// request that names what the stream contained.
    /// </summary>
    [Fact]
    public void FromMetadata_TakesTheDatasetSymbolsStypesAndRange()
    {
        var parameters = ResolveParams.FromMetadata(MetadataFor(
            symbols: ["ESH4", "ESM4"],
            start: new LocalDate(2024, 1, 2),
            end: new LocalDate(2024, 1, 5)));

        Assert.Equal("GLBX.MDP3", parameters.Dataset);
        Assert.Equal(Symbols.From(["ESH4", "ESM4"]), parameters.Symbols);
        Assert.Equal(SType.RawSymbol, parameters.StypeIn);
        Assert.Equal(SType.InstrumentId, parameters.StypeOut);
        Assert.Equal(DateRange.Between(new LocalDate(2024, 1, 2), new LocalDate(2024, 1, 5)), parameters.DateRange);
    }

    /// <summary>
    /// A stream whose end falls mid-day still covers part of that day, so the range rounds
    /// <em>up</em> — <see cref="DateTimeRange.ToDateRange"/>'s documented direction, asserted here
    /// because this conversion is where a caller meets it.
    /// </summary>
    /// <remarks>
    /// Rounding down would silently drop the last day's symbols from a resolution built to name a
    /// stream that contains them, which is precisely the mapping a caller would then fail to find.
    /// </remarks>
    [Fact]
    public void FromMetadata_WithAPartialEndDay_RoundsTheRangeUp()
    {
        var midDay = MetadataFor(
            symbols: ["ESH4"],
            start: new LocalDate(2024, 1, 2),
            end: new LocalDate(2024, 1, 4),
            endOverride: DbnTime.ToUnixNanoseconds(Instant.FromUtc(2024, 1, 4, 13, 30)));

        Assert.Equal(
            DateRange.Between(new LocalDate(2024, 1, 2), new LocalDate(2024, 1, 5)),
            ResolveParams.FromMetadata(midDay).DateRange);
    }

    /// <summary>
    /// The three absences that make a resolution request unformulable, each reported rather than
    /// filled in with something plausible.
    /// </summary>
    /// <remarks>
    /// The first two are upstream's (<c>symbology.rs:89-97</c>) and are the normal state of a live
    /// stream: a mixed-symbology capture has no <c>stype_in</c>, an open-ended session no
    /// <c>end</c>. The third is this port's, because
    /// <see cref="DatabentoDotNet.Symbols.From(System.Collections.Generic.IEnumerable{string})"/>
    /// refuses to build an empty set where upstream would send <c>symbols=</c>.
    /// </remarks>
    [Theory]
    [InlineData("stype_in")]
    [InlineData("end")]
    [InlineData("symbols")]
    public void FromMetadata_WithoutARequiredField_ThrowsNamingIt(string missing)
    {
        var start = new LocalDate(2024, 1, 2);
        var end = new LocalDate(2024, 1, 5);
        var metadata = missing switch
        {
            "stype_in" => MetadataFor(["ESH4"], start, end, stypeIn: null),
            "end" => MetadataFor(["ESH4"], start, end, endAbsent: true),
            _ => MetadataFor([], start, end),
        };

        Assert.False(ResolveParams.TryFromMetadata(metadata, out var parameters));
        Assert.Null(parameters);

        var thrown = Assert.Throws<ArgumentException>(() => ResolveParams.FromMetadata(metadata));
        Assert.Contains(missing, thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>Complete metadata converts through the <c>Try</c> form as well.</summary>
    [Fact]
    public void TryFromMetadata_WithCompleteMetadata_Succeeds()
    {
        var metadata = MetadataFor(["ESH4"], new LocalDate(2024, 1, 2), new LocalDate(2024, 1, 5));

        Assert.True(ResolveParams.TryFromMetadata(metadata, out var parameters));
        Assert.Equal(ResolveParams.FromMetadata(metadata), parameters);
    }

    /// <summary>
    /// A priced query becomes a resolution request over the same dataset, symbols, input symbology
    /// and days.
    /// </summary>
    [Fact]
    public void FromQuery_CarriesTheQuerysOwnFieldsAndNarrowsItsRange()
    {
        var query = new MetadataQueryParams
        {
            Dataset = "GLBX.MDP3",
            Symbols = Symbols.From(["ESH4"]),
            Schema = Schema.Trades,
            StypeIn = SType.Continuous,
            DateTimeRange = DateTimeRange.Between(
                Instant.FromUtc(2024, 1, 2, 0, 0), Instant.FromUtc(2024, 1, 5, 0, 0)),
        };

        var parameters = ResolveParams.FromQuery(query, SType.RawSymbol);

        Assert.Equal("GLBX.MDP3", parameters.Dataset);
        Assert.Equal(query.Symbols, parameters.Symbols);
        Assert.Equal(SType.Continuous, parameters.StypeIn);
        Assert.Equal(DateRange.Between(new LocalDate(2024, 1, 2), new LocalDate(2024, 1, 5)), parameters.DateRange);
    }

    /// <summary>
    /// <c>stype_out</c> comes from the argument, and is not quietly defaulted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the test for a bug that cannot otherwise be seen.</b> A resolution requested with
    /// the wrong <c>stype_out</c> fails nowhere: every symbol resolves, nothing lands in
    /// <see cref="Resolution.NotFound"/> or <see cref="Resolution.Partial"/>, and the names are
    /// simply the wrong kind. Since <see cref="MetadataQueryParams"/> has no <c>stype_out</c> field
    /// to read — see its remarks, and #38 — the only defence is that the argument is required, and
    /// the only way to notice it regressing to a default is to pass a non-default here.
    /// </para>
    /// <para>
    /// <see cref="SType.RawSymbol"/> is deliberately the value asserted: it is the one a caller
    /// requesting named output would ask for, and it is not
    /// <see cref="ResolveParams.StypeOut"/>'s own default.
    /// </para>
    /// </remarks>
    [Fact]
    public void FromQuery_TakesStypeOutFromTheArgument_NotFromADefault()
    {
        var query = new MetadataQueryParams
        {
            Dataset = "GLBX.MDP3",
            Symbols = Symbols.All,
            Schema = Schema.Trades,
            DateTimeRange = DateTimeRange.OnDay(new LocalDate(2024, 1, 2)),
        };

        Assert.Equal(SType.RawSymbol, ResolveParams.FromQuery(query, SType.RawSymbol).StypeOut);
        Assert.Equal(SType.InstrumentId, ResolveParams.FromQuery(query, SType.InstrumentId).StypeOut);
    }

    /// <summary>
    /// A metadata block whose <see cref="Metadata.Start"/> is the undefined-timestamp sentinel
    /// throws from both forms, rather than being reported as an absence.
    /// </summary>
    /// <remarks>
    /// <b>The asymmetry with <see cref="Metadata.End"/> is the point.</b> The decoder turns the
    /// same sentinel into <see langword="null"/> for <c>end</c>, where it means "open-ended" — an
    /// ordinary state these conversions report as <see langword="false"/>. There is no
    /// corresponding meaning for <c>start</c>: a stream with no start is a broken stream, so it
    /// throws, and <see cref="ResolveParams.TryFromMetadata"/> throws with it rather than
    /// flattening a corrupt file into "this metadata does not support resolution".
    /// </remarks>
    [Fact]
    public void FromMetadata_WithAnUndefinedStart_Throws()
    {
        var metadata = MetadataFor(
            ["ESH4"], new LocalDate(2024, 1, 2), new LocalDate(2024, 1, 5),
            startOverride: DbnConstants.UndefTimestamp);

        Assert.Throws<ArgumentOutOfRangeException>(() => ResolveParams.FromMetadata(metadata));
        Assert.Throws<ArgumentOutOfRangeException>(() => ResolveParams.TryFromMetadata(metadata, out _));
    }

    /// <summary>Both conversions guard their argument.</summary>
    [Fact]
    public void TheConversions_RejectNull()
    {
        Assert.Throws<ArgumentNullException>(() => ResolveParams.FromMetadata(null!));
        Assert.Throws<ArgumentNullException>(() => ResolveParams.TryFromMetadata(null!, out _));
        Assert.Throws<ArgumentNullException>(() => ResolveParams.FromQuery(null!, SType.InstrumentId));
    }

    /// <summary>
    /// Metadata as a decoded historical stream would carry it, with only the fields these
    /// conversions read set to anything meaningful.
    /// </summary>
    /// <remarks>
    /// <see cref="Metadata"/> is a plain class rather than a record — the decoder builds one per
    /// stream, not one per record, so it has no <c>with</c> to vary a single field from. Hence the
    /// override parameters rather than a copy expression at each call site.
    /// </remarks>
    private static Metadata MetadataFor(
        IReadOnlyList<string> symbols,
        LocalDate start,
        LocalDate end,
        SType? stypeIn = SType.RawSymbol,
        bool endAbsent = false,
        ulong? endOverride = null,
        ulong? startOverride = null) => new()
    {
        Version = 3,
        Dataset = "GLBX.MDP3",
        Schema = Schema.Trades,
        Start = startOverride ?? DbnTime.ToUnixNanosecondsAtMidnightUtc(start),
        End = endAbsent ? null : endOverride ?? DbnTime.ToUnixNanosecondsAtMidnightUtc(end),
        StypeIn = stypeIn,
        StypeOut = SType.InstrumentId,
        SymbolCstrLength = DbnConstants.SymbolCstrLength,
        Symbols = symbols,
    };
}
