using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn.Tests;

/// <summary>
/// Byte-level tests for <see cref="RecordRef"/>'s downcast rule.
/// </summary>
/// <remarks>
/// <para>
/// Records are hand-built here rather than pulled from a fixture, on purpose: this is the rule
/// that decides <em>which version</em> of a record the bytes are, and a fixture can only ever
/// exercise the combinations that happen to be vendored. Building the header by hand lets each
/// case state exactly the rtype and length it means to test — including the ones no fixture
/// contains, like a v3-sized definition being offered to the v1 struct.
/// </para>
/// <para>
/// Storage comes from a <c>ulong[]</c> rather than a <c>byte[]</c> so that the bytes start
/// 8-byte aligned, which is what <see cref="RecordRef"/> requires of any buffer it reinterprets —
/// the same guarantee <see cref="AlignedBuffer"/> gives the decoder.
/// </para>
/// </remarks>
public class RecordRefTests
{
    [Fact]
    public void Has_MatchesOnRTypeAndExactStructSize()
    {
        var v1 = BuildRecord(RType.InstrumentDef, InstrumentDefMsgV1.WireSize);
        var v2 = BuildRecord(RType.InstrumentDef, InstrumentDefMsgV2.WireSize);
        var v3 = BuildRecord(RType.InstrumentDef, InstrumentDefMsg.WireSize);

        // All three share an rtype. Only the length tells them apart, which is exactly why the
        // size comparison is exact: with `>=`, the 520-byte v3 record would answer true for all
        // three and decode as whichever the caller asked for first.
        Assert.True(new RecordRef(v1.Span).Has<InstrumentDefMsgV1>());
        Assert.False(new RecordRef(v1.Span).Has<InstrumentDefMsgV2>());
        Assert.False(new RecordRef(v1.Span).Has<InstrumentDefMsg>());

        Assert.False(new RecordRef(v2.Span).Has<InstrumentDefMsgV1>());
        Assert.True(new RecordRef(v2.Span).Has<InstrumentDefMsgV2>());
        Assert.False(new RecordRef(v2.Span).Has<InstrumentDefMsg>());

        Assert.False(new RecordRef(v3.Span).Has<InstrumentDefMsgV1>());
        Assert.False(new RecordRef(v3.Span).Has<InstrumentDefMsgV2>());
        Assert.True(new RecordRef(v3.Span).Has<InstrumentDefMsg>());
    }

    [Fact]
    public void Has_DiscriminatesEveryVersionedRecordFamily()
    {
        AssertOnlyMatches<StatMsgV1, StatMsg>(RType.Statistics);
        AssertOnlyMatches<ErrorMsgV1, ErrorMsg>(RType.Error);
        AssertOnlyMatches<SymbolMappingMsgV1, SymbolMappingMsg>(RType.SymbolMapping);
        AssertOnlyMatches<SystemMsgV1, SystemMsg>(RType.System);
    }

    [Fact]
    public void Has_WrongRType_IsFalseEvenAtTheRightSize()
    {
        // Mbp1Msg, BboMsg, Cmbp1Msg and CbboMsg are all 80 bytes, so for those four the rtype is
        // the only thing that distinguishes them — the mirror image of the definition family.
        var bbo = BuildRecord(RType.Bbo1S, BboMsg.WireSize);
        var reference = new RecordRef(bbo.Span);

        Assert.True(reference.Has<BboMsg>());
        Assert.False(reference.Has<Mbp1Msg>());
        Assert.False(reference.Has<CbboMsg>());
        Assert.False(reference.Has<Cmbp1Msg>());
    }

