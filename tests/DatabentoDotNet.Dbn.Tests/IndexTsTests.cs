using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NodaTime;

namespace DatabentoDotNet.Dbn.Tests;

/// <summary>
/// Conformance tests for <see cref="IRecord{TSelf}.IndexTs"/> and <see cref="RecordRef.IndexTs"/>:
/// which timestamp each record type indexes on.
/// </summary>
/// <remarks>
/// <para>
/// The bug this file exists to prevent has no symptom. A consumer resolving a symbol reaches for
/// <c>Header.TsEvent</c> because it is the timestamp every record has, but fourteen of the
/// twenty-one record structs index on <c>ts_recv</c>. The two straddle UTC midnight often enough
/// to matter, and when they do the wrong one silently returns the previous day's symbol, or
/// nothing — no exception, no wrong-looking number, just a mapping that is quietly off by a day.
/// </para>
/// <para>
/// Records are hand-built rather than pulled from a fixture, for the same reason
/// <see cref="RecordRefTests"/> builds its own: a fixture can only exercise the field values it
/// happens to contain, and this needs <c>ts_event</c> and <c>ts_recv</c> set to <em>different</em>
/// values in every record so that reading the wrong one cannot accidentally produce the right
/// answer.
/// </para>
/// </remarks>
public class IndexTsTests
{
    /// <summary>Written to every record's <c>ts_event</c>. 2023-07-03T23:59:59.999999999Z.</summary>
    private const ulong TsEventValue = 1_688_428_799_999_999_999UL;

    /// <summary>
    /// Written to every record's <c>ts_recv</c> slot, including on records that have no
    /// <c>ts_recv</c> — a decoy, so that a struct which wrongly indexed on the bytes after its
    /// header would return this instead of <see cref="TsEventValue"/> and fail.
    /// 2023-07-04T00:00:00Z, one nanosecond later and one day on.
    /// </summary>
    private const ulong TsRecvValue = 1_688_428_800_000_000_000UL;

    /// <summary>Which field a record struct's index timestamp comes from.</summary>
    private enum IndexField
    {
        /// <summary>The header's <c>ts_event</c> — upstream's default for records with no <c>ts_recv</c>.</summary>
        TsEvent,

        /// <summary>The struct's own <c>ts_recv</c> — upstream's <c>#[dbn(index_ts)]</c> override.</summary>
        TsRecv,
    }

    // ------------------------------------------------------------------------------------
    // Per-type conformance, and the guard that keeps it exhaustive
    // ------------------------------------------------------------------------------------

    [Fact]
    public void IndexTs_MatchesUpstreamsPerTypeChoice_ForEveryRecord()
    {
        VisitEveryListedRecord(covered: null);
    }

    [Fact]
    public void EveryRecordStructInTheAssembly_IsInTheIndexFieldList()
    {
        // The same guard-on-the-guard as RecordLayoutTests'. VisitEveryListedRecord is a
        // hand-written list, so a record struct added to the library and not to the list would
        // never be asked which timestamp it indexes on — and the suite would stay green while
        // the new type silently defaulted to whatever its author happened to write.
        var implementers = typeof(IRecord<>).Assembly
            .GetTypes()
            .Where(type => !type.IsGenericTypeDefinition)
            .Where(type => type.GetInterfaces().Any(
                iface => iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IRecord<>)))
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var covered = new List<Type>();
        VisitEveryListedRecord(covered);
        var listed = covered.Select(type => type.Name).OrderBy(name => name, StringComparer.Ordinal).ToList();

        Assert.Equal(covered.Count, covered.Distinct().Count());

        var missing = implementers.Except(listed, StringComparer.Ordinal).ToList();
        var extra = listed.Except(implementers, StringComparer.Ordinal).ToList();
        Assert.True(
            missing.Count == 0 && extra.Count == 0,
            $"VisitEveryListedRecord no longer enumerates every IRecord<> implementer.{Environment.NewLine}" +
            $"  Implements IRecord<> but is not in the list, so which timestamp it indexes on is " +
            $"unchecked: [{string.Join(", ", missing)}]{Environment.NewLine}" +
            $"  In the list but no longer implements IRecord<>: [{string.Join(", ", extra)}]");

