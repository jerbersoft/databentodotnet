using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using NodaTime;

namespace DatabentoDotNet.Dbn.Tests;

/// <summary>
/// Conformance tests for <see cref="TsSymbolMap"/> and <see cref="PitSymbolMap"/>.
/// </summary>
/// <remarks>
/// <para>
/// The hand-built <c>MetadataWith*</c> fixtures below are a direct transcription of upstream's
/// own <c>metadata_w_mappings()</c> test fixture (<c>symbol_map.rs:391-823</c>), so the specific
/// date/instrument-ID assertions here can be compared line for line against upstream's
/// <c>test_symbol_map</c> and <c>test_symbol_map_for_date</c>.
/// </para>
/// <para>
/// The <c>DefinitionFixture</c> tests are the other half of the conformance story: real wire
/// bytes (<c>test_data.definition.*</c>) rather than a hand-built fixture, checking that both map
/// types resolve every instrument-definition record in the file to the symbol its own file's
/// metadata mapping says it should have.
/// </para>
/// <para>
/// Neither map type exposes a raw-dictionary escape hatch (upstream's <c>inner()</c>) or
/// structural equality in this port — deliberately, per G9 scope discipline; see the task report.
/// Where an upstream test used one to compare two whole maps for equality, the port below
/// substitutes the same explicit spot checks run against both maps plus a <see cref="TsSymbolMap.Count"/>
/// / <see cref="PitSymbolMap.Count"/> comparison.
/// </para>
/// </remarks>
public class SymbolMapTests
{
    // ------------------------------------------------------------------------------------
    // TsSymbolMap.Insert
    // ------------------------------------------------------------------------------------

    [Fact]
    public void Insert_StartDateBeforeEndDate_ExpandsEveryCalendarDayExceptTheEnd()
    {
        var map = new TsSymbolMap();

        map.Insert(1, D(7, 1), D(7, 4), "AAPL");

        Assert.Equal(3, map.Count);
        Assert.True(map.TryGetSymbol(D(7, 1), 1, out var d1));
        Assert.Equal("AAPL", d1);
        Assert.True(map.TryGetSymbol(D(7, 2), 1, out var d2));
        Assert.Equal("AAPL", d2);
        Assert.True(map.TryGetSymbol(D(7, 3), 1, out var d3));
        Assert.Equal("AAPL", d3);

        // The end date itself is exclusive.
        Assert.False(map.TryGetSymbol(D(7, 4), 1, out _));
        Assert.False(map.TryGetSymbol(D(6, 30), 1, out _));
    }

    [Fact]
    public void Insert_StartDateEqualsEndDate_IsANoOp()
    {
        // Port of upstream's test_insert_start_end_date_same (symbol_map.rs:1058-1071): a
        // degenerate/empty interval is a silent no-op, not an error and not a single-day insert.
        var map = new TsSymbolMap();

        map.Insert(1, D(12, 3), D(12, 3), "test");

        Assert.True(map.IsEmpty);
    }

    [Fact]
    public void Insert_StartDateAfterEndDate_ThrowsDbnDecodeException()
    {
        var map = new TsSymbolMap();

        Assert.Throws<DbnDecodeException>(() => map.Insert(1, D(7, 4), D(7, 1), "AAPL"));
    }

    [Fact]
    public void Insert_SameSymbolAcrossManyDays_SharesOneStringInstance()
    {
        // The upstream design point this exists to protect: Insert must reuse the caller's string
        // reference across every day, not allocate a fresh copy per day (which is what Arc<String>
        // buys upstream and what a plain, reused `string` reference buys here for free).
        var map = new TsSymbolMap();
        var symbol = "AAPL";

        map.Insert(1, D(7, 1), D(7, 5), symbol);

        map.TryGetSymbol(D(7, 1), 1, out var day1);
        map.TryGetSymbol(D(7, 4), 1, out var day4);
        Assert.True(ReferenceEquals(symbol, day1));
        Assert.True(ReferenceEquals(symbol, day4));
    }

    // ------------------------------------------------------------------------------------
    // TsSymbolMap.FromMetadata / PitSymbolMap.FromMetadata: forward vs. inverse direction
    // ------------------------------------------------------------------------------------

    [Fact]
    public void TsSymbolMapFromMetadata_ForwardMapping_MatchesUpstreamsSpotChecks()
    {
        // Port of upstream's test_symbol_map (symbol_map.rs:892-910).
        var map = TsSymbolMap.FromMetadata(MetadataWithMappings());

        AssertForwardTsSpotChecks(map);
    }

