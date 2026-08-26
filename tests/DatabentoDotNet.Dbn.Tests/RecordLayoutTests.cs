using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DatabentoDotNet.Dbn.Enums;
using Action = DatabentoDotNet.Dbn.Enums.Action;

namespace DatabentoDotNet.Dbn.Tests;

/// <summary>
/// Guards the DBN wire layout.
/// </summary>
/// <remarks>
/// The expected sizes are the ones <c>databento-cpp</c> pins with <c>static_assert</c> against
/// the Rust reference implementation. Because records are reinterpreted directly over the read
/// buffer, a layout mistake is silent data corruption rather than an exception — these
/// assertions are what turn it back into a build failure. See <see cref="RecordLayout"/> for the
/// three claims each <c>MatchesWireLayout</c> test makes.
/// </remarks>
public class RecordLayoutTests
{
    [Fact]
    public void RecordHeader_MatchesWireSize()
    {
        Assert.Equal(16, Unsafe.SizeOf<RecordHeader>());
    }

    [Fact]
    public void MaxRecordLength_CoversLargestRecordPlusTsOut()
    {
        // InstrumentDefMsg (520) + ts_out (8). The read buffer is sized off this.
        Assert.Equal(528, DbnConstants.MaxRecordLength);
    }

    [Fact]
    public void RecordHeader_LengthIsExpressedIn32BitWords()
    {
        // A 56-byte MboMsg is encoded as length=14.
        var header = CreateHeader(length: 14);
        Assert.Equal(56, header.SizeInBytes);
    }

    // ---------------------------------------------------------------------------------------
    // Size, alignment, and no-interior-padding, one test per struct.
    // The expected size is the databento-cpp static_assert value, cited per test.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void RecordHeader_MatchesWireLayout() => RecordLayout.AssertLayout<RecordHeader>(16);

    [Fact]
    public void MboMsg_MatchesWireLayout() => RecordLayout.AssertLayout<MboMsg>(56);

    [Fact]
    public void BidAskPair_MatchesWireLayout() => RecordLayout.AssertLayout<BidAskPair>(32);

    [Fact]
    public void ConsolidatedBidAskPair_MatchesWireLayout()
        => RecordLayout.AssertLayout<ConsolidatedBidAskPair>(32);

    [Fact]
    public void TradeMsg_MatchesWireLayout() => RecordLayout.AssertLayout<TradeMsg>(48);

    [Fact]
    public void Mbp1Msg_MatchesWireLayout() => RecordLayout.AssertLayout<Mbp1Msg>(80);

    [Fact]
    public void Mbp10Msg_MatchesWireLayout() => RecordLayout.AssertLayout<Mbp10Msg>(368);

    [Fact]
    public void BboMsg_MatchesWireLayout() => RecordLayout.AssertLayout<BboMsg>(80);

    [Fact]
    public void Cmbp1Msg_MatchesWireLayout() => RecordLayout.AssertLayout<Cmbp1Msg>(80);

    [Fact]
    public void CbboMsg_MatchesWireLayout() => RecordLayout.AssertLayout<CbboMsg>(80);

    [Fact]
    public void OhlcvMsg_MatchesWireLayout() => RecordLayout.AssertLayout<OhlcvMsg>(56);

    [Fact]
    public void StatusMsg_MatchesWireLayout() => RecordLayout.AssertLayout<StatusMsg>(40);

    [Fact]
    public void InlineArrayLevels_SizeTheBufferNotTheElement()
    {
        // The failure mode this guards: an [InlineArray] that reports its element's size would
        // still satisfy every other assertion in this file while decoding nine levels of
        // garbage.
        Assert.Equal(32, Unsafe.SizeOf<BidAskPairArray1>());
        Assert.Equal(320, Unsafe.SizeOf<BidAskPairArray10>());
        Assert.Equal(32, Unsafe.SizeOf<ConsolidatedBidAskPairArray1>());
    }

