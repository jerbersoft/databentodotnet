using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NodaTime;

namespace DatabentoDotNet.Dbn.Tests;

/// <summary>
/// Conformance tests for <see cref="ISymbolIndex"/>: resolving a symbol from a decoded record
/// rather than from a caller-supplied instrument ID and date.
/// </summary>
/// <remarks>
/// <para>
/// <b>What these tests are for.</b> Assembling the key by hand is easy to get wrong in a way
/// nothing reports: fourteen of the twenty-one record structs index on <c>ts_recv</c>, not on the
/// <c>ts_event</c> every record has, and the two routinely fall on opposite sides of UTC
/// midnight. <c>get_for_rec</c> exists so that a consumer never assembles the key at all — so the
/// assertions below are mostly about <em>which</em> field the resolution reached for, not about
/// whether a dictionary lookup works.
/// </para>
/// <para>
/// <b>The two maps deliberately disagree.</b> <see cref="TsSymbolMap"/> keys on the record's own
/// index date; <see cref="PitSymbolMap"/> ignores the record's timestamp entirely, because the
/// caller already committed to one date when the map was built. Both match upstream
/// (<c>symbol_map.rs:165-171, 336-340</c>), and both directions are asserted here so that
/// "fixing" either into symmetry fails.
/// </para>
/// <para>
/// The per-record-type table — which struct indexes on which field, and therefore what
/// <see cref="RecordRefExtensions.IndexDate"/> answers — lives in <see cref="IndexTsTests"/>,
/// where it is asserted exhaustively against a guard that fails if a record type is ever added
/// to the library without being listed. It is not restated here.
/// </para>
/// </remarks>
public class SymbolIndexTests
{
    /// <summary>2023-07-03T23:59:59.999999999Z — the last nanosecond of the 3rd.</summary>
    private const ulong TsEventValue = 1_688_428_799_999_999_999UL;

    /// <summary>2023-07-04T00:00:00Z — one nanosecond later, and one day on.</summary>
    private const ulong TsRecvValue = 1_688_428_800_000_000_000UL;

    private const uint InstrumentId = 1;

    private static readonly LocalDate TsEventDate = new(2023, 7, 3);
    private static readonly LocalDate TsRecvDate = new(2023, 7, 4);

    // ------------------------------------------------------------------------------------
    // IndexDate: the conversion, and the sentinel it has to refuse
    // ------------------------------------------------------------------------------------

    [Fact]
    public void TryIndexDate_UndefinedIndexTs_ReturnsFalse()
    {
        // MboMsg indexes on ts_recv, so writing the sentinel there is what makes this record's
        // index timestamp undefined — its ts_event is a perfectly ordinary 2023 timestamp, and a
        // conversion that reached for ts_event instead would return a date and pass.
        var record = Record<MboMsg>(RType.Mbo, DbnConstants.UndefTimestamp);

        Assert.False(new RecordRef(record.Span).TryIndexDate(out var date));
        Assert.Equal(default, date);
    }

    [Fact]
    public void IndexDate_UndefinedIndexTs_ThrowsInvalidOperationException()
    {
        var record = Record<MboMsg>(RType.Mbo, DbnConstants.UndefTimestamp);

        // Not ArgumentOutOfRangeException, which is what DbnTime.ToUtcDate raises: this overload
        // takes no argument, so naming one would point the reader at a parameter that is not
        // there. See the remarks on RecordRefExtensions.IndexDate.
        Assert.Throws<InvalidOperationException>(() => new RecordRef(record.Span).IndexDate());
    }

    [Fact]
    public void IndexDate_DoesNotFloorDivideTheSentinelIntoAPlausibleDay()
    {
        // The failure this guards is not an exception, it is an answer: ulong.MaxValue
        // nanoseconds floor-divides to an entirely ordinary-looking day in 2554, and a symbol map
        // keyed on it would simply return nothing while looking like it had asked a fair
        // question.
        var record = Record<MboMsg>(RType.Mbo, DbnConstants.UndefTimestamp);

        Assert.False(new RecordRef(record.Span).TryIndexDate(out var date));
        Assert.NotEqual(2554, date.Year);
    }