    [Fact]
    public void Get_MatchingType_ReadsTheHeaderInPlace()
    {
        var bytes = BuildRecord(RType.Mbp0, TradeMsg.WireSize);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.Span[2..], 42);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.Span[4..], 7_777);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.Span[8..], 1_678_284_110_000_000_000);

        var reference = new RecordRef(bytes.Span);
        ref readonly var trade = ref reference.Get<TradeMsg>();

        Assert.Equal(42, trade.Header.PublisherId);
        Assert.Equal(7_777u, trade.Header.InstrumentId);
        Assert.Equal(1_678_284_110_000_000_000ul, trade.Header.TsEvent);
        Assert.Equal(TradeMsg.WireSize, trade.Header.SizeInBytes);
    }

    [Fact]
    public void Get_MismatchedType_Throws()
    {
        var bytes = BuildRecord(RType.InstrumentDef, InstrumentDefMsg.WireSize);

        Assert.Throws<DbnDecodeException>(() =>
        {
            var reference = new RecordRef(bytes.Span);
            _ = reference.Get<InstrumentDefMsgV1>();
        });

        Assert.Throws<DbnDecodeException>(() =>
        {
            var reference = new RecordRef(bytes.Span);
            _ = reference.Get<TradeMsg>();
        });
    }

    [Fact]
    public void TryGet_MismatchedType_ReturnsFalseWithoutThrowing()
    {
        var bytes = BuildRecord(RType.InstrumentDef, InstrumentDefMsgV1.WireSize);
        var reference = new RecordRef(bytes.Span);

        Assert.False(reference.TryGet<InstrumentDefMsg>(out var wrong));
        Assert.Equal<byte>(0, wrong.Header.Length);

        Assert.True(reference.TryGet<InstrumentDefMsgV1>(out var right));
        Assert.Equal(InstrumentDefMsgV1.WireSize, right.Header.SizeInBytes);
    }

    [Fact]
    public void StructSize_ExcludesTsOutWhileSizeInBytesIncludesIt()
    {
        const ulong SendTimestamp = 1_678_486_110_123_456_789;

        var bytes = BuildRecord(RType.Mbp0, TradeMsg.WireSize + sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.Span[TradeMsg.WireSize..], SendTimestamp);

        var withTsOut = new RecordRef(bytes.Span, hasTsOut: true);
        Assert.Equal(TradeMsg.WireSize + sizeof(ulong), withTsOut.SizeInBytes);
        Assert.Equal(TradeMsg.WireSize, withTsOut.StructSize);
        Assert.True(withTsOut.HasTsOut);
        Assert.Equal(SendTimestamp, withTsOut.TsOut);
        Assert.True(withTsOut.Has<TradeMsg>());

        // The same bytes on a stream without ts_out are simply not a TradeMsg: 56 bytes is not
        // 48, and nothing in the record itself says otherwise.
        var withoutTsOut = new RecordRef(bytes.Span);
        Assert.Equal(TradeMsg.WireSize + sizeof(ulong), withoutTsOut.StructSize);
        Assert.False(withoutTsOut.HasTsOut);
        Assert.False(withoutTsOut.Has<TradeMsg>());

        Assert.Throws<InvalidOperationException>(() =>
        {
            var reference = new RecordRef(bytes.Span);
            _ = reference.TsOut;
        });
    }

    [Fact]
    public void Constructor_ShorterThanAHeader_Throws()
    {
        var bytes = BuildRecord(RType.Mbp0, TradeMsg.WireSize);

        // Fifteen bytes cannot hold a sixteen-byte header, whatever the header claims.
        Assert.Throws<DbnDecodeException>(() =>
        {
            var reference = new RecordRef(bytes.Span[..15]);
            _ = reference.SizeInBytes;
        });
    }

    [Fact]
    public void Constructor_DeclaredLengthShorterThanTheHeader_Throws()
    {
        var bytes = new AlignedBytes(64);
        bytes.Span[0] = 1; // Four bytes, where a header alone is sixteen.

        Assert.Throws<DbnDecodeException>(() =>
        {
            var reference = new RecordRef(bytes.Span);
            _ = reference.SizeInBytes;
        });
    }

    [Fact]
    public void Constructor_BufferShorterThanTheDeclaredLength_Throws()
    {
        var bytes = new AlignedBytes(32);
        bytes.Span[0] = 20; // Eighty bytes declared, thirty-two available.

        Assert.Throws<DbnDecodeException>(() =>
        {
            var reference = new RecordRef(bytes.Span);
            _ = reference.SizeInBytes;
        });
    }

    [Fact]
    public void Constructor_TrailingBytesPastTheRecord_AreNotPartOfIt()
    {
        // The decoder always hands over the whole remaining buffer, so a RecordRef has to trim
        // itself to the length its own header declares rather than to what it was given.
        var bytes = new AlignedBytes(TradeMsg.WireSize * 3);
        bytes.Span[0] = (byte)(TradeMsg.WireSize / DbnConstants.RecordLengthMultiplier);
        bytes.Span[1] = (byte)RType.Mbp0;

        var reference = new RecordRef(bytes.Span);

        Assert.Equal(TradeMsg.WireSize, reference.SizeInBytes);
        Assert.Equal(TradeMsg.WireSize, reference.Bytes.Length);
    }

    private static void AssertOnlyMatches<TOld, TNew>(RType rtype)
        where TOld : unmanaged, IRecord<TOld>
        where TNew : unmanaged, IRecord<TNew>
    {
        Assert.NotEqual(TOld.WireSize, TNew.WireSize);

        var oldBytes = BuildRecord(rtype, TOld.WireSize);
        Assert.True(new RecordRef(oldBytes.Span).Has<TOld>());
        Assert.False(new RecordRef(oldBytes.Span).Has<TNew>());

        var newBytes = BuildRecord(rtype, TNew.WireSize);
        Assert.False(new RecordRef(newBytes.Span).Has<TOld>());
        Assert.True(new RecordRef(newBytes.Span).Has<TNew>());
    }

    private static AlignedBytes BuildRecord(RType rtype, int sizeInBytes)
    {
        var bytes = new AlignedBytes(sizeInBytes);
        bytes.Span[0] = checked((byte)(sizeInBytes / DbnConstants.RecordLengthMultiplier));
        bytes.Span[1] = (byte)rtype;
        return bytes;
    }

    /// <summary>
    /// Zeroed storage whose first byte is 8-byte aligned, which is what reinterpreting bytes as a
    /// record requires. A <c>byte[]</c> carries no such guarantee, and a <c>Span&lt;byte&gt;</c>
    /// cannot be captured by the lambdas the throwing cases need — so the array is held and the
    /// span taken freshly at each use.
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