    // ---------------------------------------------------------------------------------------
    // Every field named at its byte offset, in declaration order, transcribed from the
    // #[repr(C)] Rust structs. This is what catches a transposition of two equal-size
    // neighbours, which every size-based assertion above would pass.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void RecordHeader_DeclaresEveryFieldAtItsWireOffset()
        => RecordLayout.AssertFieldOffsets<RecordHeader>(
            16,
            (nameof(RecordHeader.Length), 0),
            (nameof(RecordHeader.RType), 1),
            (nameof(RecordHeader.PublisherId), 2),
            (nameof(RecordHeader.InstrumentId), 4),
            (nameof(RecordHeader.TsEvent), 8));

    [Fact]
    public void MboMsg_DeclaresEveryFieldAtItsWireOffset()
        => RecordLayout.AssertFieldOffsets<MboMsg>(
            56,
            (nameof(MboMsg.Header), 0),
            (nameof(MboMsg.OrderId), 16),
            (nameof(MboMsg.Price), 24),
            (nameof(MboMsg.Size), 32),
            (nameof(MboMsg.Flags), 36),
            (nameof(MboMsg.ChannelId), 37),
            (nameof(MboMsg.RawAction), 38),
            (nameof(MboMsg.RawSide), 39),
            (nameof(MboMsg.TsRecv), 40),
            (nameof(MboMsg.TsInDelta), 48),
            (nameof(MboMsg.Sequence), 52));

    [Fact]
    public void BidAskPair_DeclaresEveryFieldAtItsWireOffset()
        => RecordLayout.AssertFieldOffsets<BidAskPair>(
            32,
            (nameof(BidAskPair.BidPx), 0),
            (nameof(BidAskPair.AskPx), 8),
            (nameof(BidAskPair.BidSz), 16),
            (nameof(BidAskPair.AskSz), 20),
            (nameof(BidAskPair.BidCt), 24),
            (nameof(BidAskPair.AskCt), 28));

    [Fact]
    public void ConsolidatedBidAskPair_DeclaresEveryFieldAtItsWireOffset()
        => RecordLayout.AssertFieldOffsets<ConsolidatedBidAskPair>(
            32,
            (nameof(ConsolidatedBidAskPair.BidPx), 0),
            (nameof(ConsolidatedBidAskPair.AskPx), 8),
            (nameof(ConsolidatedBidAskPair.BidSz), 16),
            (nameof(ConsolidatedBidAskPair.AskSz), 20),
            (nameof(ConsolidatedBidAskPair.BidPb), 24),
            ("_reserved1", 26),
            (nameof(ConsolidatedBidAskPair.AskPb), 28),
            ("_reserved2", 30));

    [Fact]
    public void TradeMsg_DeclaresEveryFieldAtItsWireOffset()
        => RecordLayout.AssertFieldOffsets<TradeMsg>(
            48,
            (nameof(TradeMsg.Header), 0),
            (nameof(TradeMsg.Price), 16),
            (nameof(TradeMsg.Size), 24),
            (nameof(TradeMsg.RawAction), 28),
            (nameof(TradeMsg.RawSide), 29),
            (nameof(TradeMsg.Flags), 30),
            (nameof(TradeMsg.Depth), 31),
            (nameof(TradeMsg.TsRecv), 32),
            (nameof(TradeMsg.TsInDelta), 40),
            (nameof(TradeMsg.Sequence), 44));

    [Fact]
    public void Mbp1Msg_DeclaresEveryFieldAtItsWireOffset()
        => RecordLayout.AssertFieldOffsets<Mbp1Msg>(
            80,
            (nameof(Mbp1Msg.Header), 0),
            (nameof(Mbp1Msg.Price), 16),
            (nameof(Mbp1Msg.Size), 24),
            (nameof(Mbp1Msg.RawAction), 28),
            (nameof(Mbp1Msg.RawSide), 29),
            (nameof(Mbp1Msg.Flags), 30),
            (nameof(Mbp1Msg.Depth), 31),
            (nameof(Mbp1Msg.TsRecv), 32),
            (nameof(Mbp1Msg.TsInDelta), 40),
            (nameof(Mbp1Msg.Sequence), 44),
            (nameof(Mbp1Msg.Levels), 48));