    // ------------------------------------------------------------------------------------
    // TsSymbolMap: keyed on the record's own index date
    // ------------------------------------------------------------------------------------

    [Fact]
    public void TsSymbolMap_TryGetSymbol_KeysOnTheIndexTimestamp_NotOnTsEvent()
    {
        // The trap, resolved through get_for_rec instead of by hand. The map holds one mapping
        // that expires at midnight on the 4th and another that begins there; the record's
        // ts_event is one nanosecond before that midnight and its ts_recv is exactly on it. A
        // resolution that reached for ts_event returns "ESM3" — the previous day's symbol, with
        // no error anywhere.
        var map = StraddlingMap();
        var record = Record<MboMsg>(RType.Mbo, TsRecvValue);

        Assert.True(map.TryGetSymbol(new RecordRef(record.Span), out var byRecordRef));
        Assert.Equal("ESU3", byRecordRef);

        Assert.True(map.TryGetSymbol(in AsRecord<MboMsg>(record), out var byTypedRecord));
        Assert.Equal("ESU3", byTypedRecord);

        // Both are the answer the explicit overload gives for the index date, and neither is the
        // answer it gives for ts_event.
        Assert.True(map.TryGetSymbol(TsRecvDate, InstrumentId, out var byIndexDate));
        Assert.Equal(byIndexDate, byRecordRef);

        Assert.True(map.TryGetSymbol(TsEventDate, InstrumentId, out var byTsEventDate));
        Assert.Equal("ESM3", byTsEventDate);
        Assert.NotEqual(byTsEventDate, byRecordRef);
    }

    [Fact]
    public void TsSymbolMap_TryGetSymbol_UndefinedIndexTs_ReturnsFalse()
    {
        // Upstream's index_date() returns None here and and_then short-circuits, so the lookup
        // never happens. The instrument IS mapped — on every day the map covers — so a
        // resolution that fell back to ts_event, or that converted the sentinel to its
        // floor-divided day, would answer something rather than nothing.
        var map = StraddlingMap();
        var record = Record<MboMsg>(RType.Mbo, DbnConstants.UndefTimestamp);

        Assert.False(map.TryGetSymbol(new RecordRef(record.Span), out var symbol));
        Assert.Null(symbol);

        Assert.False(map.TryGetSymbol(in AsRecord<MboMsg>(record), out var typedSymbol));
        Assert.Null(typedSymbol);
    }

    [Fact]
    public void TsSymbolMap_TryGetSymbol_UnmappedInstrument_ReturnsFalse()
    {
        var map = StraddlingMap();
        var record = Record<MboMsg>(RType.Mbo, TsRecvValue, instrumentId: 999);

        Assert.False(map.TryGetSymbol(new RecordRef(record.Span), out var symbol));
        Assert.Null(symbol);
    }

    [Fact]
    public void TsSymbolMap_TryGetSymbol_DateOutsideEveryInterval_ReturnsFalse()
    {
        // 2023-07-04 is mapped; 2023-07-08 is where the second interval ends, exclusive.
        var map = StraddlingMap();
        var record = Record<MboMsg>(RType.Mbo, UnixNanos(2023, 7, 8));

        Assert.False(map.TryGetSymbol(new RecordRef(record.Span), out var symbol));
        Assert.Null(symbol);
    }

    // ------------------------------------------------------------------------------------
    // PitSymbolMap: the record's timestamp is not consulted at all
    // ------------------------------------------------------------------------------------