    [Fact]
    public void TsSymbolMapFromMetadata_InverseMapping_MatchesTheSameSpotChecksAsForward()
    {
        var forward = TsSymbolMap.FromMetadata(MetadataWithMappings());
        var inverse = TsSymbolMap.FromMetadata(MetadataWithInverseMappings());

        Assert.Equal(forward.Count, inverse.Count);
        AssertForwardTsSpotChecks(inverse);
    }

    private static void AssertForwardTsSpotChecks(TsSymbolMap map)
    {
        Assert.True(map.TryGetSymbol(D(7, 2), 32, out var s1));
        Assert.Equal("AAPL", s1);
        Assert.True(map.TryGetSymbol(D(7, 30), 32, out var s2));
        Assert.Equal("AAPL", s2);
        Assert.True(map.TryGetSymbol(D(7, 31), 32, out var s3));
        Assert.Equal("AAPL", s3);
        Assert.False(map.TryGetSymbol(D(8, 1), 32, out _));

        Assert.True(map.TryGetSymbol(D(7, 8), 8029, out var s4));
        Assert.Equal("PLTR", s4);
        Assert.False(map.TryGetSymbol(D(7, 10), 8029, out _));
        Assert.True(map.TryGetSymbol(D(7, 10), 8022, out var s5));
        Assert.Equal("PLTR", s5);

        Assert.True(map.TryGetSymbol(D(7, 20), 10184, out var s6));
        Assert.Equal("TSLA", s6);
        Assert.True(map.TryGetSymbol(D(7, 21), 10181, out var s7));
        Assert.Equal("TSLA", s7);
        Assert.True(map.TryGetSymbol(D(7, 24), 10174, out var s8));
        Assert.Equal("TSLA", s8);
        Assert.True(map.TryGetSymbol(D(7, 25), 10172, out var s9));
        Assert.Equal("TSLA", s9);
    }

    [Fact]
    public void PitSymbolMapFromMetadata_ForwardMapping_MatchesUpstreamsSpotChecks()
    {
        // Port of upstream's test_symbol_map_for_date (symbol_map.rs:850-868).
        var map = PitSymbolMap.FromMetadata(MetadataWithMappings(), D(7, 31));

        AssertForwardPitSpotChecks(map);
    }

    [Fact]
    public void PitSymbolMapFromMetadata_InverseMapping_MatchesTheSameSpotChecksAsForward()
    {
        var forward = PitSymbolMap.FromMetadata(MetadataWithMappings(), D(7, 31));
        var inverse = PitSymbolMap.FromMetadata(MetadataWithInverseMappings(), D(7, 31));

        Assert.Equal(forward.Count, inverse.Count);
        AssertForwardPitSpotChecks(inverse);
    }

    private static void AssertForwardPitSpotChecks(PitSymbolMap map)
    {
        Assert.Equal(4, map.Count);
        Assert.True(map.TryGetSymbol(32, out var s1));
        Assert.Equal("AAPL", s1);
        Assert.True(map.TryGetSymbol(7295, out var s2));
        Assert.Equal("NVDA", s2);

        // 7298 was NVDA's instrument ID the previous day (07-28..07-31); it must not carry over.
        Assert.False(map.TryGetSymbol(7298, out _));

        Assert.True(map.TryGetSymbol(10163, out var s3));
        Assert.Equal("TSLA", s3);
        Assert.True(map.TryGetSymbol(6803, out var s4));
        Assert.Equal("MSFT", s4);
    }

    [Fact]
    public void FromMetadata_SkipsEmptySymbolInterval_OldSymbologyFormat()
    {
        // PLTR's last interval (07-31..08-01) has an empty MappingInterval.Symbol, upstream's
        // marker for "the old symbology format had no resolved symbol here". If it were not
        // skipped, parsing "" as a uint instrument ID would throw for this (non-inverse) metadata,
        // where interval.Symbol is the ID string being parsed — so both calls below succeeding at
        // all is already part of what this test checks.
        var metadata = MetadataWithMappings();

        var ts = TsSymbolMap.FromMetadata(metadata);
        var pit = PitSymbolMap.FromMetadata(metadata, D(7, 31));

        // No new instrument ID is introduced for PLTR on 07-31: the last real PLTR interval
        // (07-28..07-31, id 7994) is exclusive of 07-31 itself, and the empty-symbol interval that
        // would otherwise cover 07-31 contributes nothing to either map.
        Assert.False(ts.TryGetSymbol(D(7, 31), 7994, out _));
        Assert.False(pit.TryGetSymbol(7994, out _));
    }

    // ------------------------------------------------------------------------------------
    // stype_in/stype_out validation
    // ------------------------------------------------------------------------------------