    [Fact]
    public void Mbp10Msg_DeclaresEveryFieldAtItsWireOffset()
        => RecordLayout.AssertFieldOffsets<Mbp10Msg>(
            368,
            (nameof(Mbp10Msg.Header), 0),
            (nameof(Mbp10Msg.Price), 16),
            (nameof(Mbp10Msg.Size), 24),
            (nameof(Mbp10Msg.RawAction), 28),
            (nameof(Mbp10Msg.RawSide), 29),
            (nameof(Mbp10Msg.Flags), 30),
            (nameof(Mbp10Msg.Depth), 31),
            (nameof(Mbp10Msg.TsRecv), 32),
            (nameof(Mbp10Msg.TsInDelta), 40),
            (nameof(Mbp10Msg.Sequence), 44),
            (nameof(Mbp10Msg.Levels), 48));

    [Fact]
    public void BboMsg_DeclaresEveryFieldAtItsWireOffset()
        => RecordLayout.AssertFieldOffsets<BboMsg>(
            80,
            (nameof(BboMsg.Header), 0),
            (nameof(BboMsg.Price), 16),
            (nameof(BboMsg.Size), 24),
            ("_reserved1", 28),
            (nameof(BboMsg.RawSide), 29),
            (nameof(BboMsg.Flags), 30),
            ("_reserved2", 31),
            (nameof(BboMsg.TsRecv), 32),
            ("_reserved3", 40),
            (nameof(BboMsg.Sequence), 44),
            (nameof(BboMsg.Levels), 48));

    [Fact]
    public void Cmbp1Msg_DeclaresEveryFieldAtItsWireOffset()
        => RecordLayout.AssertFieldOffsets<Cmbp1Msg>(
            80,
            (nameof(Cmbp1Msg.Header), 0),
            (nameof(Cmbp1Msg.Price), 16),
            (nameof(Cmbp1Msg.Size), 24),
            (nameof(Cmbp1Msg.RawAction), 28),
            (nameof(Cmbp1Msg.RawSide), 29),
            (nameof(Cmbp1Msg.Flags), 30),
            ("_reserved1", 31),
            (nameof(Cmbp1Msg.TsRecv), 32),
            (nameof(Cmbp1Msg.TsInDelta), 40),
            ("_reserved2", 44),
            (nameof(Cmbp1Msg.Levels), 48));

    [Fact]
    public void CbboMsg_DeclaresEveryFieldAtItsWireOffset()
        => RecordLayout.AssertFieldOffsets<CbboMsg>(
            80,
            (nameof(CbboMsg.Header), 0),
            (nameof(CbboMsg.Price), 16),
            (nameof(CbboMsg.Size), 24),
            ("_reserved1", 28),
            (nameof(CbboMsg.RawSide), 29),
            (nameof(CbboMsg.Flags), 30),
            ("_reserved2", 31),
            (nameof(CbboMsg.TsRecv), 32),
            ("_reserved3", 40),
            (nameof(CbboMsg.Levels), 48));

    [Fact]
    public void OhlcvMsg_DeclaresEveryFieldAtItsWireOffset()
        => RecordLayout.AssertFieldOffsets<OhlcvMsg>(
            56,
            (nameof(OhlcvMsg.Header), 0),
            (nameof(OhlcvMsg.Open), 16),
            (nameof(OhlcvMsg.High), 24),
            (nameof(OhlcvMsg.Low), 32),
            (nameof(OhlcvMsg.Close), 40),
            (nameof(OhlcvMsg.Volume), 48));

    [Fact]
    public void StatusMsg_DeclaresEveryFieldAtItsWireOffset()
        => RecordLayout.AssertFieldOffsets<StatusMsg>(
            40,
            (nameof(StatusMsg.Header), 0),
            (nameof(StatusMsg.TsRecv), 16),
            (nameof(StatusMsg.RawAction), 24),
            (nameof(StatusMsg.RawReason), 26),
            (nameof(StatusMsg.RawTradingEvent), 28),
            (nameof(StatusMsg.RawIsTrading), 30),
            (nameof(StatusMsg.RawIsQuoting), 31),
            (nameof(StatusMsg.RawIsShortSellRestricted), 32),
            ("_reserved", 33));