    [Fact]
    public void PitSymbolMap_TryGetSymbol_IgnoresTheRecordsDateEntirely()
    {
        // Upstream's impl is `self.get(record.header().instrument_id)` — no index_date() anywhere
        // (symbol_map.rs:336-340). A point-in-time map has already been resolved for one date, so
        // a record from a wildly different day still resolves. This is the divergence from
        // TsSymbolMap, asserted so that "fixing" it into symmetry fails.
        var map = new PitSymbolMap();
        map.OnSymbolMapping(SymbolMappingRecord("ESU3"));

        var farFuture = Record<MboMsg>(RType.Mbo, UnixNanos(2031, 1, 1));

        Assert.True(map.TryGetSymbol(new RecordRef(farFuture.Span), out var symbol));
        Assert.Equal("ESU3", symbol);

        Assert.True(map.TryGetSymbol(in AsRecord<MboMsg>(farFuture), out var typedSymbol));
        Assert.Equal("ESU3", typedSymbol);
    }

    [Fact]
    public void PitSymbolMap_TryGetSymbol_UndefinedIndexTs_StillResolves()
    {
        // The consequence of never reading the timestamp: the sentinel that makes TsSymbolMap
        // report a miss is invisible here.
        var map = new PitSymbolMap();
        map.OnSymbolMapping(SymbolMappingRecord("ESU3"));

        var record = Record<MboMsg>(RType.Mbo, DbnConstants.UndefTimestamp);

        Assert.True(map.TryGetSymbol(new RecordRef(record.Span), out var symbol));
        Assert.Equal("ESU3", symbol);
    }

    [Fact]
    public void PitSymbolMap_TryGetSymbol_UnmappedInstrument_ReturnsFalse()
    {
        var map = new PitSymbolMap();
        map.OnSymbolMapping(SymbolMappingRecord("ESU3"));

        var record = Record<MboMsg>(RType.Mbo, TsRecvValue, instrumentId: 999);

        Assert.False(map.TryGetSymbol(new RecordRef(record.Span), out var symbol));
        Assert.Null(symbol);
    }

    // ------------------------------------------------------------------------------------
    // The typed overload reads the instrument ID off the record's own header
    // ------------------------------------------------------------------------------------

    [Fact]
    public void TypedOverload_ReadsTheInstrumentIdFromTheRecordsHeader_ForALargeRecord()
    {
        // SymbolMapSupport.InstrumentIdOf reinterprets the record at offset 0 as a RecordHeader,
        // relying on the tested invariant that every record declares its header first. An
        // InstrumentDefMsg is 520 bytes with a great deal after that header, so it is the type
        // where a wrong offset would read something plausible rather than crash.
        var map = new PitSymbolMap();
        map.OnSymbolMapping(SymbolMappingRecord("ESU3"));

        var record = Record<InstrumentDefMsg>(RType.InstrumentDef, TsRecvValue);

        Assert.True(map.TryGetSymbol(in AsRecord<InstrumentDefMsg>(record), out var symbol));
        Assert.Equal("ESU3", symbol);

        // ...and it is the same instrument ID RecordRef reads through the header directly.
        Assert.Equal(InstrumentId, new RecordRef(record.Span).Header.InstrumentId);
    }

    [Fact]
    public void TypedOverload_ReadsTheIndexTimestampFromTheRecordsOwnField_ForALargeRecord()
    {
        // The same 520-byte record, through TsSymbolMap, where both halves of the key have to be
        // right: ts_recv sits 200-odd bytes into an InstrumentDefMsg, nowhere near the header.
        var map = StraddlingMap();
        var record = Record<InstrumentDefMsg>(RType.InstrumentDef, TsRecvValue);

        Assert.True(map.TryGetSymbol(in AsRecord<InstrumentDefMsg>(record), out var symbol));
        Assert.Equal("ESU3", symbol);
    }

    // ------------------------------------------------------------------------------------
    // ISymbolIndex: the point of the interface is that both maps answer through it
    // ------------------------------------------------------------------------------------