        Assert.Equal(implementers, listed);
    }

    /// <summary>
    /// The one hand-maintained table: every record struct, the rtype it decodes, and which
    /// timestamp upstream indexes it on.
    /// </summary>
    /// <remarks>
    /// Transcribed from upstream's <c>#[dbn(..., index_ts, ...)]</c> field attributes
    /// (<c>record.rs</c> for the current structs, <c>compat.rs</c> for the legacy ones) against
    /// the <c>Record::raw_index_ts</c> default of <c>ts_event</c>
    /// (<c>record/traits.rs:52-54</c>). Fourteen structs carry the attribute, and in dbn 0.68.0
    /// every one of them puts it on <c>ts_recv</c>.
    /// </remarks>
    /// <param name="covered">
    /// Collects each visited record type, for the coverage test. <see langword="null"/> to just
    /// run the assertions.
    /// </param>
    private static void VisitEveryListedRecord(List<Type>? covered)
    {
        AssertIndexTs<MboMsg>(RType.Mbo, IndexField.TsRecv, covered);
        AssertIndexTs<TradeMsg>(RType.Mbp0, IndexField.TsRecv, covered);
        AssertIndexTs<Mbp1Msg>(RType.Mbp1, IndexField.TsRecv, covered);
        AssertIndexTs<Mbp10Msg>(RType.Mbp10, IndexField.TsRecv, covered);
        AssertIndexTs<BboMsg>(RType.Bbo1S, IndexField.TsRecv, covered);
        AssertIndexTs<Cmbp1Msg>(RType.Cmbp1, IndexField.TsRecv, covered);
        AssertIndexTs<CbboMsg>(RType.Cbbo1S, IndexField.TsRecv, covered);
        AssertIndexTs<StatusMsg>(RType.Status, IndexField.TsRecv, covered);
        AssertIndexTs<ImbalanceMsg>(RType.Imbalance, IndexField.TsRecv, covered);
        AssertIndexTs<InstrumentDefMsg>(RType.InstrumentDef, IndexField.TsRecv, covered);
        AssertIndexTs<InstrumentDefMsgV2>(RType.InstrumentDef, IndexField.TsRecv, covered);
        AssertIndexTs<InstrumentDefMsgV1>(RType.InstrumentDef, IndexField.TsRecv, covered);
        AssertIndexTs<StatMsg>(RType.Statistics, IndexField.TsRecv, covered);
        AssertIndexTs<StatMsgV1>(RType.Statistics, IndexField.TsRecv, covered);

        // No ts_recv on the wire at all, so upstream's default applies.
        AssertIndexTs<OhlcvMsg>(RType.Ohlcv1S, IndexField.TsEvent, covered);
        AssertIndexTs<ErrorMsg>(RType.Error, IndexField.TsEvent, covered);
        AssertIndexTs<ErrorMsgV1>(RType.Error, IndexField.TsEvent, covered);
        AssertIndexTs<SymbolMappingMsg>(RType.SymbolMapping, IndexField.TsEvent, covered);
        AssertIndexTs<SymbolMappingMsgV1>(RType.SymbolMapping, IndexField.TsEvent, covered);
        AssertIndexTs<SystemMsg>(RType.System, IndexField.TsEvent, covered);
        AssertIndexTs<SystemMsgV1>(RType.System, IndexField.TsEvent, covered);
    }

    // ------------------------------------------------------------------------------------
    // Every rtype alias reaches the same struct
    // ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(RType.Cmbp1)]
    [InlineData(RType.Tcbbo)]
    public void RecordRefIndexTs_BothCmbp1RTypes_ResolveThroughCmbp1Msg(RType rtype)
        => AssertIndexTs<Cmbp1Msg>(rtype, IndexField.TsRecv, covered: null);

    [Theory]
    [InlineData(RType.Cbbo1S)]
    [InlineData(RType.Cbbo1M)]
    public void RecordRefIndexTs_BothCbboRTypes_ResolveThroughCbboMsg(RType rtype)
        => AssertIndexTs<CbboMsg>(rtype, IndexField.TsRecv, covered: null);

    [Theory]
    [InlineData(RType.Bbo1S)]
    [InlineData(RType.Bbo1M)]
    public void RecordRefIndexTs_BothBboRTypes_ResolveThroughBboMsg(RType rtype)
        => AssertIndexTs<BboMsg>(rtype, IndexField.TsRecv, covered: null);

    [Theory]
    [InlineData(RType.Ohlcv1S)]
    [InlineData(RType.Ohlcv1M)]
    [InlineData(RType.Ohlcv1H)]
    [InlineData(RType.Ohlcv1D)]
    [InlineData(RType.OhlcvEod)]
    [InlineData(RType.OhlcvDeprecated)]
    public void RecordRefIndexTs_EveryOhlcvRType_FallsBackToTsEvent(RType rtype)
        => AssertIndexTs<OhlcvMsg>(rtype, IndexField.TsEvent, covered: null);

    // ------------------------------------------------------------------------------------
    // RecordRef's fallbacks
    // ------------------------------------------------------------------------------------

    [Fact]
    public void RecordRefIndexTs_UnrecognizedRType_FallsBackToTsEvent()
    {
        // Upstream checks the rtype up front and returns ts_event rather than running a dispatch
        // it knows will fail (record_ref.rs:340-344). 0x7F is not an assigned rtype.
        var bytes = BuildRecord((RType)0x7F, MboMsg.WireSize);
        WriteTsRecv<MboMsg>(bytes, TsRecvValue);

        Assert.Equal(TsEventValue, new RecordRef(bytes.Span).IndexTs);
    }

    [Fact]
    public void RecordRefIndexTs_TsRecvRTypeAtAnUnknownLength_FallsBackToTsEvent()
    {
        // rtype says MBO, which indexes on ts_recv, but the length matches no version of MboMsg.
        // The bytes at MboMsg's ts_recv offset are not a ts_recv, so reading them would return a
        // plausible number that is not a timestamp. Has<T> is what stops that.
        var bytes = BuildRecord(RType.Mbo, MboMsg.WireSize + 8);
        WriteTsRecv<MboMsg>(bytes, TsRecvValue);

        Assert.Equal(TsEventValue, new RecordRef(bytes.Span).IndexTs);
    }

    [Fact]
    public void RecordRefIndexTs_StreamWithTsOut_StillIndexesOnTsRecv()
    {
        // ts_out is the gateway's send time and is never the index timestamp. StructSize, not
        // SizeInBytes, is what Has<T> compares — so the extra eight bytes must not shift the
        // record into the "unknown length" fallback above.
        var bytes = BuildRecord(RType.Mbo, MboMsg.WireSize + sizeof(ulong));
        WriteTsRecv<MboMsg>(bytes, TsRecvValue);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.Span[MboMsg.WireSize..], 42UL);

        var record = new RecordRef(bytes.Span, hasTsOut: true);

        Assert.Equal(TsRecvValue, record.IndexTs);
        Assert.Equal(42UL, record.TsOut);
    }

    [Fact]
    public void WithTsOutIndexTs_ForwardsTheWrappedRecordsIndexTs()
    {
        var bytes = BuildRecord(RType.Mbo, MboMsg.WireSize);
        WriteTsRecv<MboMsg>(bytes, TsRecvValue);
        var wrapped = new WithTsOut<MboMsg>(MemoryMarshal.AsRef<MboMsg>(bytes.Span), tsOut: 42UL);

        Assert.Equal(TsRecvValue, wrapped.IndexTs);
        Assert.Equal(42UL, wrapped.TsOut);
    }

    [Fact]
    public void IndexTs_UndefTimestamp_IsReturnedRaw()
    {
        // IndexTs is a raw wire value and does not interpret the sentinel; DbnTime is where that
        // check lives. A record whose ts_recv is undefined must not silently fall back to
        // ts_event either — "undefined" and "the header's timestamp" are different answers.
        var bytes = BuildRecord(RType.Mbo, MboMsg.WireSize);
        WriteTsRecv<MboMsg>(bytes, DbnConstants.UndefTimestamp);

        Assert.Equal(DbnConstants.UndefTimestamp, new RecordRef(bytes.Span).IndexTs);
        Assert.False(DbnTime.TryToUtcDate(new RecordRef(bytes.Span).IndexTs, out _));
    }

    // ------------------------------------------------------------------------------------
    // The trap itself: ts_event and ts_recv on opposite sides of UTC midnight
    // ------------------------------------------------------------------------------------

    [Fact]
    public void IndexTs_WhenTsEventAndTsRecvStraddleUtcMidnight_ResolveToDifferentDays()
    {
        // One nanosecond apart, one day apart. TsEventValue is 2023-07-03T23:59:59.999999999Z and
        // TsRecvValue is 2023-07-04T00:00:00Z.
        var bytes = BuildRecord(RType.Mbo, MboMsg.WireSize);
        WriteTsRecv<MboMsg>(bytes, TsRecvValue);
        var record = new RecordRef(bytes.Span);

        Assert.Equal(new LocalDate(2023, 7, 3), DbnTime.ToUtcDate(record.Header.TsEvent));
        Assert.Equal(new LocalDate(2023, 7, 4), DbnTime.ToUtcDate(record.IndexTs));
    }

    [Fact]
    public void IndexTs_WhenTsEventAndTsRecvStraddleUtcMidnight_ResolveToDifferentSymbols()
    {
        // The consequence, spelled out. The symbol map holds one mapping that expires at midnight
        // on the 4th and another that begins there. Keyed on ts_event the lookup returns the
        // stale symbol; keyed on the index timestamp it returns the current one. Neither errors.
        var map = new TsSymbolMap();
        map.Insert(1, new LocalDate(2023, 7, 1), new LocalDate(2023, 7, 4), "ESM3");
        map.Insert(1, new LocalDate(2023, 7, 4), new LocalDate(2023, 7, 8), "ESU3");

        var bytes = BuildRecord(RType.Mbo, MboMsg.WireSize);
        WriteTsRecv<MboMsg>(bytes, TsRecvValue);
        var record = new RecordRef(bytes.Span);

        Assert.True(map.TryGetSymbol(DbnTime.ToUtcDate(record.Header.TsEvent), 1, out var byTsEvent));
        Assert.Equal("ESM3", byTsEvent);

        Assert.True(map.TryGetSymbol(DbnTime.ToUtcDate(record.IndexTs), 1, out var byIndexTs));
        Assert.Equal("ESU3", byIndexTs);
    }

    // ------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// Asserts that <typeparamref name="T"/> and <see cref="RecordRef"/> both read
    /// <paramref name="expected"/> as the index timestamp of a record built for
    /// <paramref name="rtype"/>.
    /// </summary>
    private static void AssertIndexTs<T>(RType rtype, IndexField expected, List<Type>? covered)
        where T : unmanaged, IRecord<T>
    {
        covered?.Add(typeof(T));

        var bytes = BuildRecord(rtype, T.WireSize);
        WriteTsRecv<T>(bytes, TsRecvValue);
        var expectedTs = expected == IndexField.TsRecv ? TsRecvValue : TsEventValue;

        // Through the concrete struct, which is what a consumer holding a decoded record calls...
        Assert.Equal(expectedTs, MemoryMarshal.AsRef<T>(bytes.Span).IndexTs);

        // ...and through RecordRef's rtype dispatch, which is what a live stream calls. The two
        // must agree: RecordRef's switch is a second, independent statement of the same table.
        Assert.Equal(expectedTs, new RecordRef(bytes.Span).IndexTs);
    }

    /// <summary>
    /// Writes <paramref name="value"/> at <typeparamref name="T"/>'s <c>ts_recv</c> offset, found
    /// by reflection rather than hard-coded.
    /// </summary>
    /// <remarks>
    /// <c>ts_recv</c> is not at a fixed offset — <see cref="MboMsg"/> puts it 40 bytes in, behind
    /// the order ID, price, size, flags, channel ID, action and side, while
    /// <see cref="StatusMsg"/> puts it directly after the header, at 16. A hard-coded offset would write into some other field and the assertion would
    /// pass for the wrong reason. Records with no <c>ts_recv</c> get the value written at offset
    /// 16 instead, as a decoy: a struct that wrongly indexed on the bytes after its header would
    /// then return it and fail.
    /// </remarks>
    private static void WriteTsRecv<T>(AlignedBytes bytes, ulong value)
        where T : unmanaged, IRecord<T>
    {
        var offset = typeof(T).GetField("TsRecv") is null
            ? Unsafe.SizeOf<RecordHeader>()
            : (int)Marshal.OffsetOf<T>("TsRecv");

        // The offset has to land inside the record and leave room for the timestamp, or the
        // write would be scribbling past the struct and the assertion would mean nothing.
        Assert.InRange(offset, Unsafe.SizeOf<RecordHeader>(), T.WireSize - sizeof(ulong));

        BinaryPrimitives.WriteUInt64LittleEndian(bytes.Span[offset..], value);
    }

    /// <summary>
    /// A zeroed, 8-byte-aligned record of <paramref name="sizeInBytes"/> bytes, with its header
    /// length and rtype set and its <c>ts_event</c> set to <see cref="TsEventValue"/>.
    /// </summary>
    /// <remarks>
    /// <c>ts_recv</c> is left zeroed — its offset depends on the record type, so callers set it
    /// with <see cref="WriteTsRecv{T}"/> once they have named that type.
    /// </remarks>
    private static AlignedBytes BuildRecord(RType rtype, int sizeInBytes)
    {
        var bytes = new AlignedBytes(sizeInBytes);
        bytes.Span[0] = checked((byte)(sizeInBytes / DbnConstants.RecordLengthMultiplier));
        bytes.Span[1] = (byte)rtype;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.Span[8..], TsEventValue);
        return bytes;
    }

    /// <summary>
    /// Zeroed storage whose first byte is 8-byte aligned, which is what reinterpreting bytes as a
    /// record requires. A <c>byte[]</c> carries no such guarantee.
    /// </summary>
    private sealed class AlignedBytes
    {
        private readonly ulong[] _storage;
        private readonly int _length;

        public AlignedBytes(int sizeInBytes)
        {
            _length = sizeInBytes;
            _storage = new ulong[(sizeInBytes + 7) / 8];
        }

        public Span<byte> Span => MemoryMarshal.AsBytes(_storage.AsSpan())[.._length];
    }
}
