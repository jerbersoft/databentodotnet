using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
        // InstrumentDefMsg (520) + ts_out (8). The read buffer is sized off this. Backed by the
        // real struct rather than a literal 528, so growing the largest record without growing
        // the constant fails here instead of overflowing a buffer at run time.
        Assert.Equal(528, DbnConstants.MaxRecordLength);
        Assert.Equal(DbnConstants.MaxRecordLength, Unsafe.SizeOf<WithTsOut<InstrumentDefMsg>>());
        Assert.Equal(520, InstrumentDefMsg.WireSize);

        // And nothing else is bigger.
        Assert.Equal(
            InstrumentDefMsg.WireSize,
            new[]
            {
                MboMsg.WireSize, TradeMsg.WireSize, Mbp1Msg.WireSize, Mbp10Msg.WireSize,
                BboMsg.WireSize, Cmbp1Msg.WireSize, CbboMsg.WireSize, OhlcvMsg.WireSize,
                StatusMsg.WireSize, InstrumentDefMsg.WireSize, ImbalanceMsg.WireSize,
                StatMsg.WireSize, ErrorMsg.WireSize, SymbolMappingMsg.WireSize,
                SystemMsg.WireSize, InstrumentDefMsgV1.WireSize, InstrumentDefMsgV2.WireSize,
                StatMsgV1.WireSize, ErrorMsgV1.WireSize, SymbolMappingMsgV1.WireSize,
                SystemMsgV1.WireSize,
            }.Max());
    }

    [Fact]
    public void RecordHeader_RType_IsTheTypedViewOfEveryPossibleRawByte()
    {
        // RecordHeader used to expose the wire byte under the name RType, which left no typed
        // path at all: RecordRef.Has<T> cast privately and RTypeSchemaMapping.TryIntoSchema took
        // a byte while its counterpart ToRType took the enum. The field is now RawRType and RType
        // is the typed view, matching every other record's RawX / X pair.
        //
        // The cast is total by design — an undefined byte becomes an unnamed RType rather than
        // throwing, exactly as RawAction/Action do — because a reinterpret over the read buffer
        // cannot fail and validation belongs to EnumValues.TryFromRType.
        Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<RecordHeader>()];
        for (var raw = 0; raw <= byte.MaxValue; raw++)
        {
            bytes.Clear();
            bytes[1] = (byte)raw;

            var header = MemoryMarshal.Read<RecordHeader>(bytes);

            Assert.Equal((byte)raw, header.RawRType);
            Assert.Equal((RType)raw, header.RType);
        }
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
    public void InstrumentDefMsg_MatchesWireLayout()
        => RecordLayout.AssertLayout<InstrumentDefMsg>(520);

    [Fact]
    public void ImbalanceMsg_MatchesWireLayout() => RecordLayout.AssertLayout<ImbalanceMsg>(112);

    [Fact]
    public void StatMsg_MatchesWireLayout() => RecordLayout.AssertLayout<StatMsg>(80);

    [Fact]
    public void ErrorMsg_MatchesWireLayout() => RecordLayout.AssertLayout<ErrorMsg>(320);

    [Fact]
    public void SymbolMappingMsg_MatchesWireLayout()
        => RecordLayout.AssertLayout<SymbolMappingMsg>(176);

    [Fact]
    public void SystemMsg_MatchesWireLayout() => RecordLayout.AssertLayout<SystemMsg>(320);

    // The version-specific layouts. Their sizes come from databento-cpp's v1.hpp and v2.hpp
    // static_asserts, which are independent of record.hpp's.

    [Fact]
    public void InstrumentDefMsgV1_MatchesWireLayout()
        => RecordLayout.AssertLayout<InstrumentDefMsgV1>(360);

    [Fact]
    public void InstrumentDefMsgV2_MatchesWireLayout()
        => RecordLayout.AssertLayout<InstrumentDefMsgV2>(400);

    [Fact]
    public void StatMsgV1_MatchesWireLayout() => RecordLayout.AssertLayout<StatMsgV1>(64);

    [Fact]
    public void ErrorMsgV1_MatchesWireLayout() => RecordLayout.AssertLayout<ErrorMsgV1>(80);

    [Fact]
    public void SymbolMappingMsgV1_MatchesWireLayout()
        => RecordLayout.AssertLayout<SymbolMappingMsgV1>(80);

    [Fact]
    public void SystemMsgV1_MatchesWireLayout() => RecordLayout.AssertLayout<SystemMsgV1>(80);

    [Fact]
    public void NoTwoVersionsOfTheSameRecordShareASize()
    {
        // The decoder identifies a versioned record by rtype AND exact size, so two versions of
        // one rtype sharing a size would make that rule ambiguous. StatMsg v1 and v2 are the same
        // struct, so the families are checked pairwise, not as sets.
        Assert.NotEqual(InstrumentDefMsgV1.WireSize, InstrumentDefMsgV2.WireSize);
        Assert.NotEqual(InstrumentDefMsgV2.WireSize, InstrumentDefMsg.WireSize);
        Assert.NotEqual(InstrumentDefMsgV1.WireSize, InstrumentDefMsg.WireSize);
        Assert.NotEqual(StatMsgV1.WireSize, StatMsg.WireSize);
        Assert.NotEqual(ErrorMsgV1.WireSize, ErrorMsg.WireSize);
        Assert.NotEqual(SymbolMappingMsgV1.WireSize, SymbolMappingMsg.WireSize);
        Assert.NotEqual(SystemMsgV1.WireSize, SystemMsg.WireSize);
    }

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
            (nameof(RecordHeader.RawRType), 1),
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

    [Fact]
    public void InstrumentDefMsg_DeclaresEveryFieldAtItsWireOffset()
        => RecordLayout.AssertFieldOffsets<InstrumentDefMsg>(
            520,
            (nameof(InstrumentDefMsg.Header), 0),
            (nameof(InstrumentDefMsg.TsRecv), 16),
            (nameof(InstrumentDefMsg.MinPriceIncrement), 24),
            (nameof(InstrumentDefMsg.DisplayFactor), 32),
            (nameof(InstrumentDefMsg.Expiration), 40),
            (nameof(InstrumentDefMsg.Activation), 48),
            (nameof(InstrumentDefMsg.HighLimitPrice), 56),
            (nameof(InstrumentDefMsg.LowLimitPrice), 64),
            (nameof(InstrumentDefMsg.MaxPriceVariation), 72),
            (nameof(InstrumentDefMsg.UnitOfMeasureQty), 80),
            (nameof(InstrumentDefMsg.MinPriceIncrementAmount), 88),
            (nameof(InstrumentDefMsg.PriceRatio), 96),
            (nameof(InstrumentDefMsg.StrikePrice), 104),
            (nameof(InstrumentDefMsg.RawInstrumentId), 112),
            (nameof(InstrumentDefMsg.LegPrice), 120),
            (nameof(InstrumentDefMsg.LegDelta), 128),
            (nameof(InstrumentDefMsg.InstAttribValue), 136),
            (nameof(InstrumentDefMsg.UnderlyingId), 140),
            (nameof(InstrumentDefMsg.MarketDepthImplied), 144),
            (nameof(InstrumentDefMsg.MarketDepth), 148),
            (nameof(InstrumentDefMsg.MarketSegmentId), 152),
            (nameof(InstrumentDefMsg.MaxTradeVol), 156),
            (nameof(InstrumentDefMsg.MinLotSize), 160),
            (nameof(InstrumentDefMsg.MinLotSizeBlock), 164),
            (nameof(InstrumentDefMsg.MinLotSizeRoundLot), 168),
            (nameof(InstrumentDefMsg.MinTradeVol), 172),
            (nameof(InstrumentDefMsg.ContractMultiplier), 176),
            (nameof(InstrumentDefMsg.DecayQuantity), 180),
            (nameof(InstrumentDefMsg.OriginalContractSize), 184),
            (nameof(InstrumentDefMsg.LegInstrumentId), 188),
            (nameof(InstrumentDefMsg.LegRatioPriceNumerator), 192),
            (nameof(InstrumentDefMsg.LegRatioPriceDenominator), 196),
            (nameof(InstrumentDefMsg.LegRatioQtyNumerator), 200),
            (nameof(InstrumentDefMsg.LegRatioQtyDenominator), 204),
            (nameof(InstrumentDefMsg.LegUnderlyingId), 208),
            (nameof(InstrumentDefMsg.ApplId), 212),
            (nameof(InstrumentDefMsg.MaturityYear), 214),
            (nameof(InstrumentDefMsg.DecayStartDate), 216),
            (nameof(InstrumentDefMsg.ChannelId), 218),
            (nameof(InstrumentDefMsg.LegCount), 220),
            (nameof(InstrumentDefMsg.LegIndex), 222),
            (nameof(InstrumentDefMsg.Currency), 224),
            (nameof(InstrumentDefMsg.SettlCurrency), 228),
            (nameof(InstrumentDefMsg.SecSubType), 232),
            (nameof(InstrumentDefMsg.RawSymbol), 238),
            (nameof(InstrumentDefMsg.Group), 309),
            (nameof(InstrumentDefMsg.Exchange), 330),
            (nameof(InstrumentDefMsg.Asset), 335),
            (nameof(InstrumentDefMsg.Cfi), 346),
            (nameof(InstrumentDefMsg.SecurityType), 353),
            (nameof(InstrumentDefMsg.UnitOfMeasure), 360),
            (nameof(InstrumentDefMsg.Underlying), 391),
            (nameof(InstrumentDefMsg.StrikePriceCurrency), 412),
            (nameof(InstrumentDefMsg.LegRawSymbol), 416),
            (nameof(InstrumentDefMsg.RawInstrumentClass), 487),
            (nameof(InstrumentDefMsg.RawMatchAlgorithm), 488),
            (nameof(InstrumentDefMsg.MainFraction), 489),
            (nameof(InstrumentDefMsg.PriceDisplayFormat), 490),
            (nameof(InstrumentDefMsg.SubFraction), 491),
            (nameof(InstrumentDefMsg.UnderlyingProduct), 492),
            (nameof(InstrumentDefMsg.RawSecurityUpdateAction), 493),
            (nameof(InstrumentDefMsg.MaturityMonth), 494),
            (nameof(InstrumentDefMsg.MaturityDay), 495),
            (nameof(InstrumentDefMsg.MaturityWeek), 496),
            (nameof(InstrumentDefMsg.RawUserDefinedInstrument), 497),
            (nameof(InstrumentDefMsg.ContractMultiplierUnit), 498),
            (nameof(InstrumentDefMsg.FlowScheduleType), 499),
            (nameof(InstrumentDefMsg.TickRule), 500),
            (nameof(InstrumentDefMsg.RawLegInstrumentClass), 501),
            (nameof(InstrumentDefMsg.RawLegSide), 502),
            ("_reserved", 503));

    [Fact]
    public void ImbalanceMsg_DeclaresEveryFieldAtItsWireOffset()
        => RecordLayout.AssertFieldOffsets<ImbalanceMsg>(
            112,
            (nameof(ImbalanceMsg.Header), 0),
            (nameof(ImbalanceMsg.TsRecv), 16),
            (nameof(ImbalanceMsg.RefPrice), 24),
            (nameof(ImbalanceMsg.AuctionTime), 32),
            (nameof(ImbalanceMsg.ContBookClrPrice), 40),
            (nameof(ImbalanceMsg.AuctInterestClrPrice), 48),
            (nameof(ImbalanceMsg.SsrFillingPrice), 56),
            (nameof(ImbalanceMsg.IndMatchPrice), 64),
            (nameof(ImbalanceMsg.UpperCollar), 72),
            (nameof(ImbalanceMsg.LowerCollar), 80),
            (nameof(ImbalanceMsg.PairedQty), 88),
            (nameof(ImbalanceMsg.TotalImbalanceQty), 92),
            (nameof(ImbalanceMsg.MarketImbalanceQty), 96),
            (nameof(ImbalanceMsg.UnpairedQty), 100),
            (nameof(ImbalanceMsg.RawAuctionType), 104),
            (nameof(ImbalanceMsg.RawSide), 105),
            (nameof(ImbalanceMsg.AuctionStatus), 106),
            (nameof(ImbalanceMsg.FreezeStatus), 107),
            (nameof(ImbalanceMsg.NumExtensions), 108),
            (nameof(ImbalanceMsg.RawUnpairedSide), 109),
            (nameof(ImbalanceMsg.RawSignificantImbalance), 110),
            ("_reserved", 111));

    [Fact]
    public void StatMsg_DeclaresEveryFieldAtItsWireOffset()
        => RecordLayout.AssertFieldOffsets<StatMsg>(
            80,
            (nameof(StatMsg.Header), 0),
            (nameof(StatMsg.TsRecv), 16),
            (nameof(StatMsg.TsRef), 24),
            (nameof(StatMsg.Price), 32),
            (nameof(StatMsg.Quantity), 40),
            (nameof(StatMsg.Sequence), 48),
            (nameof(StatMsg.TsInDelta), 52),
            (nameof(StatMsg.RawStatType), 56),
            (nameof(StatMsg.ChannelId), 58),
            (nameof(StatMsg.RawUpdateAction), 60),
            (nameof(StatMsg.StatFlags), 61),
            ("_reserved", 62));

    [Fact]
    public void ErrorMsg_DeclaresEveryFieldAtItsWireOffset()
        => RecordLayout.AssertFieldOffsets<ErrorMsg>(
            320,
            (nameof(ErrorMsg.Header), 0),
            (nameof(ErrorMsg.Err), 16),
            (nameof(ErrorMsg.RawCode), 318),
            (nameof(ErrorMsg.IsLast), 319));

    [Fact]
    public void SymbolMappingMsg_DeclaresEveryFieldAtItsWireOffset()
        => RecordLayout.AssertFieldOffsets<SymbolMappingMsg>(
            176,
            (nameof(SymbolMappingMsg.Header), 0),
            (nameof(SymbolMappingMsg.RawStypeIn), 16),
            (nameof(SymbolMappingMsg.StypeInSymbol), 17),
            (nameof(SymbolMappingMsg.RawStypeOut), 88),
            (nameof(SymbolMappingMsg.StypeOutSymbol), 89),
            (nameof(SymbolMappingMsg.StartTs), 160),
            (nameof(SymbolMappingMsg.EndTs), 168));

    [Fact]
    public void SystemMsg_DeclaresEveryFieldAtItsWireOffset()
        => RecordLayout.AssertFieldOffsets<SystemMsg>(
            320,
            (nameof(SystemMsg.Header), 0),
            (nameof(SystemMsg.Msg), 16),
            (nameof(SystemMsg.RawCode), 319));

    [Fact]
    public void InstrumentDefMsgV1_DeclaresEveryFieldAtItsWireOffset()
        => RecordLayout.AssertFieldOffsets<InstrumentDefMsgV1>(
            360,
            (nameof(InstrumentDefMsgV1.Header), 0),
            (nameof(InstrumentDefMsgV1.TsRecv), 16),
            (nameof(InstrumentDefMsgV1.MinPriceIncrement), 24),
            (nameof(InstrumentDefMsgV1.DisplayFactor), 32),
            (nameof(InstrumentDefMsgV1.Expiration), 40),
            (nameof(InstrumentDefMsgV1.Activation), 48),
            (nameof(InstrumentDefMsgV1.HighLimitPrice), 56),
            (nameof(InstrumentDefMsgV1.LowLimitPrice), 64),
            (nameof(InstrumentDefMsgV1.MaxPriceVariation), 72),
            (nameof(InstrumentDefMsgV1.TradingReferencePrice), 80),
            (nameof(InstrumentDefMsgV1.UnitOfMeasureQty), 88),
            (nameof(InstrumentDefMsgV1.MinPriceIncrementAmount), 96),
            (nameof(InstrumentDefMsgV1.PriceRatio), 104),
            (nameof(InstrumentDefMsgV1.InstAttribValue), 112),
            (nameof(InstrumentDefMsgV1.UnderlyingId), 116),
            (nameof(InstrumentDefMsgV1.RawInstrumentId), 120),
            (nameof(InstrumentDefMsgV1.MarketDepthImplied), 124),
            (nameof(InstrumentDefMsgV1.MarketDepth), 128),
            (nameof(InstrumentDefMsgV1.MarketSegmentId), 132),
            (nameof(InstrumentDefMsgV1.MaxTradeVol), 136),
            (nameof(InstrumentDefMsgV1.MinLotSize), 140),
            (nameof(InstrumentDefMsgV1.MinLotSizeBlock), 144),
            (nameof(InstrumentDefMsgV1.MinLotSizeRoundLot), 148),
            (nameof(InstrumentDefMsgV1.MinTradeVol), 152),
            ("_reserved2", 156),
            (nameof(InstrumentDefMsgV1.ContractMultiplier), 160),
            (nameof(InstrumentDefMsgV1.DecayQuantity), 164),
            (nameof(InstrumentDefMsgV1.OriginalContractSize), 168),
            ("_reserved3", 172),
            (nameof(InstrumentDefMsgV1.TradingReferenceDate), 176),
            (nameof(InstrumentDefMsgV1.ApplId), 178),
            (nameof(InstrumentDefMsgV1.MaturityYear), 180),
            (nameof(InstrumentDefMsgV1.DecayStartDate), 182),
            (nameof(InstrumentDefMsgV1.ChannelId), 184),
            (nameof(InstrumentDefMsgV1.Currency), 186),
            (nameof(InstrumentDefMsgV1.SettlCurrency), 190),
            (nameof(InstrumentDefMsgV1.SecSubType), 194),
            (nameof(InstrumentDefMsgV1.RawSymbol), 200),
            (nameof(InstrumentDefMsgV1.Group), 222),
            (nameof(InstrumentDefMsgV1.Exchange), 243),
            (nameof(InstrumentDefMsgV1.Asset), 248),
            (nameof(InstrumentDefMsgV1.Cfi), 255),
            (nameof(InstrumentDefMsgV1.SecurityType), 262),
            (nameof(InstrumentDefMsgV1.UnitOfMeasure), 269),
            (nameof(InstrumentDefMsgV1.Underlying), 300),
            (nameof(InstrumentDefMsgV1.StrikePriceCurrency), 321),
            (nameof(InstrumentDefMsgV1.RawInstrumentClass), 325),
            ("_reserved4", 326),
            (nameof(InstrumentDefMsgV1.StrikePrice), 328),
            ("_reserved5", 336),
            (nameof(InstrumentDefMsgV1.RawMatchAlgorithm), 342),
            (nameof(InstrumentDefMsgV1.MdSecurityTradingStatus), 343),
            (nameof(InstrumentDefMsgV1.MainFraction), 344),
            (nameof(InstrumentDefMsgV1.PriceDisplayFormat), 345),
            (nameof(InstrumentDefMsgV1.SettlPriceType), 346),
            (nameof(InstrumentDefMsgV1.SubFraction), 347),
            (nameof(InstrumentDefMsgV1.UnderlyingProduct), 348),
            (nameof(InstrumentDefMsgV1.RawSecurityUpdateAction), 349),
            (nameof(InstrumentDefMsgV1.MaturityMonth), 350),
            (nameof(InstrumentDefMsgV1.MaturityDay), 351),
            (nameof(InstrumentDefMsgV1.MaturityWeek), 352),
            (nameof(InstrumentDefMsgV1.RawUserDefinedInstrument), 353),
            (nameof(InstrumentDefMsgV1.ContractMultiplierUnit), 354),
            (nameof(InstrumentDefMsgV1.FlowScheduleType), 355),
            (nameof(InstrumentDefMsgV1.TickRule), 356),
            ("_dummy", 357));

    [Fact]
    public void InstrumentDefMsgV2_DeclaresEveryFieldAtItsWireOffset()
        => RecordLayout.AssertFieldOffsets<InstrumentDefMsgV2>(
            400,
            (nameof(InstrumentDefMsgV2.Header), 0),
            (nameof(InstrumentDefMsgV2.TsRecv), 16),
            (nameof(InstrumentDefMsgV2.MinPriceIncrement), 24),
            (nameof(InstrumentDefMsgV2.DisplayFactor), 32),
            (nameof(InstrumentDefMsgV2.Expiration), 40),
            (nameof(InstrumentDefMsgV2.Activation), 48),
            (nameof(InstrumentDefMsgV2.HighLimitPrice), 56),
            (nameof(InstrumentDefMsgV2.LowLimitPrice), 64),
            (nameof(InstrumentDefMsgV2.MaxPriceVariation), 72),
            (nameof(InstrumentDefMsgV2.TradingReferencePrice), 80),
            (nameof(InstrumentDefMsgV2.UnitOfMeasureQty), 88),
            (nameof(InstrumentDefMsgV2.MinPriceIncrementAmount), 96),
            (nameof(InstrumentDefMsgV2.PriceRatio), 104),
            (nameof(InstrumentDefMsgV2.StrikePrice), 112),
            (nameof(InstrumentDefMsgV2.InstAttribValue), 120),
            (nameof(InstrumentDefMsgV2.UnderlyingId), 124),
            (nameof(InstrumentDefMsgV2.RawInstrumentId), 128),
            (nameof(InstrumentDefMsgV2.MarketDepthImplied), 132),
            (nameof(InstrumentDefMsgV2.MarketDepth), 136),
            (nameof(InstrumentDefMsgV2.MarketSegmentId), 140),
            (nameof(InstrumentDefMsgV2.MaxTradeVol), 144),
            (nameof(InstrumentDefMsgV2.MinLotSize), 148),
            (nameof(InstrumentDefMsgV2.MinLotSizeBlock), 152),
            (nameof(InstrumentDefMsgV2.MinLotSizeRoundLot), 156),
            (nameof(InstrumentDefMsgV2.MinTradeVol), 160),
            (nameof(InstrumentDefMsgV2.ContractMultiplier), 164),
            (nameof(InstrumentDefMsgV2.DecayQuantity), 168),
            (nameof(InstrumentDefMsgV2.OriginalContractSize), 172),
            (nameof(InstrumentDefMsgV2.TradingReferenceDate), 176),
            (nameof(InstrumentDefMsgV2.ApplId), 178),
            (nameof(InstrumentDefMsgV2.MaturityYear), 180),
            (nameof(InstrumentDefMsgV2.DecayStartDate), 182),
            (nameof(InstrumentDefMsgV2.ChannelId), 184),
            (nameof(InstrumentDefMsgV2.Currency), 186),
            (nameof(InstrumentDefMsgV2.SettlCurrency), 190),
            (nameof(InstrumentDefMsgV2.SecSubType), 194),
            (nameof(InstrumentDefMsgV2.RawSymbol), 200),
            (nameof(InstrumentDefMsgV2.Group), 271),
            (nameof(InstrumentDefMsgV2.Exchange), 292),
            (nameof(InstrumentDefMsgV2.Asset), 297),
            (nameof(InstrumentDefMsgV2.Cfi), 304),
            (nameof(InstrumentDefMsgV2.SecurityType), 311),
            (nameof(InstrumentDefMsgV2.UnitOfMeasure), 318),
            (nameof(InstrumentDefMsgV2.Underlying), 349),
            (nameof(InstrumentDefMsgV2.StrikePriceCurrency), 370),
            (nameof(InstrumentDefMsgV2.RawInstrumentClass), 374),
            (nameof(InstrumentDefMsgV2.RawMatchAlgorithm), 375),
            (nameof(InstrumentDefMsgV2.MdSecurityTradingStatus), 376),
            (nameof(InstrumentDefMsgV2.MainFraction), 377),
            (nameof(InstrumentDefMsgV2.PriceDisplayFormat), 378),
            (nameof(InstrumentDefMsgV2.SettlPriceType), 379),
            (nameof(InstrumentDefMsgV2.SubFraction), 380),
            (nameof(InstrumentDefMsgV2.UnderlyingProduct), 381),
            (nameof(InstrumentDefMsgV2.RawSecurityUpdateAction), 382),
            (nameof(InstrumentDefMsgV2.MaturityMonth), 383),
            (nameof(InstrumentDefMsgV2.MaturityDay), 384),
            (nameof(InstrumentDefMsgV2.MaturityWeek), 385),
            (nameof(InstrumentDefMsgV2.RawUserDefinedInstrument), 386),
            (nameof(InstrumentDefMsgV2.ContractMultiplierUnit), 387),
            (nameof(InstrumentDefMsgV2.FlowScheduleType), 388),
            (nameof(InstrumentDefMsgV2.TickRule), 389),
            ("_reserved", 390));

    [Fact]
    public void StatMsgV1_DeclaresEveryFieldAtItsWireOffset()
        => RecordLayout.AssertFieldOffsets<StatMsgV1>(
            64,
            (nameof(StatMsgV1.Header), 0),
            (nameof(StatMsgV1.TsRecv), 16),
            (nameof(StatMsgV1.TsRef), 24),
            (nameof(StatMsgV1.Price), 32),
            (nameof(StatMsgV1.Quantity), 40),
            (nameof(StatMsgV1.Sequence), 44),
            (nameof(StatMsgV1.TsInDelta), 48),
            (nameof(StatMsgV1.RawStatType), 52),
            (nameof(StatMsgV1.ChannelId), 54),
            (nameof(StatMsgV1.RawUpdateAction), 56),
            (nameof(StatMsgV1.StatFlags), 57),
            ("_reserved", 58));

    [Fact]
    public void ErrorMsgV1_DeclaresEveryFieldAtItsWireOffset()
        => RecordLayout.AssertFieldOffsets<ErrorMsgV1>(
            80,
            (nameof(ErrorMsgV1.Header), 0),
            (nameof(ErrorMsgV1.Err), 16));

    [Fact]
    public void SymbolMappingMsgV1_DeclaresEveryFieldAtItsWireOffset()
        => RecordLayout.AssertFieldOffsets<SymbolMappingMsgV1>(
            80,
            (nameof(SymbolMappingMsgV1.Header), 0),
            (nameof(SymbolMappingMsgV1.StypeInSymbol), 16),
            (nameof(SymbolMappingMsgV1.StypeOutSymbol), 38),
            ("_dummy", 60),
            (nameof(SymbolMappingMsgV1.StartTs), 64),
            (nameof(SymbolMappingMsgV1.EndTs), 72));

    [Fact]
    public void SystemMsgV1_DeclaresEveryFieldAtItsWireOffset()
        => RecordLayout.AssertFieldOffsets<SystemMsgV1>(
            80,
            (nameof(SystemMsgV1.Header), 0),
            (nameof(SystemMsgV1.Msg), 16));

    // ---------------------------------------------------------------------------------------
    // IRecord<TSelf>: rtype membership and the exact wire size the decoder matches on.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void WireSize_MatchesRuntimeSize_ForEveryRecord()
    {
        VisitEveryListedRecord(covered: null);
    }

    [Fact]
    public void EveryRecordStructInTheAssembly_IsInTheWireSizeList()
    {
        // The guard on the guard. WireSize_MatchesRuntimeSize_ForEveryRecord is a hand-written
        // list of 21 calls, and every assertion folded into AssertWireSize — the size, the
        // alignment, the header-first rule — reaches a record struct only through that list. A
        // record added to the library and not to the list therefore escapes all of them
        // silently: the suite stays green because nothing ever asks about the new type.
        //
        // So ask the assembly instead of the list. Implementing IRecord<TSelf> is what makes a
        // struct a record — RecordRef.Has<T>/Get<T> are constrained on it, so nothing can be
        // decoded without it — which makes "implements IRecord<>" the definition of the set the
        // list is supposed to enumerate, not a proxy for it.
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

        // Named, not just "collections differ at index 20": the whole point is that whoever
        // trips this is someone who has just added a record struct and does not yet know this
        // list exists, so the failure has to say which type and what it costs them.
        var missing = implementers.Except(listed, StringComparer.Ordinal).ToList();
        var extra = listed.Except(implementers, StringComparer.Ordinal).ToList();
        Assert.True(
            missing.Count == 0 && extra.Count == 0,
            $"VisitEveryListedRecord no longer enumerates every IRecord<> implementer.{Environment.NewLine}" +
            $"  Implements IRecord<> but is not in the list, so its size, alignment and " +
            $"header-first offset go unchecked: [{string.Join(", ", missing)}]{Environment.NewLine}" +
            $"  In the list but no longer implements IRecord<>: [{string.Join(", ", extra)}]");

        Assert.Equal(implementers, listed);
    }

    /// <summary>
    /// The one hand-maintained record table: every record struct paired with the size
    /// <c>databento-cpp</c> pins for it with a <c>static_assert</c>. Both
    /// <see cref="WireSize_MatchesRuntimeSize_ForEveryRecord"/> and
    /// <see cref="EveryRecordStructInTheAssembly_IsInTheWireSizeList"/> run it, so the set the
    /// coverage test checks is by construction the same set the size test asserts — there is no
    /// second list to fall out of step with this one.
    /// </summary>
    /// <param name="covered">
    /// Collects each visited record type, for the coverage test. <see langword="null"/> to just
    /// run the assertions.
    /// </param>
    private static void VisitEveryListedRecord(List<Type>? covered)
    {
        AssertWireSize<MboMsg>(56, covered);
        AssertWireSize<TradeMsg>(48, covered);
        AssertWireSize<Mbp1Msg>(80, covered);
        AssertWireSize<Mbp10Msg>(368, covered);
        AssertWireSize<BboMsg>(80, covered);
        AssertWireSize<Cmbp1Msg>(80, covered);
        AssertWireSize<CbboMsg>(80, covered);
        AssertWireSize<OhlcvMsg>(56, covered);
        AssertWireSize<StatusMsg>(40, covered);
        AssertWireSize<InstrumentDefMsg>(520, covered);
        AssertWireSize<ImbalanceMsg>(112, covered);
        AssertWireSize<StatMsg>(80, covered);
        AssertWireSize<ErrorMsg>(320, covered);
        AssertWireSize<SymbolMappingMsg>(176, covered);
        AssertWireSize<SystemMsg>(320, covered);
        AssertWireSize<InstrumentDefMsgV1>(360, covered);
        AssertWireSize<InstrumentDefMsgV2>(400, covered);
        AssertWireSize<StatMsgV1>(64, covered);
        AssertWireSize<ErrorMsgV1>(80, covered);
        AssertWireSize<SymbolMappingMsgV1>(80, covered);
        AssertWireSize<SystemMsgV1>(80, covered);
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
        AssertHasRType<InstrumentDefMsg>(RType.InstrumentDef);
        AssertHasRType<ImbalanceMsg>(RType.Imbalance);
        AssertHasRType<StatMsg>(RType.Statistics);
        AssertHasRType<ErrorMsg>(RType.Error);
        AssertHasRType<SymbolMappingMsg>(RType.SymbolMapping);
        AssertHasRType<SystemMsg>(RType.System);

        // A version-specific struct claims the same rtype as its current-version counterpart.
        // That is exactly why rtype alone cannot identify a record and the wire size has to be
        // part of the match rule -- see the remarks on IRecord<TSelf>.
        AssertHasRType<InstrumentDefMsgV1>(RType.InstrumentDef);
        AssertHasRType<InstrumentDefMsgV2>(RType.InstrumentDef);
        AssertHasRType<StatMsgV1>(RType.Statistics);
        AssertHasRType<ErrorMsgV1>(RType.Error);
        AssertHasRType<SymbolMappingMsgV1>(RType.SymbolMapping);
        AssertHasRType<SystemMsgV1>(RType.System);
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
        Assert.Equal(RType.Mbo, msg.Header.RType);
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
        Assert.Equal(RType.Mbp10, msg.Header.RType);
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

    [Fact]
    public void InstrumentDefMsg_ReadsEveryFieldAtItsWireOffset()
    {
        // Offsets here are written out by hand from the Rust #[repr(C)] declaration order, not
        // taken from the struct, so this is an independent check of the CLR's managed layout —
        // in particular that each [c_char; N] buffer occupies exactly N bytes with nothing
        // inserted around it. The whole record is filled with noise first so that a field read
        // from the wrong place comes back as 0xEE rather than as a plausible zero.
        var bytes = new byte[520];
        var span = bytes.AsSpan();
        span.Fill(0xEE);

        bytes[0] = 130;                                                     // hd.length      @0
        bytes[1] = (byte)RType.InstrumentDef;                               // hd.rtype       @1
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 1);             // publisher_id   @2
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 2);             // instrument_id  @4
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], 3);             // ts_event       @8
        BinaryPrimitives.WriteUInt64LittleEndian(span[16..], 4);            // ts_recv        @16
        BinaryPrimitives.WriteInt64LittleEndian(span[24..], 5);             // min_px_incr    @24
        BinaryPrimitives.WriteInt64LittleEndian(span[104..], 106);          // strike_price   @104
        BinaryPrimitives.WriteUInt64LittleEndian(span[112..], 0x1_0000_0007);// raw_inst_id   @112
        BinaryPrimitives.WriteInt64LittleEndian(span[120..], 108);          // leg_price      @120
        BinaryPrimitives.WriteInt64LittleEndian(span[128..], 109);          // leg_delta      @128
        // inst_attrib_value through leg_underlying_id: nineteen consecutive 4-byte fields,
        // offsets 136-208, that no two-or-three-field spot check can tell apart from a
        // transposition of two adjacent ones. Every slot gets its own offset as its sentinel
        // value, so each is distinct and self-describing: a swap between any two of these
        // fields reads back the wrong offset and fails.
        BinaryPrimitives.WriteInt32LittleEndian(span[136..], 136);          // inst_attrib    @136
        BinaryPrimitives.WriteUInt32LittleEndian(span[140..], 140);         // underlying_id  @140
        BinaryPrimitives.WriteInt32LittleEndian(span[144..], 144);          // mkt_depth_impl @144
        BinaryPrimitives.WriteInt32LittleEndian(span[148..], 148);          // market_depth   @148
        BinaryPrimitives.WriteUInt32LittleEndian(span[152..], 152);         // mkt_segment_id @152
        BinaryPrimitives.WriteUInt32LittleEndian(span[156..], 156);         // max_trade_vol  @156
        BinaryPrimitives.WriteInt32LittleEndian(span[160..], 160);          // min_lot_size   @160
        BinaryPrimitives.WriteInt32LittleEndian(span[164..], 164);          // min_lot_sz_blk @164
        BinaryPrimitives.WriteInt32LittleEndian(span[168..], 168);          // min_lot_sz_rl  @168
        BinaryPrimitives.WriteUInt32LittleEndian(span[172..], 172);         // min_trade_vol  @172
        BinaryPrimitives.WriteInt32LittleEndian(span[176..], 176);          // contract_mult  @176
        BinaryPrimitives.WriteInt32LittleEndian(span[180..], 180);          // decay_qty      @180
        BinaryPrimitives.WriteInt32LittleEndian(span[184..], 184);          // orig_ctr_size  @184
        BinaryPrimitives.WriteUInt32LittleEndian(span[188..], 188);         // leg_inst_id    @188
        BinaryPrimitives.WriteInt32LittleEndian(span[192..], 192);          // leg_ratio_p_n  @192
        BinaryPrimitives.WriteInt32LittleEndian(span[196..], 196);          // leg_ratio_p_d  @196
        BinaryPrimitives.WriteInt32LittleEndian(span[200..], 200);          // leg_ratio_q_n  @200
        BinaryPrimitives.WriteInt32LittleEndian(span[204..], 204);          // leg_ratio_q_d  @204
        BinaryPrimitives.WriteUInt32LittleEndian(span[208..], 208);         // leg_underlying @208
        BinaryPrimitives.WriteInt16LittleEndian(span[212..], -113);         // appl_id        @212
        BinaryPrimitives.WriteUInt16LittleEndian(span[220..], 114);         // leg_count      @220
        BinaryPrimitives.WriteUInt16LittleEndian(span[222..], 115);         // leg_index      @222
        "USD\0"u8.CopyTo(span[224..]);                                      // currency       @224
        "GBP\0"u8.CopyTo(span[228..]);                                      // settl_currency @228
        "ABCDE\0"u8.CopyTo(span[232..]);                                    // secsubtype     @232
        "ESH4\0"u8.CopyTo(span[238..]);                                     // raw_symbol     @238
        "ES\0"u8.CopyTo(span[309..]);                                       // group          @309
        "XCME\0"u8.CopyTo(span[330..]);                                     // exchange       @330
        "ES\0"u8.CopyTo(span[335..]);                                       // asset          @335
        "FFICSX\0"u8.CopyTo(span[346..]);                                   // cfi            @346
        "FUT\0"u8.CopyTo(span[353..]);                                      // security_type  @353
        "IPNT\0"u8.CopyTo(span[360..]);                                     // unit_of_measure@360
        "SPX\0"u8.CopyTo(span[391..]);                                      // underlying     @391
        "USD\0"u8.CopyTo(span[412..]);                                      // strike_px_ccy  @412
        "ESM4-ESU4\0"u8.CopyTo(span[416..]);                                // leg_raw_symbol @416
        bytes[487] = (byte)'F';                                             // instrument_cls @487
        bytes[488] = (byte)'F';                                             // match_algorithm@488
        bytes[489] = 1;                                                     // main_fraction  @489
        bytes[490] = 2;                                                     // px_disp_format @490
        bytes[491] = 3;                                                     // sub_fraction   @491
        bytes[492] = 4;                                                     // underlying_prod@492
        bytes[493] = (byte)'A';                                             // sec_upd_action @493
        bytes[494] = 6;                                                     // maturity_month @494
        bytes[495] = 7;                                                     // maturity_day   @495
        bytes[496] = 8;                                                     // maturity_week  @496
        bytes[497] = (byte)'N';                                             // user_defined   @497
        bytes[498] = 0xFF;                                                  // ctr_mult_unit  @498  (-1)
        bytes[499] = 0xFE;                                                  // flow_sched_type@499  (-2)
        bytes[500] = 9;                                                     // tick_rule      @500
        bytes[501] = (byte)'C';                                             // leg_inst_class @501
        bytes[502] = (byte)'B';                                             // leg_side       @502

        ref readonly var msg = ref MemoryMarshal.AsRef<InstrumentDefMsg>((ReadOnlySpan<byte>)bytes);

        Assert.Equal(520, msg.Header.SizeInBytes);
        Assert.Equal(RType.InstrumentDef, msg.Header.RType);
        Assert.Equal(4UL, msg.TsRecv);
        Assert.Equal(5, msg.MinPriceIncrement);
        Assert.Equal(106, msg.StrikePrice);

        // Would be 7 if the field were still 32-bit, as it is in v1 and v2.
        Assert.Equal(0x1_0000_0007UL, msg.RawInstrumentId);

        Assert.Equal(108, msg.LegPrice);
        Assert.Equal(109, msg.LegDelta);

        // Every slot in the 136-208 run, read back through its own named field.
        Assert.Equal(136, msg.InstAttribValue);
        Assert.Equal(140u, msg.UnderlyingId);
        Assert.Equal(144, msg.MarketDepthImplied);
        Assert.Equal(148, msg.MarketDepth);
        Assert.Equal(152u, msg.MarketSegmentId);
        Assert.Equal(156u, msg.MaxTradeVol);
        Assert.Equal(160, msg.MinLotSize);
        Assert.Equal(164, msg.MinLotSizeBlock);
        Assert.Equal(168, msg.MinLotSizeRoundLot);
        Assert.Equal(172u, msg.MinTradeVol);
        Assert.Equal(176, msg.ContractMultiplier);
        Assert.Equal(180, msg.DecayQuantity);
        Assert.Equal(184, msg.OriginalContractSize);
        Assert.Equal(188u, msg.LegInstrumentId);
        Assert.Equal(192, msg.LegRatioPriceNumerator);
        Assert.Equal(196, msg.LegRatioPriceDenominator);
        Assert.Equal(200, msg.LegRatioQtyNumerator);
        Assert.Equal(204, msg.LegRatioQtyDenominator);
        Assert.Equal(208u, msg.LegUnderlyingId);

        Assert.Equal(-113, msg.ApplId);
        Assert.Equal(114, msg.LegCount);
        Assert.Equal(115, msg.LegIndex);
        Assert.Equal("USD", msg.Currency.ToString());
        Assert.Equal("GBP", msg.SettlCurrency.ToString());
        Assert.Equal("ABCDE", msg.SecSubType.ToString());
        Assert.Equal("ESH4", msg.RawSymbol.ToString());
        Assert.Equal("ES", msg.Group.ToString());
        Assert.Equal("XCME", msg.Exchange.ToString());
        Assert.Equal("ES", msg.Asset.ToString());
        Assert.Equal("FFICSX", msg.Cfi.ToString());
        Assert.Equal("FUT", msg.SecurityType.ToString());
        Assert.Equal("IPNT", msg.UnitOfMeasure.ToString());
        Assert.Equal("SPX", msg.Underlying.ToString());
        Assert.Equal("USD", msg.StrikePriceCurrency.ToString());
        Assert.Equal("ESM4-ESU4", msg.LegRawSymbol.ToString());
        Assert.Equal(InstrumentClass.Future, msg.InstrumentClass);
        Assert.Equal(MatchAlgorithm.Fifo, msg.MatchAlgorithm);
        Assert.Equal(1, msg.MainFraction);
        Assert.Equal(2, msg.PriceDisplayFormat);
        Assert.Equal(3, msg.SubFraction);
        Assert.Equal(4, msg.UnderlyingProduct);
        Assert.Equal(SecurityUpdateAction.Add, msg.SecurityUpdateAction);
        Assert.Equal(6, msg.MaturityMonth);
        Assert.Equal(7, msg.MaturityDay);
        Assert.Equal(8, msg.MaturityWeek);
        Assert.Equal(UserDefinedInstrument.No, msg.UserDefinedInstrument);
        Assert.Equal(-1, msg.ContractMultiplierUnit);
        Assert.Equal(-2, msg.FlowScheduleType);
        Assert.Equal(9, msg.TickRule);
        Assert.Equal(InstrumentClass.Call, msg.LegInstrumentClass);
        Assert.Equal(Side.Bid, msg.LegSide);
    }

    [Fact]
    public void InstrumentDefMsgV1_ReadsItsRelocatedStrikePriceAndShorterSymbol()
    {
        // v1's two structural oddities, at their hand-computed offsets: strike_price sits at 328
        // rather than among the other price fields at the front, and raw_symbol is 22 bytes at
        // 200 rather than 71 bytes at 238.
        var bytes = new byte[360];
        var span = bytes.AsSpan();
        span.Fill(0xEE);

        bytes[0] = 90;                                                      // hd.length      @0
        bytes[1] = (byte)RType.InstrumentDef;                               // hd.rtype       @1
        BinaryPrimitives.WriteInt64LittleEndian(span[80..], 1_234);         // trading_ref_px @80

        // inst_attrib_value through original_contract_size: fourteen consecutive 4-byte fields
        // (offsets 112-152 and 160-168, with the reserved dummy at 156 skipped) that no
        // two-or-three-field spot check can tell apart from a transposition of two adjacent
        // ones. Every slot gets its own offset as its sentinel value.
        BinaryPrimitives.WriteInt32LittleEndian(span[112..], 112);          // inst_attrib    @112
        BinaryPrimitives.WriteUInt32LittleEndian(span[116..], 116);         // underlying_id  @116
        BinaryPrimitives.WriteUInt32LittleEndian(span[120..], 120);         // raw_inst_id    @120
        BinaryPrimitives.WriteInt32LittleEndian(span[124..], 124);          // mkt_depth_impl @124
        BinaryPrimitives.WriteInt32LittleEndian(span[128..], 128);          // market_depth   @128
        BinaryPrimitives.WriteUInt32LittleEndian(span[132..], 132);         // mkt_segment_id @132
        BinaryPrimitives.WriteUInt32LittleEndian(span[136..], 136);         // max_trade_vol  @136
        BinaryPrimitives.WriteInt32LittleEndian(span[140..], 140);          // min_lot_size   @140
        BinaryPrimitives.WriteInt32LittleEndian(span[144..], 144);          // min_lot_sz_blk @144
        BinaryPrimitives.WriteInt32LittleEndian(span[148..], 148);          // min_lot_sz_rl  @148
        BinaryPrimitives.WriteUInt32LittleEndian(span[152..], 152);         // min_trade_vol  @152
        BinaryPrimitives.WriteInt32LittleEndian(span[160..], 160);          // contract_mult  @160
        BinaryPrimitives.WriteInt32LittleEndian(span[164..], 164);          // decay_qty      @164
        BinaryPrimitives.WriteInt32LittleEndian(span[168..], 168);          // orig_ctr_size  @168

        BinaryPrimitives.WriteUInt16LittleEndian(span[176..], 19_000);      // trading_ref_dt @176
        "MSFT\0"u8.CopyTo(span[200..]);                                     // raw_symbol     @200
        "EQ\0"u8.CopyTo(span[248..]);                                       // asset          @248
        bytes[325] = (byte)'K';                                             // instrument_cls @325
        BinaryPrimitives.WriteInt64LittleEndian(span[328..], 4_200);        // strike_price   @328
        bytes[342] = (byte)'F';                                             // match_algorithm@342
        bytes[343] = 17;                                                    // md_sec_status  @343
        bytes[346] = 18;                                                    // settl_px_type  @346
        bytes[349] = (byte)'M';                                             // sec_upd_action @349
        bytes[353] = (byte)'Y';                                             // user_defined   @353

        ref readonly var msg =
            ref MemoryMarshal.AsRef<InstrumentDefMsgV1>((ReadOnlySpan<byte>)bytes);

        Assert.Equal(360, msg.Header.SizeInBytes);
        Assert.Equal(1_234, msg.TradingReferencePrice);

        // Every slot in the 112-168 run, read back through its own named field.
        Assert.Equal(112, msg.InstAttribValue);
        Assert.Equal(116u, msg.UnderlyingId);
        Assert.Equal(120u, msg.RawInstrumentId);
        Assert.Equal(124, msg.MarketDepthImplied);
        Assert.Equal(128, msg.MarketDepth);
        Assert.Equal(132u, msg.MarketSegmentId);
        Assert.Equal(136u, msg.MaxTradeVol);
        Assert.Equal(140, msg.MinLotSize);
        Assert.Equal(144, msg.MinLotSizeBlock);
        Assert.Equal(148, msg.MinLotSizeRoundLot);
        Assert.Equal(152u, msg.MinTradeVol);
        Assert.Equal(160, msg.ContractMultiplier);
        Assert.Equal(164, msg.DecayQuantity);
        Assert.Equal(168, msg.OriginalContractSize);

        Assert.Equal(19_000, msg.TradingReferenceDate);
        Assert.Equal("MSFT", msg.RawSymbol.ToString());
        Assert.Equal("EQ", msg.Asset.ToString());
        Assert.Equal(InstrumentClass.Stock, msg.InstrumentClass);
        Assert.Equal(4_200, msg.StrikePrice);
        Assert.Equal(MatchAlgorithm.Fifo, msg.MatchAlgorithm);
        Assert.Equal(17, msg.MdSecurityTradingStatus);
        Assert.Equal(18, msg.SettlPriceType);
        Assert.Equal(SecurityUpdateAction.Modify, msg.SecurityUpdateAction);
        Assert.Equal(UserDefinedInstrument.Yes, msg.UserDefinedInstrument);
    }

    [Fact]
    public void InstrumentDefMsgV2_ReadsEveryFieldInTheAdjacentIntegerRun()
    {
        // v2 removed all five of v1's reserved blocks, so inst_attrib_value through
        // original_contract_size is fully contiguous here: fourteen 4-byte fields at offsets
        // 120-172, with no reserved gap the way v1 has at 156. No hand-built byte test existed
        // for this run at all before now — AssertFieldOffsets checked each field's position
        // against a hand-typed list, but never against an independently-derived value written
        // to a raw offset, so a transposition present in both the struct and that list would
        // have passed everything. Every slot below gets its own offset as its sentinel value.
        var bytes = new byte[400];
        var span = bytes.AsSpan();
        span.Fill(0xEE);

        bytes[0] = 100;                                                     // hd.length      @0
        bytes[1] = (byte)RType.InstrumentDef;                               // hd.rtype       @1
        BinaryPrimitives.WriteInt32LittleEndian(span[120..], 120);          // inst_attrib    @120
        BinaryPrimitives.WriteUInt32LittleEndian(span[124..], 124);         // underlying_id  @124
        BinaryPrimitives.WriteUInt32LittleEndian(span[128..], 128);         // raw_inst_id    @128
        BinaryPrimitives.WriteInt32LittleEndian(span[132..], 132);          // mkt_depth_impl @132
        BinaryPrimitives.WriteInt32LittleEndian(span[136..], 136);          // market_depth   @136
        BinaryPrimitives.WriteUInt32LittleEndian(span[140..], 140);         // mkt_segment_id @140
        BinaryPrimitives.WriteUInt32LittleEndian(span[144..], 144);         // max_trade_vol  @144
        BinaryPrimitives.WriteInt32LittleEndian(span[148..], 148);          // min_lot_size   @148
        BinaryPrimitives.WriteInt32LittleEndian(span[152..], 152);          // min_lot_sz_blk @152
        BinaryPrimitives.WriteInt32LittleEndian(span[156..], 156);          // min_lot_sz_rl  @156
        BinaryPrimitives.WriteUInt32LittleEndian(span[160..], 160);         // min_trade_vol  @160
        BinaryPrimitives.WriteInt32LittleEndian(span[164..], 164);          // contract_mult  @164
        BinaryPrimitives.WriteInt32LittleEndian(span[168..], 168);          // decay_qty      @168
        BinaryPrimitives.WriteInt32LittleEndian(span[172..], 172);          // orig_ctr_size  @172

        ref readonly var msg =
            ref MemoryMarshal.AsRef<InstrumentDefMsgV2>((ReadOnlySpan<byte>)bytes);

        Assert.Equal(400, msg.Header.SizeInBytes);
        Assert.Equal(RType.InstrumentDef, msg.Header.RType);
        Assert.Equal(120, msg.InstAttribValue);
        Assert.Equal(124u, msg.UnderlyingId);
        Assert.Equal(128u, msg.RawInstrumentId);
        Assert.Equal(132, msg.MarketDepthImplied);
        Assert.Equal(136, msg.MarketDepth);
        Assert.Equal(140u, msg.MarketSegmentId);
        Assert.Equal(144u, msg.MaxTradeVol);
        Assert.Equal(148, msg.MinLotSize);
        Assert.Equal(152, msg.MinLotSizeBlock);
        Assert.Equal(156, msg.MinLotSizeRoundLot);
        Assert.Equal(160u, msg.MinTradeVol);
        Assert.Equal(164, msg.ContractMultiplier);
        Assert.Equal(168, msg.DecayQuantity);
        Assert.Equal(172, msg.OriginalContractSize);
    }

    [Fact]
    public void ImbalanceMsg_ReadsEveryFieldAtItsWireOffset()
    {
        var bytes = new byte[112];
        var span = bytes.AsSpan();
        span.Fill(0xEE);

        bytes[0] = 28;                                                      // hd.length      @0
        bytes[1] = (byte)RType.Imbalance;                                   // hd.rtype       @1
        BinaryPrimitives.WriteUInt64LittleEndian(span[16..], 1);            // ts_recv        @16
        BinaryPrimitives.WriteInt64LittleEndian(span[24..], 2);             // ref_price      @24
        BinaryPrimitives.WriteUInt64LittleEndian(span[32..], 3);            // auction_time   @32
        BinaryPrimitives.WriteInt64LittleEndian(span[40..], 4);             // cont_book_clr  @40
        BinaryPrimitives.WriteInt64LittleEndian(span[48..], 5);             // auct_int_clr   @48
        BinaryPrimitives.WriteInt64LittleEndian(span[56..], 6);             // ssr_filling_px @56
        BinaryPrimitives.WriteInt64LittleEndian(span[64..], 7);             // ind_match_px   @64
        BinaryPrimitives.WriteInt64LittleEndian(span[72..], 8);             // upper_collar   @72
        BinaryPrimitives.WriteInt64LittleEndian(span[80..], 9);             // lower_collar   @80
        BinaryPrimitives.WriteUInt32LittleEndian(span[88..], 10);           // paired_qty     @88
        BinaryPrimitives.WriteUInt32LittleEndian(span[92..], 11);           // total_imb_qty  @92
        BinaryPrimitives.WriteUInt32LittleEndian(span[96..], 12);           // market_imb_qty @96
        BinaryPrimitives.WriteUInt32LittleEndian(span[100..], 13);          // unpaired_qty   @100
        bytes[104] = (byte)'O';                                             // auction_type   @104
        bytes[105] = (byte)'B';                                             // side           @105
        bytes[106] = 14;                                                    // auction_status @106
        bytes[107] = 15;                                                    // freeze_status  @107
        bytes[108] = 16;                                                    // num_extensions @108
        bytes[109] = (byte)'A';                                             // unpaired_side  @109
        bytes[110] = (byte)'~';                                             // signif_imb     @110

        ref readonly var msg = ref MemoryMarshal.AsRef<ImbalanceMsg>((ReadOnlySpan<byte>)bytes);

        Assert.Equal(112, msg.Header.SizeInBytes);
        Assert.Equal(1UL, msg.TsRecv);
        Assert.Equal(2, msg.RefPrice);
        Assert.Equal(3UL, msg.AuctionTime);
        Assert.Equal(4, msg.ContBookClrPrice);
        Assert.Equal(5, msg.AuctInterestClrPrice);
        Assert.Equal(6, msg.SsrFillingPrice);
        Assert.Equal(7, msg.IndMatchPrice);
        Assert.Equal(8, msg.UpperCollar);
        Assert.Equal(9, msg.LowerCollar);
        Assert.Equal(10u, msg.PairedQty);
        Assert.Equal(11u, msg.TotalImbalanceQty);
        Assert.Equal(12u, msg.MarketImbalanceQty);
        Assert.Equal(13u, msg.UnpairedQty);
        Assert.Equal('O', msg.AuctionTypeChar);
        Assert.Equal(Side.Bid, msg.Side);
        Assert.Equal(14, msg.AuctionStatus);
        Assert.Equal(15, msg.FreezeStatus);
        Assert.Equal(16, msg.NumExtensions);
        Assert.Equal(Side.Ask, msg.UnpairedSide);
        Assert.Equal('~', msg.SignificantImbalanceChar);
    }

    [Fact]
    public void StatMsg_ReadsEveryFieldAtItsWireOffset()
    {
        var bytes = new byte[80];
        var span = bytes.AsSpan();
        span.Fill(0xEE);

        bytes[0] = 20;                                                      // hd.length      @0
        bytes[1] = (byte)RType.Statistics;                                  // hd.rtype       @1
        BinaryPrimitives.WriteUInt64LittleEndian(span[16..], 1);            // ts_recv        @16
        BinaryPrimitives.WriteUInt64LittleEndian(span[24..], 2);            // ts_ref         @24
        BinaryPrimitives.WriteInt64LittleEndian(span[32..], 3);             // price          @32
        BinaryPrimitives.WriteInt64LittleEndian(span[40..], long.MaxValue); // quantity       @40
        BinaryPrimitives.WriteUInt32LittleEndian(span[48..], 4);            // sequence       @48
        BinaryPrimitives.WriteInt32LittleEndian(span[52..], -5);            // ts_in_delta    @52
        BinaryPrimitives.WriteUInt16LittleEndian(span[56..], (ushort)StatType.OpeningPrice);
        BinaryPrimitives.WriteUInt16LittleEndian(span[58..], 6);            // channel_id     @58
        bytes[60] = (byte)StatUpdateAction.Delete;                          // update_action  @60
        bytes[61] = 0b0000_0011;                                            // stat_flags     @61

        ref readonly var msg = ref MemoryMarshal.AsRef<StatMsg>((ReadOnlySpan<byte>)bytes);

        Assert.Equal(80, msg.Header.SizeInBytes);
        Assert.Equal(1UL, msg.TsRecv);
        Assert.Equal(2UL, msg.TsRef);
        Assert.Equal(3, msg.Price);
        Assert.Equal(DbnConstants.UndefStatQuantity, msg.Quantity);
        Assert.Equal(4u, msg.Sequence);
        Assert.Equal(-5, msg.TsInDelta);
        Assert.Equal(StatType.OpeningPrice, msg.StatType);
        Assert.Equal(6, msg.ChannelId);
        Assert.Equal(StatUpdateAction.Delete, msg.UpdateAction);
        Assert.Equal(0b0000_0011, msg.StatFlags);
    }

    [Fact]
    public void ErrorMsg_ReadsItsCodeAndIsLastPastThe302ByteMessage()
    {
        var bytes = new byte[320];
        var span = bytes.AsSpan();

        bytes[0] = 80;                                                      // hd.length      @0
        bytes[1] = (byte)RType.Error;                                       // hd.rtype       @1
        "Internal error\0"u8.CopyTo(span[16..]);                            // err            @16
        bytes[318] = (byte)ErrorCode.InternalError;                         // code           @318
        bytes[319] = 1;                                                     // is_last        @319

        ref readonly var msg = ref MemoryMarshal.AsRef<ErrorMsg>((ReadOnlySpan<byte>)bytes);

        Assert.Equal(320, msg.Header.SizeInBytes);
        Assert.Equal("Internal error", msg.Err.ToString());
        Assert.Equal(ErrorCode.InternalError, msg.Code);
        Assert.Equal(1, msg.IsLast);
    }

    [Fact]
    public void SymbolMappingMsg_ReadsBothSymbolsAndBothSymbologyTypes()
    {
        var bytes = new byte[176];
        var span = bytes.AsSpan();

        bytes[0] = 44;                                                      // hd.length      @0
        bytes[1] = (byte)RType.SymbolMapping;                               // hd.rtype       @1
        bytes[16] = (byte)SType.Continuous;                                 // stype_in       @16
        "ES.c.0\0"u8.CopyTo(span[17..]);                                    // stype_in_sym   @17
        bytes[88] = (byte)SType.RawSymbol;                                  // stype_out      @88
        "ESH4\0"u8.CopyTo(span[89..]);                                      // stype_out_sym  @89
        BinaryPrimitives.WriteUInt64LittleEndian(span[160..], 111);         // start_ts       @160
        BinaryPrimitives.WriteUInt64LittleEndian(span[168..], 222);         // end_ts         @168

        ref readonly var msg =
            ref MemoryMarshal.AsRef<SymbolMappingMsg>((ReadOnlySpan<byte>)bytes);

        Assert.Equal(176, msg.Header.SizeInBytes);
        Assert.Equal(SType.Continuous, msg.StypeIn);
        Assert.Equal("ES.c.0", msg.StypeInSymbol.ToString());
        Assert.Equal(SType.RawSymbol, msg.StypeOut);
        Assert.Equal("ESH4", msg.StypeOutSymbol.ToString());
        Assert.Equal(111UL, msg.StartTs);
        Assert.Equal(222UL, msg.EndTs);
    }

    [Fact]
    public void SymbolMappingMsgV1_ReadsBothSymbolsAcrossTheAlignmentDummy()
    {
        var bytes = new byte[80];
        var span = bytes.AsSpan();
        span.Fill(0xEE);

        bytes[0] = 20;                                                      // hd.length      @0
        bytes[1] = (byte)RType.SymbolMapping;                               // hd.rtype       @1
        "ES.c.0\0"u8.CopyTo(span[16..]);                                    // stype_in_sym   @16
        "ESH4\0"u8.CopyTo(span[38..]);                                      // stype_out_sym  @38
        BinaryPrimitives.WriteUInt64LittleEndian(span[64..], 111);          // start_ts       @64
        BinaryPrimitives.WriteUInt64LittleEndian(span[72..], 222);          // end_ts         @72

        ref readonly var msg =
            ref MemoryMarshal.AsRef<SymbolMappingMsgV1>((ReadOnlySpan<byte>)bytes);

        Assert.Equal(80, msg.Header.SizeInBytes);
        Assert.Equal("ES.c.0", msg.StypeInSymbol.ToString());
        Assert.Equal("ESH4", msg.StypeOutSymbol.ToString());
        Assert.Equal(111UL, msg.StartTs);
        Assert.Equal(222UL, msg.EndTs);
    }

    [Fact]
    public void SystemMsg_ReadsItsCodePastThe303ByteMessage()
    {
        var bytes = new byte[320];
        var span = bytes.AsSpan();

        bytes[0] = 80;                                                      // hd.length      @0
        bytes[1] = (byte)RType.System;                                      // hd.rtype       @1
        "Heartbeat\0"u8.CopyTo(span[16..]);                                 // msg            @16
        bytes[319] = (byte)SystemCode.Heartbeat;                            // code           @319

        ref readonly var msg = ref MemoryMarshal.AsRef<SystemMsg>((ReadOnlySpan<byte>)bytes);

        Assert.Equal(320, msg.Header.SizeInBytes);
        Assert.Equal("Heartbeat", msg.Msg.ToString());
        Assert.Equal(SystemCode.Heartbeat, msg.Code);
    }

    // ---------------------------------------------------------------------------------------
    // WithTsOut<T>: the +8-byte wrapper, and the one length that is not correct for free.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void WithTsOut_IsTheWrappedRecordPlusEightBytes()
    {
        Assert.Equal(48 + 8, Unsafe.SizeOf<WithTsOut<TradeMsg>>());
        Assert.Equal(520 + 8, Unsafe.SizeOf<WithTsOut<InstrumentDefMsg>>());
        Assert.Equal(320 + 8, Unsafe.SizeOf<WithTsOut<SystemMsg>>());

        // No padding between the record and ts_out, for any T: every record's size is already a
        // multiple of eight.
        Assert.Equal(0, RecordLayout.OffsetOf<WithTsOut<TradeMsg>>("Record"));
        Assert.Equal(48, RecordLayout.OffsetOf<WithTsOut<TradeMsg>>("TsOut"));
        Assert.Equal(520, RecordLayout.OffsetOf<WithTsOut<InstrumentDefMsg>>("TsOut"));
    }

    [Fact]
    public void WithTsOut_RecomputesTheHeaderLengthForTheExtraEightBytes()
    {
        var bytes = new byte[48];
        bytes[0] = 12;                                        // 48 bytes, the record's own length
        bytes[1] = (byte)RType.Mbp0;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 99);
        var trade = MemoryMarshal.Read<TradeMsg>(bytes);
        Assert.Equal(48, trade.Header.SizeInBytes);

        var wrapped = new WithTsOut<TradeMsg>(trade, 1_700_000_000_000_000_000);

        // 48 + 8 = 56 bytes = 14 words. A wrong length here desynchronises the whole stream.
        Assert.Equal(14, wrapped.Record.Header.Length);
        Assert.Equal(56, wrapped.Record.Header.SizeInBytes);
        Assert.Equal(Unsafe.SizeOf<WithTsOut<TradeMsg>>(), wrapped.Record.Header.SizeInBytes);
        Assert.Equal(1_700_000_000_000_000_000UL, wrapped.TsOut);

        // Nothing else in the record moved, and the source value was not mutated.
        Assert.Equal(RType.Mbp0, wrapped.Record.Header.RType);
        Assert.Equal(99u, wrapped.Record.Header.InstrumentId);
        Assert.Equal(12, trade.Header.Length);
    }

    [Fact]
    public void WithTsOut_RecomputesTheLengthOfTheLargestRecordToo()
    {
        var def = default(InstrumentDefMsg);
        var wrapped = new WithTsOut<InstrumentDefMsg>(def, 7);

        // 528 / 4 = 132, which still fits in the byte the wire gives it.
        Assert.Equal(132, wrapped.Record.Header.Length);
        Assert.Equal(DbnConstants.MaxRecordLength, wrapped.Record.Header.SizeInBytes);
    }

    private static void AssertWireSize<T>(int expected, List<Type>? covered)
        where T : unmanaged, IRecord<T>
    {
        covered?.Add(typeof(T));

        Assert.Equal(expected, T.WireSize);
        Assert.Equal(Unsafe.SizeOf<T>(), T.WireSize);

        // WithTsOut<T> writes hd.length as the first byte of the wrapped record, so every record
        // must declare its header first. Folded in here rather than kept as its own separate
        // list of record types, so there is one list to maintain instead of two — every record
        // already passes through this method for its size assertion, and inherits the header
        // check for free.
        //
        // That is a convenience, not a guarantee: this method is only ever reached from the list
        // in VisitEveryListedRecord, so a record missing from that list skips this check along
        // with the size and alignment ones. What closes that gap is
        // EveryRecordStructInTheAssembly_IsInTheWireSizeList, which reflects over the assembly
        // and fails if the list and the set of IRecord<> implementers differ.
        Assert.Equal(0, RecordLayout.OffsetOf<T>("Header"));
        Assert.Equal(0, RecordLayout.OffsetOf<RecordHeader>(nameof(RecordHeader.Length)));
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