    [Fact]
    public void ISymbolIndex_ResolvesThroughEitherMapWithoutTheCallerKnowingWhich()
    {
        var ts = StraddlingMap();
        var pit = new PitSymbolMap();
        pit.OnSymbolMapping(SymbolMappingRecord("ESU3"));

        var record = Record<MboMsg>(RType.Mbo, TsRecvValue);

        foreach (ISymbolIndex index in new ISymbolIndex[] { ts, pit })
        {
            Assert.True(index.TryGetSymbol(new RecordRef(record.Span), out var symbol));
            Assert.Equal("ESU3", symbol);

            Assert.True(index.TryGetSymbol(in AsRecord<MboMsg>(record), out var typedSymbol));
            Assert.Equal("ESU3", typedSymbol);
        }
    }

    // ------------------------------------------------------------------------------------
    // Definition of done: get_for_rec agrees with the explicit overload, over real wire bytes
    // ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("test_data.definition.dbn")]
    [InlineData("test_data.definition.v1.dbn.zst")]
    [InlineData("test_data.definition.v2.dbn.zst")]
    [InlineData("test_data.definition.v3.dbn.zst")]
    public void TryGetSymbol_DefinitionFixture_AgreesWithTheExplicitOverload(string fixtureName)
    {
        var bytes = TestFixtures.ReadDecompressed(
            TestFixtures.All.Single(fixture => fixture.Name == fixtureName));
        using var decoder = new DbnDecoder(new MemoryStream(bytes));
        var metadata = decoder.Metadata!;
        var tsMap = TsSymbolMap.FromMetadata(metadata);

        var instrumentCount = 0;
        while (decoder.TryNextRecord(out var record))
        {
            Assert.True(record.Has<InstrumentDefMsg>());
            ref readonly var def = ref record.Get<InstrumentDefMsg>();
            instrumentCount++;

            // The expected date is computed in NodaTime directly rather than through DbnTime or
            // IndexDate, so it stays independent of the conversion under test — the same reason
            // SymbolMapTests keeps its own arithmetic. InstrumentDefMsg indexes on ts_recv.
            var date = IndexDateIndependently(def.TsRecv);
            var expectedSymbol = def.RawSymbol.ToString();

            Assert.True(
                tsMap.TryGetSymbol(date, def.Header.InstrumentId, out var explicitSymbol),
                $"TsSymbolMap has no mapping for instrument {def.Header.InstrumentId} on {date}.");
            Assert.Equal(expectedSymbol, explicitSymbol);

            Assert.True(tsMap.TryGetSymbol(record, out var byRecordRef));
            Assert.Equal(explicitSymbol, byRecordRef);

            Assert.True(tsMap.TryGetSymbol(in def, out var byTypedRecord));
            Assert.Equal(explicitSymbol, byTypedRecord);

            // ...and the same for the point-in-time map, whose explicit overload takes no date.
            var pitMap = PitSymbolMap.FromMetadata(metadata, date);
            Assert.True(pitMap.TryGetSymbol(def.Header.InstrumentId, out var pitExplicit));
            Assert.Equal(expectedSymbol, pitExplicit);

            Assert.True(pitMap.TryGetSymbol(record, out var pitByRecordRef));
            Assert.Equal(pitExplicit, pitByRecordRef);

            Assert.True(pitMap.TryGetSymbol(in def, out var pitByTypedRecord));
            Assert.Equal(pitExplicit, pitByTypedRecord);
        }

        // The fixture census, restated as an assertion: if a re-vendor ever changes the file,
        // this stops silently exercising two instruments and starts silently exercising zero.
        Assert.Equal(2, instrumentCount);
    }

    // ------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// A map whose two intervals meet exactly at midnight on 2023-07-04, so that
    /// <see cref="TsEventValue"/> and <see cref="TsRecvValue"/> resolve to different symbols.
    /// </summary>
    private static TsSymbolMap StraddlingMap()
    {
        var map = new TsSymbolMap();
        map.Insert(InstrumentId, new LocalDate(2023, 7, 1), TsRecvDate, "ESM3");
        map.Insert(InstrumentId, TsRecvDate, new LocalDate(2023, 7, 8), "ESU3");
        return map;
    }