    [Fact]
    public void TsSymbolMapFromMetadata_NeitherStypeIsInstrumentId_ThrowsDbnDecodeException()
    {
        var metadata = WithStypes(MetadataWithMappings(), SType.RawSymbol, SType.RawSymbol);

        Assert.Throws<DbnDecodeException>(() => TsSymbolMap.FromMetadata(metadata));
    }

    [Fact]
    public void PitSymbolMapFromMetadata_NeitherStypeIsInstrumentId_ThrowsDbnDecodeException()
    {
        var metadata = WithStypes(MetadataWithMappings(), SType.RawSymbol, SType.RawSymbol);

        Assert.Throws<DbnDecodeException>(() => PitSymbolMap.FromMetadata(metadata, D(7, 31)));
    }

    // ------------------------------------------------------------------------------------
    // PitSymbolMap.FromMetadata: the date-boundary subtlety (the load-bearing tests)
    // ------------------------------------------------------------------------------------

    [Fact]
    public void PitSymbolMapFromMetadata_DateBeforeStart_ThrowsDbnDecodeException()
    {
        // Port of upstream's test_symbol_map_for_date_out_of_range (symbol_map.rs:877-880).
        Assert.Throws<DbnDecodeException>(() => PitSymbolMap.FromMetadata(MetadataWithMappings(), D(6, 30)));
    }

    [Fact]
    public void PitSymbolMapFromMetadata_DateAtOrAfterDefaultEnd_ThrowsDbnDecodeException()
    {
        // The fixture's default end is exactly 2023-08-01 00:00 UTC (symbol_map.rs:873-876).
        Assert.Throws<DbnDecodeException>(() => PitSymbolMap.FromMetadata(MetadataWithMappings(), D(8, 1)));
    }

    [Fact]
    public void PitSymbolMapFromMetadata_EndLaterTheSameDayAsTheQueriedDate_Succeeds()
    {
        // end = 2023-07-01 08:00 UTC; querying 07-01 is fine because 07-01 00:00 < 08:00.
        var metadata = WithEnd(MetadataWithMappings(), UnixNanos(2023, 7, 1, 8, 0, 0));

        var map = PitSymbolMap.FromMetadata(metadata, D(7, 1));

        Assert.False(map.IsEmpty);
    }

    [Fact]
    public void PitSymbolMapFromMetadata_EndEarlierTheNextDay_ThrowsDbnDecodeException()
    {
        // Same end as above (07-01 08:00 UTC); querying 07-02 fails because 07-02 00:00 > 08:00.
        var metadata = WithEnd(MetadataWithMappings(), UnixNanos(2023, 7, 1, 8, 0, 0));

        Assert.Throws<DbnDecodeException>(() => PitSymbolMap.FromMetadata(metadata, D(7, 2)));
    }

    [Fact]
    public void PitSymbolMapFromMetadata_EndAtExactMidnightOfTheQueriedDate_ExcludesThatWholeDay()
    {
        // THE subtle case: end == the queried date at exactly 00:00 UTC. The comparison is
        // `datetime >= end`, so an end of exactly midnight excludes the day it falls on entirely —
        // pinned upstream by test_symbol_map_for_date_out_of_range (symbol_map.rs:884-885).
        var metadata = WithEnd(MetadataWithMappings(), UnixNanos(2023, 7, 2, 0, 0, 0));

        Assert.Throws<DbnDecodeException>(() => PitSymbolMap.FromMetadata(metadata, D(7, 2)));
    }

    [Fact]
    public void PitSymbolMapFromMetadata_EndOneNanosecondPastMidnightOfTheQueriedDate_IncludesThatWholeDay()
    {
        // The other half of the subtle case: nudging end just one nanosecond past midnight flips
        // the whole day from excluded to included. A port that truncates `end` to a Date before
        // comparing collapses this case into the previous one and gets it backwards — pinned
        // upstream by test_symbol_map_for_date_out_of_range (symbol_map.rs:886-889).
        var metadata = WithEnd(MetadataWithMappings(), UnixNanos(2023, 7, 2, 0, 0, 0) + 1);

        var map = PitSymbolMap.FromMetadata(metadata, D(7, 2));

        Assert.False(map.IsEmpty);
    }

    [Fact]
    public void PitSymbolMapFromMetadata_NullEnd_NeverThrowsForBeingTooLate()
    {
        // metadata.End is null for an open-ended query; only the lower bound can reject a date.
        var metadata = WithEnd(MetadataWithMappings(), null);

        var map = PitSymbolMap.FromMetadata(metadata, D(12, 31));

        Assert.True(map.IsEmpty); // No mapping interval covers 12-31, but no exception either.
    }