    // ---------------------------------------------------------------------------------------
    // IRecord<TSelf>: rtype membership and the exact wire size the decoder matches on.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void WireSize_MatchesRuntimeSize_ForEveryRecord()
    {
        AssertWireSize<MboMsg>(56);
        AssertWireSize<TradeMsg>(48);
        AssertWireSize<Mbp1Msg>(80);
        AssertWireSize<Mbp10Msg>(368);
        AssertWireSize<BboMsg>(80);
        AssertWireSize<Cmbp1Msg>(80);
        AssertWireSize<CbboMsg>(80);
        AssertWireSize<OhlcvMsg>(56);
        AssertWireSize<StatusMsg>(40);
    }

    [Fact]
    public void HasRType_AcceptsExactlyItsOwnRecordTypes()
    {
        AssertHasRType<MboMsg>(RType.Mbo);
        AssertHasRType<TradeMsg>(RType.Mbp0);
        AssertHasRType<Mbp1Msg>(RType.Mbp1);
        AssertHasRType<Mbp10Msg>(RType.Mbp10);
        AssertHasRType<BboMsg>(RType.Bbo1S, RType.Bbo1M);
        AssertHasRType<Cmbp1Msg>(RType.Cmbp1, RType.Tcbbo);
        AssertHasRType<CbboMsg>(RType.Cbbo1S, RType.Cbbo1M);
        AssertHasRType<OhlcvMsg>(
            RType.Ohlcv1S,
            RType.Ohlcv1M,
            RType.Ohlcv1H,
            RType.Ohlcv1D,
            RType.OhlcvEod,
            RType.OhlcvDeprecated);
        AssertHasRType<StatusMsg>(RType.Status);
    }