    /// <summary>
    /// A zeroed, 8-byte-aligned record of <typeparamref name="T"/>'s wire size, with its header
    /// length, rtype, instrument ID and <c>ts_event</c> set, and <paramref name="indexTs"/>
    /// written at <typeparamref name="T"/>'s <c>ts_recv</c> offset.
    /// </summary>
    /// <remarks>
    /// <c>ts_event</c> is always <see cref="TsEventValue"/>, one day before
    /// <see cref="TsRecvDate"/>. That is what makes every assertion above discriminating: a
    /// resolution that reached for <c>ts_event</c> would land on a different day, not merely a
    /// different nanosecond. The <c>ts_recv</c> offset is found by reflection because it is not
    /// fixed — <see cref="MboMsg"/> puts it 40 bytes in, <see cref="InstrumentDefMsg"/> far
    /// further.
    /// </remarks>
    private static AlignedRecord Record<T>(RType rtype, ulong indexTs, uint instrumentId = InstrumentId)
        where T : unmanaged, IRecord<T>
    {
        var record = new AlignedRecord(T.WireSize);
        record.Span[0] = checked((byte)(T.WireSize / DbnConstants.RecordLengthMultiplier));
        record.Span[1] = (byte)rtype;
        BinaryPrimitives.WriteUInt32LittleEndian(record.Span[4..], instrumentId);
        BinaryPrimitives.WriteUInt64LittleEndian(record.Span[8..], TsEventValue);
        BinaryPrimitives.WriteUInt64LittleEndian(record.Span[(int)Marshal.OffsetOf<T>("TsRecv")..], indexTs);
        return record;
    }

    private static ref readonly T AsRecord<T>(AlignedRecord record)
        where T : unmanaged, IRecord<T>
        => ref MemoryMarshal.AsRef<T>(record.Span);

    /// <summary>A <see cref="SymbolMappingMsg"/> mapping <see cref="InstrumentId"/> to a symbol.</summary>
    private static SymbolMappingMsg SymbolMappingRecord(string symbol)
    {
        var record = new AlignedRecord(SymbolMappingMsg.WireSize);
        record.Span[0] = checked((byte)(SymbolMappingMsg.WireSize / DbnConstants.RecordLengthMultiplier));
        record.Span[1] = (byte)RType.SymbolMapping;
        BinaryPrimitives.WriteUInt32LittleEndian(record.Span[4..], InstrumentId);
        System.Text.Encoding.ASCII.GetBytes(
            symbol,
            record.Span[(int)Marshal.OffsetOf<SymbolMappingMsg>("StypeOutSymbol")..]);
        return MemoryMarshal.AsRef<SymbolMappingMsg>(record.Span);
    }

    /// <summary>
    /// The UTC date a raw nanosecond timestamp falls on, computed in NodaTime directly so the
    /// expected value stays independent of <see cref="DbnTime"/>, which is under test.
    /// </summary>
    private static LocalDate IndexDateIndependently(ulong unixNanoseconds)
        => (NodaConstants.UnixEpoch + Duration.FromNanoseconds((long)unixNanoseconds)).InUtc().Date;

    private static ulong UnixNanos(int year, int month, int day)
        => (ulong)(Instant.FromUtc(year, month, day, 0, 0) - NodaConstants.UnixEpoch).ToInt64Nanoseconds();

    /// <summary>
    /// Zeroed storage whose first byte is 8-byte aligned, which is what reinterpreting bytes as a
    /// record requires. A <c>byte[]</c> carries no such guarantee.
    /// </summary>
    private sealed class AlignedRecord
    {
        private readonly ulong[] _storage;
        private readonly int _length;

        public AlignedRecord(int sizeInBytes)
        {
            _length = sizeInBytes;
            _storage = new ulong[(sizeInBytes + 7) / 8];
        }

        public Span<byte> Span => MemoryMarshal.AsBytes(_storage.AsSpan())[.._length];
    }
}