    // ------------------------------------------------------------------------------------
    // PitSymbolMap.OnRecord / OnSymbolMapping — the path M2's live client depends on
    // ------------------------------------------------------------------------------------

    [Fact]
    public void OnSymbolMapping_CurrentVersion_MapsInstrumentIdToTheOutputSymbol()
    {
        var map = new PitSymbolMap();

        map.OnSymbolMapping(CreateSymbolMappingMsg(1, "AAPL"));
        map.OnSymbolMapping(CreateSymbolMappingMsg(2, "TSLA"));
        map.OnSymbolMapping(CreateSymbolMappingMsg(3, "MSFT"));

        Assert.Equal(3, map.Count);
        Assert.True(map.TryGetSymbol(1, out var s1));
        Assert.Equal("AAPL", s1);
        Assert.True(map.TryGetSymbol(2, out var s2));
        Assert.Equal("TSLA", s2);
        Assert.True(map.TryGetSymbol(3, out var s3));
        Assert.Equal("MSFT", s3);
    }

    [Fact]
    public void OnSymbolMapping_CurrentVersion_OverwritesAnExistingInstrumentId()
    {
        // Port of upstream's test_on_symbol_mapping (symbol_map.rs:989-1054).
        var map = new PitSymbolMap();
        map.OnSymbolMapping(CreateSymbolMappingMsg(1, "AAPL"));
        map.OnSymbolMapping(CreateSymbolMappingMsg(2, "TSLA"));
        map.OnSymbolMapping(CreateSymbolMappingMsg(3, "MSFT"));

        map.OnSymbolMapping(CreateSymbolMappingMsg(10, "AAPL"));
        Assert.True(map.TryGetSymbol(10, out var s10));
        Assert.Equal("AAPL", s10);

        map.OnSymbolMapping(CreateSymbolMappingMsg(9, "MSFT"));
        Assert.True(map.TryGetSymbol(9, out var s9));
        Assert.Equal("MSFT", s9);

        // A genuine overwrite: instrument 1 gets remapped from AAPL to a new symbol.
        map.OnSymbolMapping(CreateSymbolMappingMsg(1, "NVDA"));
        Assert.True(map.TryGetSymbol(1, out var s1));
        Assert.Equal("NVDA", s1);
    }

    [Fact]
    public void OnSymbolMapping_V1_MapsInstrumentIdToTheOutputSymbol()
    {
        var map = new PitSymbolMap();

        map.OnSymbolMapping(CreateSymbolMappingMsgV1(1, "AAPL"));
        map.OnSymbolMapping(CreateSymbolMappingMsgV1(2, "TSLA"));

        Assert.Equal(2, map.Count);
        Assert.True(map.TryGetSymbol(1, out var s1));
        Assert.Equal("AAPL", s1);
        Assert.True(map.TryGetSymbol(2, out var s2));
        Assert.Equal("TSLA", s2);
    }

    [Fact]
    public void OnRecord_CurrentVersionSymbolMappingMsg_UpdatesTheMap()
    {
        var map = new PitSymbolMap();
        var bytes = BuildSymbolMappingMsg(7, "AAPL");

        map.OnRecord(new RecordRef(bytes.Span));

        Assert.True(map.TryGetSymbol(7, out var symbol));
        Assert.Equal("AAPL", symbol);
    }

    [Fact]
    public void OnRecord_V1SymbolMappingMsg_UpdatesTheMap()
    {
        var map = new PitSymbolMap();
        var bytes = BuildSymbolMappingMsgV1(7, "AAPL");

        map.OnRecord(new RecordRef(bytes.Span));

        Assert.True(map.TryGetSymbol(7, out var symbol));
        Assert.Equal("AAPL", symbol);
    }

    [Fact]
    public void OnRecord_InstrumentDefRecord_IsANoOp()
    {
        // Upstream's on_record dispatches only to SymbolMappingMsg / v1::SymbolMappingMsg; a
        // definition record must not update the map through this path — OnInstrumentDef is a
        // separate, explicitly-called path (see its own remarks).
        var map = new PitSymbolMap();
        var bytes = TestFixtures.ReadDecompressed(Fixture("test_data.definition.dbn"));
        using var decoder = new DbnDecoder(new MemoryStream(bytes), VersionUpgradePolicy.AsIs);
        Assert.True(decoder.TryNextRecord(out var record));
        Assert.True(record.Has<InstrumentDefMsgV2>());

        map.OnRecord(record);

        Assert.True(map.IsEmpty);
    }

    [Fact]
    public void TryGetSymbol_UnknownInstrumentId_ReturnsFalse()
    {
        var map = new PitSymbolMap();

        Assert.False(map.TryGetSymbol(12345, out var symbol));
        Assert.Null(symbol);
    }