    // ---------------------------------------------------------------------------------------
    // Byte-level round trips: write a known pattern, read every field back at its wire offset.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void MboMsg_ReadsEveryFieldAtItsWireOffset()
    {
        var bytes = new byte[56];
        var span = bytes.AsSpan();

        bytes[0] = 14;                                                          // hd.length  @0
        bytes[1] = (byte)RType.Mbo;                                             // hd.rtype   @1
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 0x1234);            // publisher  @2
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 0xDEADBEEF);        // instrument @4
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], 0x0102030405060708);// ts_event   @8
        BinaryPrimitives.WriteUInt64LittleEndian(span[16..], 0x1122334455667788);// order_id  @16
        BinaryPrimitives.WriteInt64LittleEndian(span[24..], -9_876_543_210);    // price      @24
        BinaryPrimitives.WriteUInt32LittleEndian(span[32..], 4242);             // size       @32
        bytes[36] = (byte)(FlagSet.Last | FlagSet.Snapshot);                    // flags      @36
        bytes[37] = 7;                                                          // channel_id @37
        bytes[38] = (byte)'A';                                                  // action     @38
        bytes[39] = (byte)'B';                                                  // side       @39
        BinaryPrimitives.WriteUInt64LittleEndian(span[40..], 1_700_000_000_123_456_789);
        BinaryPrimitives.WriteInt32LittleEndian(span[48..], -500);              // ts_in_delta@48
        BinaryPrimitives.WriteUInt32LittleEndian(span[52..], 123_456_789);      // sequence   @52

        ref readonly var msg = ref MemoryMarshal.AsRef<MboMsg>((ReadOnlySpan<byte>)bytes);

        Assert.Equal(14, msg.Header.Length);
        Assert.Equal(56, msg.Header.SizeInBytes);
        Assert.Equal((byte)RType.Mbo, msg.Header.RType);
        Assert.Equal(0x1234, msg.Header.PublisherId);
        Assert.Equal(0xDEADBEEF, msg.Header.InstrumentId);
        Assert.Equal(0x0102030405060708UL, msg.Header.TsEvent);
        Assert.Equal(0x1122334455667788UL, msg.OrderId);
        Assert.Equal(-9_876_543_210, msg.Price);
        Assert.Equal(4242u, msg.Size);
        Assert.Equal(FlagSet.Last | FlagSet.Snapshot, msg.Flags);
        Assert.Equal(7, msg.ChannelId);
        Assert.Equal((byte)'A', msg.RawAction);
        Assert.Equal('A', msg.ActionChar);
        Assert.Equal(Action.Add, msg.Action);
        Assert.Equal((byte)'B', msg.RawSide);
        Assert.Equal('B', msg.SideChar);
        Assert.Equal(Side.Bid, msg.Side);
        Assert.Equal(1_700_000_000_123_456_789UL, msg.TsRecv);
        Assert.Equal(-500, msg.TsInDelta);
        Assert.Equal(123_456_789u, msg.Sequence);
    }

    [Fact]
    public void Mbp10Msg_ReadsEveryLevelAtItsWireOffset()
    {
        var bytes = new byte[368];
        var span = bytes.AsSpan();

        bytes[0] = 92;                                                          // hd.length  @0
        bytes[1] = (byte)RType.Mbp10;                                           // hd.rtype   @1
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 42);                // publisher  @2
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 99);                // instrument @4
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], 11);                // ts_event   @8
        BinaryPrimitives.WriteInt64LittleEndian(span[16..], 12_345_000_000_000);// price      @16
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], 500);              // size       @24
        bytes[28] = (byte)'M';                                                  // action     @28
        bytes[29] = (byte)'A';                                                  // side       @29
        bytes[30] = (byte)FlagSet.Mbp;                                          // flags      @30
        bytes[31] = 3;                                                          // depth      @31
        BinaryPrimitives.WriteUInt64LittleEndian(span[32..], 22);               // ts_recv    @32
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], -7);                // ts_in_delta@40
        BinaryPrimitives.WriteUInt32LittleEndian(span[44..], 8);                // sequence   @44

        // levels[i] starts at 48 + i*32; within a level: bid_px @0, ask_px @8, bid_sz @16,
        // ask_sz @20, bid_ct @24, ask_ct @28.
        for (var i = 0; i < 10; i++)
        {
            var level = span[(48 + (i * 32))..];
            BinaryPrimitives.WriteInt64LittleEndian(level, 1_000 + i);
            BinaryPrimitives.WriteInt64LittleEndian(level[8..], 2_000 + i);
            BinaryPrimitives.WriteUInt32LittleEndian(level[16..], (uint)(10 + i));
            BinaryPrimitives.WriteUInt32LittleEndian(level[20..], (uint)(20 + i));
            BinaryPrimitives.WriteUInt32LittleEndian(level[24..], (uint)(30 + i));
            BinaryPrimitives.WriteUInt32LittleEndian(level[28..], (uint)(40 + i));
        }

        ref readonly var msg = ref MemoryMarshal.AsRef<Mbp10Msg>((ReadOnlySpan<byte>)bytes);

        Assert.Equal(368, msg.Header.SizeInBytes);
        Assert.Equal((byte)RType.Mbp10, msg.Header.RType);
        Assert.Equal(42, msg.Header.PublisherId);
        Assert.Equal(99u, msg.Header.InstrumentId);
        Assert.Equal(11UL, msg.Header.TsEvent);
        Assert.Equal(12_345_000_000_000, msg.Price);
        Assert.Equal(500u, msg.Size);
        Assert.Equal(Action.Modify, msg.Action);
        Assert.Equal(Side.Ask, msg.Side);
        Assert.Equal(FlagSet.Mbp, msg.Flags);
        Assert.Equal(3, msg.Depth);
        Assert.Equal(22UL, msg.TsRecv);
        Assert.Equal(-7, msg.TsInDelta);
        Assert.Equal(8u, msg.Sequence);

        for (var i = 0; i < 10; i++)
        {
            var level = msg.Levels[i];
            Assert.Equal(1_000 + i, level.BidPx);
            Assert.Equal(2_000 + i, level.AskPx);
            Assert.Equal((uint)(10 + i), level.BidSz);
            Assert.Equal((uint)(20 + i), level.AskSz);
            Assert.Equal((uint)(30 + i), level.BidCt);
            Assert.Equal((uint)(40 + i), level.AskCt);
        }
    }

    [Fact]
    public void ConsolidatedBidAskPair_ReadsPublisherIdsWhereBidAskPairHasCounts()
    {
        var bytes = new byte[32];
        var span = bytes.AsSpan();

        BinaryPrimitives.WriteInt64LittleEndian(span, 111);                     // bid_px  @0
        BinaryPrimitives.WriteInt64LittleEndian(span[8..], 222);                // ask_px  @8
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 333);              // bid_sz  @16
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], 444);              // ask_sz  @20
        BinaryPrimitives.WriteUInt16LittleEndian(span[24..], 555);              // bid_pb  @24
        BinaryPrimitives.WriteUInt16LittleEndian(span[26..], 0xFFFF);           // reserved@26
        BinaryPrimitives.WriteUInt16LittleEndian(span[28..], 666);              // ask_pb  @28
        BinaryPrimitives.WriteUInt16LittleEndian(span[30..], 0xFFFF);           // reserved@30

        ref readonly var pair =
            ref MemoryMarshal.AsRef<ConsolidatedBidAskPair>((ReadOnlySpan<byte>)bytes);

        Assert.Equal(111, pair.BidPx);
        Assert.Equal(222, pair.AskPx);
        Assert.Equal(333u, pair.BidSz);
        Assert.Equal(444u, pair.AskSz);
        Assert.Equal(555, pair.BidPb);
        Assert.Equal(666, pair.AskPb);
    }

    [Fact]
    public void BboMsg_ReadsEveryFieldAtItsWireOffsetAcrossThreeReservedBlocks()
    {
        var bytes = new byte[80];
        var span = bytes.AsSpan();
        span.Fill(0xEE);                                     // reserved bytes stay noise

        bytes[0] = 20;
        bytes[1] = (byte)RType.Bbo1S;
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 2);
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], 3);
        BinaryPrimitives.WriteInt64LittleEndian(span[16..], 4_500_000_000);     // price   @16
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], 25);               // size    @24
        bytes[29] = (byte)'A';                                                  // side    @29
        bytes[30] = (byte)FlagSet.Last;                                         // flags   @30
        BinaryPrimitives.WriteUInt64LittleEndian(span[32..], 6);                // ts_recv @32
        BinaryPrimitives.WriteUInt32LittleEndian(span[44..], 77);               // sequence@44
        BinaryPrimitives.WriteInt64LittleEndian(span[48..], 8);                 // bid_px  @48
        BinaryPrimitives.WriteInt64LittleEndian(span[56..], 9);                 // ask_px  @56
        BinaryPrimitives.WriteUInt32LittleEndian(span[64..], 10);               // bid_sz  @64
        BinaryPrimitives.WriteUInt32LittleEndian(span[68..], 11);               // ask_sz  @68
        BinaryPrimitives.WriteUInt32LittleEndian(span[72..], 12);               // bid_ct  @72
        BinaryPrimitives.WriteUInt32LittleEndian(span[76..], 13);               // ask_ct  @76

        ref readonly var msg = ref MemoryMarshal.AsRef<BboMsg>((ReadOnlySpan<byte>)bytes);

        Assert.Equal(4_500_000_000, msg.Price);
        Assert.Equal(25u, msg.Size);
        Assert.Equal(Side.Ask, msg.Side);
        Assert.Equal(FlagSet.Last, msg.Flags);
        Assert.Equal(6UL, msg.TsRecv);
        Assert.Equal(77u, msg.Sequence);
        Assert.Equal(8, msg.Levels[0].BidPx);
        Assert.Equal(9, msg.Levels[0].AskPx);
        Assert.Equal(10u, msg.Levels[0].BidSz);
        Assert.Equal(11u, msg.Levels[0].AskSz);
        Assert.Equal(12u, msg.Levels[0].BidCt);
        Assert.Equal(13u, msg.Levels[0].AskCt);
    }

    [Fact]
    public void Cmbp1Msg_ReadsEveryFieldAtItsWireOffset()
    {
        var bytes = new byte[80];
        var span = bytes.AsSpan();
        span.Fill(0xEE);

        bytes[0] = 20;
        bytes[1] = (byte)RType.Tcbbo;
        BinaryPrimitives.WriteInt64LittleEndian(span[16..], 1_500);             // price      @16
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], 60);               // size       @24
        bytes[28] = (byte)'T';                                                  // action     @28
        bytes[29] = (byte)'B';                                                  // side       @29
        bytes[30] = (byte)FlagSet.Tob;                                          // flags      @30
        BinaryPrimitives.WriteUInt64LittleEndian(span[32..], 70);               // ts_recv    @32
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], -80);               // ts_in_delta@40
        BinaryPrimitives.WriteInt64LittleEndian(span[48..], 90);                // bid_px     @48
        BinaryPrimitives.WriteInt64LittleEndian(span[56..], 100);               // ask_px     @56
        BinaryPrimitives.WriteUInt32LittleEndian(span[64..], 110);              // bid_sz     @64
        BinaryPrimitives.WriteUInt32LittleEndian(span[68..], 120);              // ask_sz     @68
        BinaryPrimitives.WriteUInt16LittleEndian(span[72..], 130);              // bid_pb     @72
        BinaryPrimitives.WriteUInt16LittleEndian(span[76..], 140);              // ask_pb     @76

        ref readonly var msg = ref MemoryMarshal.AsRef<Cmbp1Msg>((ReadOnlySpan<byte>)bytes);

        Assert.Equal(1_500, msg.Price);
        Assert.Equal(60u, msg.Size);
        Assert.Equal(Action.Trade, msg.Action);
        Assert.Equal(Side.Bid, msg.Side);
        Assert.Equal(FlagSet.Tob, msg.Flags);
        Assert.Equal(70UL, msg.TsRecv);
        Assert.Equal(-80, msg.TsInDelta);
        Assert.Equal(90, msg.Levels[0].BidPx);
        Assert.Equal(100, msg.Levels[0].AskPx);
        Assert.Equal(110u, msg.Levels[0].BidSz);
        Assert.Equal(120u, msg.Levels[0].AskSz);
        Assert.Equal(130, msg.Levels[0].BidPb);
        Assert.Equal(140, msg.Levels[0].AskPb);
    }

    [Fact]
    public void BboMsg_PutsEveryFieldWhereMbp1MsgPutsIt()
    {
        // Upstream pins this with test_bbo_alignment_matches_mbp1: BboMsg's reserved blocks
        // exist so the two records stay interchangeable byte for byte.
        Assert.Equal(RecordLayout.OffsetOf<Mbp1Msg>("Header"), RecordLayout.OffsetOf<BboMsg>("Header"));
        Assert.Equal(RecordLayout.OffsetOf<Mbp1Msg>("Price"), RecordLayout.OffsetOf<BboMsg>("Price"));
        Assert.Equal(RecordLayout.OffsetOf<Mbp1Msg>("Size"), RecordLayout.OffsetOf<BboMsg>("Size"));
        Assert.Equal(RecordLayout.OffsetOf<Mbp1Msg>("RawSide"), RecordLayout.OffsetOf<BboMsg>("RawSide"));
        Assert.Equal(RecordLayout.OffsetOf<Mbp1Msg>("Flags"), RecordLayout.OffsetOf<BboMsg>("Flags"));
        Assert.Equal(RecordLayout.OffsetOf<Mbp1Msg>("TsRecv"), RecordLayout.OffsetOf<BboMsg>("TsRecv"));
        Assert.Equal(RecordLayout.OffsetOf<Mbp1Msg>("Sequence"), RecordLayout.OffsetOf<BboMsg>("Sequence"));
        Assert.Equal(RecordLayout.OffsetOf<Mbp1Msg>("Levels"), RecordLayout.OffsetOf<BboMsg>("Levels"));

        // And the absolute offsets, so "both wrong the same way" cannot pass.
        Assert.Equal(16, RecordLayout.OffsetOf<BboMsg>("Price"));
        Assert.Equal(24, RecordLayout.OffsetOf<BboMsg>("Size"));
        Assert.Equal(29, RecordLayout.OffsetOf<BboMsg>("RawSide"));
        Assert.Equal(30, RecordLayout.OffsetOf<BboMsg>("Flags"));
        Assert.Equal(32, RecordLayout.OffsetOf<BboMsg>("TsRecv"));
        Assert.Equal(44, RecordLayout.OffsetOf<BboMsg>("Sequence"));
        Assert.Equal(48, RecordLayout.OffsetOf<BboMsg>("Levels"));
    }

    [Fact]
    public void CbboMsg_HasNoSequenceFieldAndReachesLevelsAtTheSameOffset()
    {
        // CbboMsg spends BboMsg's ts_in_delta + sequence budget on one 8-byte reserved block.
        Assert.Equal(32, RecordLayout.OffsetOf<CbboMsg>("TsRecv"));
        Assert.Equal(48, RecordLayout.OffsetOf<CbboMsg>("Levels"));
        Assert.Equal(48, RecordLayout.OffsetOf<Cmbp1Msg>("Levels"));
        Assert.Equal(40, RecordLayout.OffsetOf<Cmbp1Msg>("TsInDelta"));
    }

    [Fact]
    public void OhlcvMsg_ReadsEveryFieldAtItsWireOffset()
    {
        var bytes = new byte[56];
        var span = bytes.AsSpan();

        bytes[0] = 14;
        bytes[1] = (byte)RType.Ohlcv1D;
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], 5);
        BinaryPrimitives.WriteInt64LittleEndian(span[16..], 100);               // open   @16
        BinaryPrimitives.WriteInt64LittleEndian(span[24..], 400);               // high   @24
        BinaryPrimitives.WriteInt64LittleEndian(span[32..], 50);                // low    @32
        BinaryPrimitives.WriteInt64LittleEndian(span[40..], 300);               // close  @40
        BinaryPrimitives.WriteUInt64LittleEndian(span[48..], 987_654);          // volume @48

        ref readonly var msg = ref MemoryMarshal.AsRef<OhlcvMsg>((ReadOnlySpan<byte>)bytes);

        Assert.Equal(100, msg.Open);
        Assert.Equal(400, msg.High);
        Assert.Equal(50, msg.Low);
        Assert.Equal(300, msg.Close);
        Assert.Equal(987_654UL, msg.Volume);
        Assert.Equal(5UL, msg.Header.TsEvent);
    }

    [Fact]
    public void StatusMsg_ReadsEveryFieldAtItsWireOffset()
    {
        var bytes = new byte[40];
        var span = bytes.AsSpan();

        bytes[0] = 10;
        bytes[1] = (byte)RType.Status;
        BinaryPrimitives.WriteUInt64LittleEndian(span[16..], 777);              // ts_recv       @16
        BinaryPrimitives.WriteUInt16LittleEndian(span[24..], (ushort)StatusAction.Trading);
        BinaryPrimitives.WriteUInt16LittleEndian(span[26..], (ushort)StatusReason.Scheduled);
        BinaryPrimitives.WriteUInt16LittleEndian(span[28..], (ushort)TradingEvent.NoCancel);
        bytes[30] = (byte)'Y';                                                  // is_trading    @30
        bytes[31] = (byte)'N';                                                  // is_quoting    @31
        bytes[32] = (byte)'~';                                                  // is_ssr        @32

        ref readonly var msg = ref MemoryMarshal.AsRef<StatusMsg>((ReadOnlySpan<byte>)bytes);

        Assert.Equal(777UL, msg.TsRecv);
        Assert.Equal(StatusAction.Trading, msg.Action);
        Assert.Equal(StatusReason.Scheduled, msg.Reason);
        Assert.Equal(TradingEvent.NoCancel, msg.TradingEvent);
        Assert.Equal(TriState.Yes, msg.IsTrading);
        Assert.Equal('Y', msg.IsTradingChar);
        Assert.Equal(TriState.No, msg.IsQuoting);
        Assert.Equal(TriState.NotAvailable, msg.IsShortSellRestricted);
    }

    private static void AssertWireSize<T>(int expected)
        where T : unmanaged, IRecord<T>
    {
        Assert.Equal(expected, T.WireSize);
        Assert.Equal(Unsafe.SizeOf<T>(), T.WireSize);
    }

    private static void AssertHasRType<T>(params RType[] accepted)
        where T : unmanaged, IRecord<T>
    {
        foreach (var rtype in Enum.GetValues<RType>())
        {
            Assert.Equal(accepted.Contains(rtype), T.HasRType(rtype));
        }
    }

    private static RecordHeader CreateHeader(byte length)
    {
        Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<RecordHeader>()];
        bytes.Clear();
        bytes[0] = length;
        return MemoryMarshal.Read<RecordHeader>(bytes);
    }
}