    // ------------------------------------------------------------------------------------
    // PitSymbolMap.OnInstrumentDef — the alternate incremental-update path
    // ------------------------------------------------------------------------------------

    [Fact]
    public void OnInstrumentDef_CurrentVersion_MapsInstrumentIdToTheRawSymbol()
    {
        var map = new PitSymbolMap();
        var bytes = TestFixtures.ReadDecompressed(Fixture("test_data.definition.v3.dbn.zst"));
        using var decoder = new DbnDecoder(new MemoryStream(bytes), VersionUpgradePolicy.AsIs);
        Assert.True(decoder.TryNextRecord(out var record));
        ref readonly var def = ref record.Get<InstrumentDefMsg>();

        map.OnInstrumentDef(in def);

        Assert.True(map.TryGetSymbol(def.Header.InstrumentId, out var symbol));
        Assert.Equal("MSFT", symbol);
    }

    [Fact]
    public void OnInstrumentDef_V1_MapsInstrumentIdToTheRawSymbol()
    {
        var map = new PitSymbolMap();
        var bytes = TestFixtures.ReadDecompressed(Fixture("test_data.definition.v1.dbn.zst"));
        using var decoder = new DbnDecoder(new MemoryStream(bytes), VersionUpgradePolicy.AsIs);
        Assert.True(decoder.TryNextRecord(out var record));
        ref readonly var def = ref record.Get<InstrumentDefMsgV1>();

        map.OnInstrumentDef(in def);

        Assert.True(map.TryGetSymbol(def.Header.InstrumentId, out var symbol));
        Assert.Equal("MSFT", symbol);
    }

    [Fact]
    public void OnInstrumentDef_V2_MapsInstrumentIdToTheRawSymbol()
    {
        var map = new PitSymbolMap();
        var bytes = TestFixtures.ReadDecompressed(Fixture("test_data.definition.v2.dbn.zst"));
        using var decoder = new DbnDecoder(new MemoryStream(bytes), VersionUpgradePolicy.AsIs);
        Assert.True(decoder.TryNextRecord(out var record));
        ref readonly var def = ref record.Get<InstrumentDefMsgV2>();

        map.OnInstrumentDef(in def);

        Assert.True(map.TryGetSymbol(def.Header.InstrumentId, out var symbol));
        Assert.Equal("MSFT", symbol);
    }

    // ------------------------------------------------------------------------------------
    // Definition of done: resolving every instrument in the vendored definition fixture
    // ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("test_data.definition.dbn")]
    [InlineData("test_data.definition.v1.dbn.zst")]
    [InlineData("test_data.definition.v2.dbn.zst")]
    [InlineData("test_data.definition.v3.dbn.zst")]
    public void FromMetadata_DefinitionFixture_ResolvesEveryInstrumentToTheFilesOwnMapping(string fixtureName)
    {
        var bytes = TestFixtures.ReadDecompressed(Fixture(fixtureName));
        using var decoder = new DbnDecoder(new MemoryStream(bytes)); // default policy: every record upgrades to InstrumentDefMsg.
        var metadata = decoder.Metadata!;

        var tsMap = TsSymbolMap.FromMetadata(metadata);

        var instrumentCount = 0;
        while (decoder.TryNextRecord(out var record))
        {
            Assert.True(record.Has<InstrumentDefMsg>());
            ref readonly var def = ref record.Get<InstrumentDefMsg>();
            instrumentCount++;

            var date = IndexDate(def.Header.TsEvent);
            var expectedSymbol = def.RawSymbol.ToString();

            Assert.True(
                tsMap.TryGetSymbol(date, def.Header.InstrumentId, out var tsSymbol),
                $"TsSymbolMap has no mapping for instrument {def.Header.InstrumentId} on {date:yyyy-MM-dd}.");
            Assert.Equal(expectedSymbol, tsSymbol);

            var pitMap = PitSymbolMap.FromMetadata(metadata, date);
            Assert.True(
                pitMap.TryGetSymbol(def.Header.InstrumentId, out var pitSymbol),
                $"PitSymbolMap for {date:yyyy-MM-dd} has no mapping for instrument {def.Header.InstrumentId}.");
            Assert.Equal(expectedSymbol, pitSymbol);
        }

        // The fixture census, restated as an assertion: if a re-vendor ever changes the file, this
        // stops silently exercising two instruments and starts silently exercising zero.
        Assert.Equal(2, instrumentCount);
    }

    // ------------------------------------------------------------------------------------
    // Fixture builders — a direct transcription of upstream's metadata_w_mappings()
    // (symbol_map.rs:391-823) and metadata_w_inverse_mappings() (symbol_map.rs:825-847).
    // ------------------------------------------------------------------------------------

    private static LocalDate D(int month, int day) => new(2023, month, day);

    // Both helpers do their arithmetic in NodaTime directly rather than calling DbnTime, so the
    // expected values they produce stay independent of the conversion under test. The unchecked
    // long cast is safe for the 2023-era timestamps used here and nowhere near the sentinel;
    // DbnTime is the one that has to survive the whole ulong range.
    private static ulong UnixNanos(int year, int month, int day, int hour, int minute, int second)
        => (ulong)(Instant.FromUtc(year, month, day, hour, minute, second) - NodaConstants.UnixEpoch).ToInt64Nanoseconds();

    private static LocalDate IndexDate(ulong tsEvent)
        => (NodaConstants.UnixEpoch + Duration.FromNanoseconds((long)tsEvent)).InUtc().Date;

    private static DbnFixture Fixture(string name) => TestFixtures.All.Single(fixture => fixture.Name == name);

    private static Metadata MetadataWithMappings() => new()
    {
        Version = 2,
        Dataset = "XNAS.ITCH",
        Schema = Schema.Trades,
        Start = UnixNanos(2023, 7, 1, 0, 0, 0),
        End = UnixNanos(2023, 8, 1, 0, 0, 0),
        StypeIn = SType.RawSymbol,
        StypeOut = SType.InstrumentId,
        SymbolCstrLength = DbnConstants.SymbolCstrLength,
        Mappings =
        [
            new SymbolMapping
            {
                RawSymbol = "AAPL",
                Intervals = [new MappingInterval(D(7, 1), D(8, 1), "32")],
            },
            new SymbolMapping
            {
                RawSymbol = "TSLA",
                Intervals =
                [
                    new MappingInterval(D(7, 1), D(7, 3), "10221"),
                    new MappingInterval(D(7, 3), D(7, 5), "10213"),
                    new MappingInterval(D(7, 5), D(7, 6), "10209"),
                    new MappingInterval(D(7, 6), D(7, 7), "10206"),
                    new MappingInterval(D(7, 7), D(7, 10), "10201"),
                    new MappingInterval(D(7, 10), D(7, 11), "10193"),
                    new MappingInterval(D(7, 11), D(7, 12), "10192"),
                    new MappingInterval(D(7, 12), D(7, 13), "10189"),
                    new MappingInterval(D(7, 13), D(7, 14), "10191"),
                    new MappingInterval(D(7, 14), D(7, 17), "10188"),
                    new MappingInterval(D(7, 17), D(7, 20), "10186"),
                    new MappingInterval(D(7, 20), D(7, 21), "10184"),
                    new MappingInterval(D(7, 21), D(7, 24), "10181"),
                    new MappingInterval(D(7, 24), D(7, 25), "10174"),
                    new MappingInterval(D(7, 25), D(7, 26), "10172"),
                    new MappingInterval(D(7, 26), D(7, 27), "10169"),
                    new MappingInterval(D(7, 27), D(7, 28), "10168"),
                    new MappingInterval(D(7, 28), D(7, 31), "10164"),
                    new MappingInterval(D(7, 31), D(8, 1), "10163"),
                ],
            },
            new SymbolMapping
            {
                RawSymbol = "MSFT",
                Intervals =
                [
                    new MappingInterval(D(7, 1), D(7, 3), "6854"),
                    new MappingInterval(D(7, 3), D(7, 5), "6849"),
                    new MappingInterval(D(7, 5), D(7, 6), "6846"),
                    new MappingInterval(D(7, 6), D(7, 7), "6843"),
                    new MappingInterval(D(7, 7), D(7, 10), "6840"),
                    new MappingInterval(D(7, 10), D(7, 11), "6833"),
                    new MappingInterval(D(7, 11), D(7, 12), "6830"),
                    new MappingInterval(D(7, 12), D(7, 13), "6826"),
                    new MappingInterval(D(7, 13), D(7, 17), "6827"),
                    new MappingInterval(D(7, 17), D(7, 18), "6824"),
                    new MappingInterval(D(7, 18), D(7, 19), "6823"),
                    new MappingInterval(D(7, 19), D(7, 20), "6822"),
                    new MappingInterval(D(7, 20), D(7, 21), "6818"),
                    new MappingInterval(D(7, 21), D(7, 24), "6815"),
                    new MappingInterval(D(7, 24), D(7, 25), "6814"),
                    new MappingInterval(D(7, 25), D(7, 26), "6812"),
                    new MappingInterval(D(7, 26), D(7, 27), "6810"),
                    new MappingInterval(D(7, 27), D(7, 28), "6808"),
                    new MappingInterval(D(7, 28), D(7, 31), "6805"),
                    new MappingInterval(D(7, 31), D(8, 1), "6803"),
                ],
            },
            new SymbolMapping
            {
                RawSymbol = "NVDA",
                Intervals =
                [
                    new MappingInterval(D(7, 1), D(7, 3), "7348"),
                    new MappingInterval(D(7, 3), D(7, 5), "7343"),
                    new MappingInterval(D(7, 5), D(7, 6), "7340"),
                    new MappingInterval(D(7, 6), D(7, 7), "7337"),
                    new MappingInterval(D(7, 7), D(7, 10), "7335"),
                    new MappingInterval(D(7, 10), D(7, 11), "7328"),
                    new MappingInterval(D(7, 11), D(7, 12), "7325"),
                    new MappingInterval(D(7, 12), D(7, 13), "7321"),
                    new MappingInterval(D(7, 13), D(7, 17), "7322"),
                    new MappingInterval(D(7, 17), D(7, 18), "7320"),
                    new MappingInterval(D(7, 18), D(7, 19), "7319"),
                    new MappingInterval(D(7, 19), D(7, 20), "7318"),
                    new MappingInterval(D(7, 20), D(7, 21), "7314"),
                    new MappingInterval(D(7, 21), D(7, 24), "7311"),
                    new MappingInterval(D(7, 24), D(7, 25), "7310"),
                    new MappingInterval(D(7, 25), D(7, 26), "7308"),
                    new MappingInterval(D(7, 26), D(7, 27), "7303"),
                    new MappingInterval(D(7, 27), D(7, 28), "7301"),
                    new MappingInterval(D(7, 28), D(7, 31), "7298"),
                    new MappingInterval(D(7, 31), D(8, 1), "7295"),
                ],
            },
            new SymbolMapping
            {
                RawSymbol = "PLTR",
                Intervals =
                [
                    new MappingInterval(D(7, 1), D(7, 3), "8043"),
                    new MappingInterval(D(7, 3), D(7, 5), "8038"),
                    new MappingInterval(D(7, 5), D(7, 6), "8035"),
                    new MappingInterval(D(7, 6), D(7, 7), "8032"),
                    new MappingInterval(D(7, 7), D(7, 10), "8029"),
                    new MappingInterval(D(7, 10), D(7, 11), "8022"),
                    new MappingInterval(D(7, 11), D(7, 12), "8019"),
                    new MappingInterval(D(7, 12), D(7, 13), "8015"),
                    new MappingInterval(D(7, 13), D(7, 17), "8016"),
                    new MappingInterval(D(7, 17), D(7, 19), "8014"),
                    new MappingInterval(D(7, 19), D(7, 20), "8013"),
                    new MappingInterval(D(7, 20), D(7, 21), "8009"),
                    new MappingInterval(D(7, 21), D(7, 24), "8006"),
                    new MappingInterval(D(7, 24), D(7, 25), "8005"),
                    new MappingInterval(D(7, 25), D(7, 26), "8003"),
                    new MappingInterval(D(7, 26), D(7, 27), "7999"),
                    new MappingInterval(D(7, 27), D(7, 28), "7997"),
                    new MappingInterval(D(7, 28), D(7, 31), "7994"),

                    // Old symbology format: an interval with no resolved symbol at all.
                    new MappingInterval(D(7, 31), D(8, 1), string.Empty),
                ],
            },
        ],
    };

    private static Metadata MetadataWithInverseMappings()
    {
        var forward = MetadataWithMappings();
        var inverseMappings = new List<SymbolMapping>();

        foreach (var mapping in forward.Mappings)
        {
            foreach (var interval in mapping.Intervals)
            {
                if (interval.Symbol.Length == 0)
                {
                    continue;
                }

                inverseMappings.Add(new SymbolMapping
                {
                    RawSymbol = interval.Symbol,
                    Intervals = [new MappingInterval(interval.StartDate, interval.EndDate, mapping.RawSymbol)],
                });
            }
        }

        return new Metadata
        {
            Version = forward.Version,
            Dataset = forward.Dataset,
            Schema = forward.Schema,
            Start = forward.Start,
            End = forward.End,
            Limit = forward.Limit,
            StypeIn = SType.InstrumentId,
            StypeOut = SType.RawSymbol,
            TsOut = forward.TsOut,
            SymbolCstrLength = forward.SymbolCstrLength,
            Symbols = forward.Symbols,
            Partial = forward.Partial,
            NotFound = forward.NotFound,
            Mappings = inverseMappings,
        };
    }

    private static Metadata WithStypes(Metadata metadata, SType? stypeIn, SType stypeOut) => new()
    {
        Version = metadata.Version,
        Dataset = metadata.Dataset,
        Schema = metadata.Schema,
        Start = metadata.Start,
        End = metadata.End,
        Limit = metadata.Limit,
        StypeIn = stypeIn,
        StypeOut = stypeOut,
        TsOut = metadata.TsOut,
        SymbolCstrLength = metadata.SymbolCstrLength,
        Symbols = metadata.Symbols,
        Partial = metadata.Partial,
        NotFound = metadata.NotFound,
        Mappings = metadata.Mappings,
    };

    private static Metadata WithEnd(Metadata metadata, ulong? end) => new()
    {
        Version = metadata.Version,
        Dataset = metadata.Dataset,
        Schema = metadata.Schema,
        Start = metadata.Start,
        End = end,
        Limit = metadata.Limit,
        StypeIn = metadata.StypeIn,
        StypeOut = metadata.StypeOut,
        TsOut = metadata.TsOut,
        SymbolCstrLength = metadata.SymbolCstrLength,
        Symbols = metadata.Symbols,
        Partial = metadata.Partial,
        NotFound = metadata.NotFound,
        Mappings = metadata.Mappings,
    };

    // ------------------------------------------------------------------------------------
    // Hand-built SymbolMappingMsg / SymbolMappingMsgV1 records
    // ------------------------------------------------------------------------------------

    private static SymbolMappingMsg CreateSymbolMappingMsg(uint instrumentId, string stypeOutSymbol)
        => MemoryMarshal.Read<SymbolMappingMsg>(BuildSymbolMappingMsg(instrumentId, stypeOutSymbol).Span);

    private static AlignedBytes BuildSymbolMappingMsg(uint instrumentId, string stypeOutSymbol)
    {
        var bytes = new AlignedBytes(SymbolMappingMsg.WireSize);
        var span = bytes.Span;
        WriteHeader(span, RType.SymbolMapping, SymbolMappingMsg.WireSize, instrumentId);
        span[16] = (byte)SType.InstrumentId;                    // stype_in        @16
        span[88] = (byte)SType.RawSymbol;                       // stype_out       @88
        Ascii.FromUtf16(stypeOutSymbol, span.Slice(89, 71), out _); // stype_out_symbol @89
        BinaryPrimitives.WriteUInt64LittleEndian(span[160..], DbnConstants.UndefTimestamp); // start_ts @160
        BinaryPrimitives.WriteUInt64LittleEndian(span[168..], DbnConstants.UndefTimestamp); // end_ts   @168
        return bytes;
    }

    private static SymbolMappingMsgV1 CreateSymbolMappingMsgV1(uint instrumentId, string stypeOutSymbol)
        => MemoryMarshal.Read<SymbolMappingMsgV1>(BuildSymbolMappingMsgV1(instrumentId, stypeOutSymbol).Span);

    private static AlignedBytes BuildSymbolMappingMsgV1(uint instrumentId, string stypeOutSymbol)
    {
        var bytes = new AlignedBytes(SymbolMappingMsgV1.WireSize);
        var span = bytes.Span;
        WriteHeader(span, RType.SymbolMapping, SymbolMappingMsgV1.WireSize, instrumentId);
        Ascii.FromUtf16(stypeOutSymbol, span.Slice(38, 22), out _); // stype_out_symbol @38
        BinaryPrimitives.WriteUInt64LittleEndian(span[64..], DbnConstants.UndefTimestamp); // start_ts @64
        BinaryPrimitives.WriteUInt64LittleEndian(span[72..], DbnConstants.UndefTimestamp); // end_ts   @72
        return bytes;
    }

    private static void WriteHeader(Span<byte> bytes, RType rtype, int sizeInBytes, uint instrumentId)
    {
        bytes[0] = (byte)(sizeInBytes / DbnConstants.RecordLengthMultiplier);
        bytes[1] = (byte)rtype;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[2..], 1);            // publisher_id  @2
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], instrumentId); // instrument_id @4
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], 3);            // ts_event      @8
    }

    /// <summary>
    /// Zeroed storage whose first byte is 8-byte aligned, which is what reinterpreting bytes as a
    /// record requires — the same shape as <c>RecordRefTests.AlignedBytes</c>. A <c>byte[]</c>
    /// carries no such guarantee.
    /// </summary>
    private sealed class AlignedBytes
    {
        private readonly ulong[] _storage;

        public AlignedBytes(int sizeInBytes)
        {
            Length = sizeInBytes;
            _storage = new ulong[(sizeInBytes + 7) / 8];
        }

        public int Length { get; }

        public Span<byte> Span => MemoryMarshal.AsBytes(_storage.AsSpan())[..Length];
    }
}
