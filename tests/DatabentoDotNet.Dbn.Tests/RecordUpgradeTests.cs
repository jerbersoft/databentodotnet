using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn.Tests;

/// <summary>
/// Guards the version-upgrade conversions.
/// </summary>
/// <remarks>
/// <para>
/// An upgrade is a value-level conversion into different-sized storage, never an in-place
/// reinterpret — the target is larger and its fields are at different offsets. Two things about
/// it fail silently if they are wrong, so both are asserted directly rather than inferred from a
/// round trip:
/// </para>
/// <list type="number">
/// <item><see cref="RecordHeader.Length"/> must be recomputed for the target's size. A record
/// that says it is 360 bytes when it is 520 desynchronises every record after it.</item>
/// <item>A field the older version did not carry must take its type's real default, which for a
/// price is <see cref="DbnConstants.UndefPrice"/> and not zero. Zero is a valid price, so a
/// zero-filled leg price reads as a fact rather than as an absence.</item>
/// </list>
/// <para>
/// Every source record here is built from a byte buffer at hand-written offsets, so these tests
/// also exercise the version-specific layouts independently of the structs' own declarations.
/// </para>
/// </remarks>
public class RecordUpgradeTests
{
    // ------------------------------------------------------------------------------------
    // StatMsg: the sentinel that has to be translated rather than widened.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void StatMsgV1_UpgradeTo_TranslatesTheUndefinedQuantitySentinel()
    {
        var old = CreateStatMsgV1(quantity: DbnConstants.UndefStatQuantityV1);

        var upgraded = old.UpgradeTo();

        Assert.Equal(DbnConstants.UndefStatQuantity, upgraded.Quantity);
        Assert.Equal(long.MaxValue, upgraded.Quantity);

        // The failure this exists to catch: a plain widening, which turns "no quantity" into a
        // real and entirely plausible quantity of two billion.
        Assert.NotEqual(2_147_483_647L, upgraded.Quantity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(123_456)]
    [InlineData(int.MaxValue - 1)]
    [InlineData(int.MinValue)]
    public void StatMsgV1_UpgradeTo_WidensAnOrdinaryQuantityUnchanged(int quantity)
    {
        var upgraded = CreateStatMsgV1(quantity).UpgradeTo();

        Assert.Equal(quantity, upgraded.Quantity);
        Assert.NotEqual(DbnConstants.UndefStatQuantity, upgraded.Quantity);
    }

    [Fact]
    public void StatMsgV1_UpgradeTo_CarriesEveryOtherFieldAndRecomputesTheLength()
    {
        var upgraded = CreateStatMsgV1(quantity: 7).UpgradeTo();

        Assert.Equal(80, upgraded.Header.SizeInBytes);
        Assert.Equal(20, upgraded.Header.Length);
        Assert.Equal((byte)RType.Statistics, upgraded.Header.RType);
        Assert.Equal(1, upgraded.Header.PublisherId);
        Assert.Equal(2u, upgraded.Header.InstrumentId);
        Assert.Equal(3UL, upgraded.Header.TsEvent);
        Assert.Equal(4UL, upgraded.TsRecv);
        Assert.Equal(5UL, upgraded.TsRef);
        Assert.Equal(6L, upgraded.Price);
        Assert.Equal(8u, upgraded.Sequence);
        Assert.Equal(-9, upgraded.TsInDelta);
        Assert.Equal(StatType.SettlementPrice, upgraded.StatType);
        Assert.Equal(10, upgraded.ChannelId);
        Assert.Equal(StatUpdateAction.Delete, upgraded.UpdateAction);
        Assert.Equal(0b0000_0101, upgraded.StatFlags);
    }

    // ------------------------------------------------------------------------------------
    // ErrorMsg and SystemMsg: fields v1 never carried, recovered from the message text.
    // ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("User or API key deactivated", ErrorCode.ApiKeyDeactivated)]
    [InlineData("User has reached their open connection limit", ErrorCode.ConnectionLimitExceeded)]
    [InlineData("Failed to resolve symbol ESH4", ErrorCode.SymbolResolutionFailed)]
    [InlineData("Internal error", ErrorCode.InternalError)]
    [InlineData("Slow client detected for session 12", ErrorCode.SkippedRecordsAfterSlowReading)]
    [InlineData("Something nobody has seen before", ErrorCode.Unset)]
    [InlineData("", ErrorCode.Unset)]
    public void ErrorMsgV1_UpgradeTo_InfersTheCodeFromTheMessage(string message, ErrorCode expected)
    {
        var upgraded = CreateErrorMsgV1(message).UpgradeTo();

        Assert.Equal(expected, upgraded.Code);
    }

    [Fact]
    public void ErrorMsgV1_UpgradeTo_CopiesTheMessageAndRecomputesTheLength()
    {
        var upgraded = CreateErrorMsgV1("Internal error").UpgradeTo();

        Assert.Equal(320, upgraded.Header.SizeInBytes);
        Assert.Equal(80, upgraded.Header.Length);
        Assert.Equal((byte)RType.Error, upgraded.Header.RType);
        Assert.Equal(1, upgraded.Header.PublisherId);
        Assert.Equal(2u, upgraded.Header.InstrumentId);
        Assert.Equal(3UL, upgraded.Header.TsEvent);
        Assert.Equal("Internal error", upgraded.Err.ToString());

        // The 238 bytes of the wider field the old record could not fill stay NUL.
        Assert.Equal(302, upgraded.Err.AsSpan().Length);
        Assert.True(upgraded.Err.AsSpan()[64..].IndexOfAnyExcept((byte)0) < 0);
    }

    [Fact]
    public void ErrorMsgV1_UpgradeTo_SetsIsLastToTheUpstreamDefaultRatherThanZero()
    {
        var upgraded = CreateErrorMsgV1("Internal error").UpgradeTo();

        // Upstream's ErrorMsg default is byte.MaxValue. Zero would claim this error is one of a
        // batch with more to follow, which a v1 record says nothing about either way.
        Assert.Equal(byte.MaxValue, upgraded.IsLast);
    }

    [Theory]
    [InlineData("Heartbeat", SystemCode.Heartbeat)]
    [InlineData("End of interval for 2024-01-01", SystemCode.EndOfInterval)]
    [InlineData("Subscription request for ESH4 succeeded", SystemCode.SubscriptionAck)]
    [InlineData("Warning: slow reading detected", SystemCode.SlowReaderWarning)]
    [InlineData("Finished intraday replay", SystemCode.ReplayCompleted)]
    [InlineData("Heartbeats are on", SystemCode.Unset)]
    [InlineData("Subscription request for ESH4 failed", SystemCode.Unset)]
    [InlineData("Finished but not a replay of anything", SystemCode.Unset)]
    [InlineData("", SystemCode.Unset)]
    public void SystemMsgV1_UpgradeTo_InfersTheCodeFromTheMessage(
        string message,
        SystemCode expected)
    {
        var upgraded = CreateSystemMsgV1(message).UpgradeTo();

        Assert.Equal(expected, upgraded.Code);
    }

    [Fact]
    public void SystemMsgV1_UpgradeTo_CopiesTheMessageAndRecomputesTheLength()
    {
        var upgraded = CreateSystemMsgV1("Heartbeat").UpgradeTo();

        Assert.Equal(320, upgraded.Header.SizeInBytes);
        Assert.Equal(80, upgraded.Header.Length);
        Assert.Equal((byte)RType.System, upgraded.Header.RType);
        Assert.Equal(3UL, upgraded.Header.TsEvent);
        Assert.Equal("Heartbeat", upgraded.Msg.ToString());
        Assert.Equal(303, upgraded.Msg.AsSpan().Length);
        Assert.True(upgraded.Msg.AsSpan()[64..].IndexOfAnyExcept((byte)0) < 0);
    }

    // ------------------------------------------------------------------------------------
    // SymbolMappingMsg: the two symbology types v1 has no field for.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void SymbolMappingMsgV1_UpgradeTo_LeavesBothSymbologyTypesUnsetRatherThanZero()
    {
        var upgraded = CreateSymbolMappingMsgV1().UpgradeTo();

        // Zero is SType.InstrumentId, a real symbology type. Claiming it for a record that never
        // carried the field would be a wrong answer rather than a missing one.
        Assert.Equal(DbnConstants.NullStype, upgraded.RawStypeIn);
        Assert.Equal(DbnConstants.NullStype, upgraded.RawStypeOut);
        Assert.NotEqual((byte)SType.InstrumentId, upgraded.RawStypeIn);
    }

    [Fact]
    public void SymbolMappingMsgV1_UpgradeTo_CopiesBothSymbolsAndRecomputesTheLength()
    {
        var upgraded = CreateSymbolMappingMsgV1().UpgradeTo();

        Assert.Equal(176, upgraded.Header.SizeInBytes);
        Assert.Equal(44, upgraded.Header.Length);
        Assert.Equal((byte)RType.SymbolMapping, upgraded.Header.RType);
        Assert.Equal("ES.c.0", upgraded.StypeInSymbol.ToString());
        Assert.Equal("ESH4", upgraded.StypeOutSymbol.ToString());
        Assert.Equal(111UL, upgraded.StartTs);
        Assert.Equal(222UL, upgraded.EndTs);

        // The v1 record's four-byte alignment dummy must not have been dragged into the wider
        // symbol buffers; everything past the old 22 bytes is NUL.
        Assert.True(upgraded.StypeInSymbol.AsSpan()[22..].IndexOfAnyExcept((byte)0) < 0);
        Assert.True(upgraded.StypeOutSymbol.AsSpan()[22..].IndexOfAnyExcept((byte)0) < 0);
    }

    // ------------------------------------------------------------------------------------
    // InstrumentDefMsg: the thirteen fields v3 added and the four it dropped.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void InstrumentDefMsgV1_UpgradeTo_LeavesEveryV3OnlyPriceFieldAtUndefPriceNotZero()
    {
        // The source is entirely zero apart from its header, so a conversion that filled the new
        // fields with default(long) instead of the sentinel would still produce a record that
        // decodes, prints, and round-trips — with two leg prices of exactly 0.
        var upgraded = CreateZeroedInstrumentDefMsgV1().UpgradeTo();

        Assert.Equal(DbnConstants.UndefPrice, upgraded.LegPrice);
        Assert.Equal(DbnConstants.UndefPrice, upgraded.LegDelta);
        Assert.NotEqual(0L, upgraded.LegPrice);
        Assert.NotEqual(0L, upgraded.LegDelta);
    }

    [Fact]
    public void InstrumentDefMsgV2_UpgradeTo_LeavesEveryV3OnlyPriceFieldAtUndefPriceNotZero()
    {
        var upgraded = CreateZeroedInstrumentDefMsgV2().UpgradeTo();

        Assert.Equal(DbnConstants.UndefPrice, upgraded.LegPrice);
        Assert.Equal(DbnConstants.UndefPrice, upgraded.LegDelta);
    }

    [Fact]
    public void InstrumentDefMsgV1_UpgradeTo_DefaultsTheRemainingLegFieldsAsUpstreamDoes()
    {
        var upgraded = CreateZeroedInstrumentDefMsgV1().UpgradeTo();

        Assert.Equal(0u, upgraded.LegInstrumentId);
        Assert.Equal(0, upgraded.LegRatioPriceNumerator);
        Assert.Equal(0, upgraded.LegRatioPriceDenominator);
        Assert.Equal(0, upgraded.LegRatioQtyNumerator);
        Assert.Equal(0, upgraded.LegRatioQtyDenominator);
        Assert.Equal(0u, upgraded.LegUnderlyingId);
        Assert.Equal(0, upgraded.LegCount);
        Assert.Equal(0, upgraded.LegIndex);
        Assert.Equal(string.Empty, upgraded.LegRawSymbol.ToString());

        // InstrumentClass has no "none" discriminant, so upstream leaves a raw zero byte here.
        // Side does have one, so upstream uses it.
        Assert.Equal(0, upgraded.RawLegInstrumentClass);
        Assert.Equal((byte)Side.None, upgraded.RawLegSide);
        Assert.Equal(Side.None, upgraded.LegSide);
    }

    [Fact]
    public void InstrumentDefMsgV1_UpgradeTo_CarriesTheSharedFieldsAndRecomputesTheLength()
    {
        var upgraded = CreateInstrumentDefMsgV1().UpgradeTo();

        Assert.Equal(520, upgraded.Header.SizeInBytes);
        Assert.Equal(130, upgraded.Header.Length);
        Assert.Equal((byte)RType.InstrumentDef, upgraded.Header.RType);
        Assert.Equal(1, upgraded.Header.PublisherId);
        Assert.Equal(2u, upgraded.Header.InstrumentId);
        Assert.Equal(3UL, upgraded.Header.TsEvent);
        Assert.Equal(4UL, upgraded.TsRecv);
        Assert.Equal(5L, upgraded.MinPriceIncrement);
        Assert.Equal(4_200L, upgraded.StrikePrice);
        Assert.Equal(InstrumentClass.Stock, upgraded.InstrumentClass);
        Assert.Equal(MatchAlgorithm.Fifo, upgraded.MatchAlgorithm);
        Assert.Equal(SecurityUpdateAction.Modify, upgraded.SecurityUpdateAction);
        Assert.Equal(UserDefinedInstrument.Yes, upgraded.UserDefinedInstrument);
        Assert.Equal(19_001, upgraded.DecayStartDate);
    }

    [Fact]
    public void InstrumentDefMsgV1_UpgradeTo_WidensRawInstrumentIdAndGrowsBothStringFields()
    {
        var upgraded = CreateInstrumentDefMsgV1().UpgradeTo();

        Assert.Equal(0xDEAD_BEEFUL, upgraded.RawInstrumentId);

        // 22 -> 71 and 7 -> 11, with the remainder of each wider buffer left NUL.
        Assert.Equal("MSFT", upgraded.RawSymbol.ToString());
        Assert.Equal(71, upgraded.RawSymbol.AsSpan().Length);
        Assert.True(upgraded.RawSymbol.AsSpan()[22..].IndexOfAnyExcept((byte)0) < 0);
        Assert.Equal("EQ", upgraded.Asset.ToString());
        Assert.Equal(11, upgraded.Asset.AsSpan().Length);
        Assert.True(upgraded.Asset.AsSpan()[7..].IndexOfAnyExcept((byte)0) < 0);
    }

    [Fact]
    public void InstrumentDefMsgV2_UpgradeTo_CarriesTheFullWidthSymbolAndGrowsOnlyAsset()
    {
        var upgraded = CreateInstrumentDefMsgV2().UpgradeTo();

        Assert.Equal(520, upgraded.Header.SizeInBytes);
        Assert.Equal(130, upgraded.Header.Length);
        Assert.Equal(0xDEAD_BEEFUL, upgraded.RawInstrumentId);

        // v2's raw_symbol is already 71 bytes, so it is carried whole rather than copied into a
        // wider buffer — including a symbol that fills the field with no terminator.
        var full = new string('A', CStr71.Length);
        Assert.Equal(full, upgraded.RawSymbol.ToString());
        Assert.Equal("EQ", upgraded.Asset.ToString());
        Assert.True(upgraded.Asset.AsSpan()[7..].IndexOfAnyExcept((byte)0) < 0);
        Assert.Equal(4_200L, upgraded.StrikePrice);
        Assert.Equal(InstrumentClass.Stock, upgraded.InstrumentClass);
    }

    [Fact]
    public void UpgradeTo_NeverProducesARecordWhoseHeaderLengthDisagreesWithItsSize()
    {
        // One assertion per conversion. A header length copied from the source rather than
        // recomputed is the failure mode, and it is invisible until the stream desynchronises.
        AssertLengthMatchesSize(CreateInstrumentDefMsgV1().UpgradeTo().Header, 520);
        AssertLengthMatchesSize(CreateInstrumentDefMsgV2().UpgradeTo().Header, 520);
        AssertLengthMatchesSize(CreateStatMsgV1(1).UpgradeTo().Header, 80);
        AssertLengthMatchesSize(CreateErrorMsgV1("x").UpgradeTo().Header, 320);
        AssertLengthMatchesSize(CreateSymbolMappingMsgV1().UpgradeTo().Header, 176);
        AssertLengthMatchesSize(CreateSystemMsgV1("x").UpgradeTo().Header, 320);
    }

    private static void AssertLengthMatchesSize(RecordHeader header, int expectedSize)
    {
        Assert.Equal(expectedSize, header.SizeInBytes);
        Assert.Equal(expectedSize / DbnConstants.RecordLengthMultiplier, header.Length);
    }

    // ------------------------------------------------------------------------------------
    // Source records, built from bytes at hand-written v1/v2 offsets.
    // ------------------------------------------------------------------------------------

    private static void WriteHeader(Span<byte> bytes, RType rtype, int sizeInBytes)
    {
        bytes[0] = (byte)(sizeInBytes / DbnConstants.RecordLengthMultiplier);
        bytes[1] = (byte)rtype;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[2..], 1);            // publisher_id  @2
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], 2);            // instrument_id @4
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], 3);            // ts_event      @8
    }

    private static StatMsgV1 CreateStatMsgV1(int quantity)
    {
        Span<byte> bytes = stackalloc byte[StatMsgV1.WireSize];
        bytes.Clear();
        WriteHeader(bytes, RType.Statistics, StatMsgV1.WireSize);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], 4);           // ts_recv       @16
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], 5);           // ts_ref        @24
        BinaryPrimitives.WriteInt64LittleEndian(bytes[32..], 6);            // price         @32
        BinaryPrimitives.WriteInt32LittleEndian(bytes[40..], quantity);     // quantity      @40
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[44..], 8);           // sequence      @44
        BinaryPrimitives.WriteInt32LittleEndian(bytes[48..], -9);           // ts_in_delta   @48
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[52..], (ushort)StatType.SettlementPrice);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[54..], 10);          // channel_id    @54
        bytes[56] = (byte)StatUpdateAction.Delete;                          // update_action @56
        bytes[57] = 0b0000_0101;                                            // stat_flags    @57
        return MemoryMarshal.Read<StatMsgV1>(bytes);
    }

    private static ErrorMsgV1 CreateErrorMsgV1(string message)
    {
        Span<byte> bytes = stackalloc byte[ErrorMsgV1.WireSize];
        bytes.Clear();
        WriteHeader(bytes, RType.Error, ErrorMsgV1.WireSize);
        System.Text.Encoding.ASCII.GetBytes(message, bytes[16..]);          // err           @16
        return MemoryMarshal.Read<ErrorMsgV1>(bytes);
    }

    private static SystemMsgV1 CreateSystemMsgV1(string message)
    {
        Span<byte> bytes = stackalloc byte[SystemMsgV1.WireSize];
        bytes.Clear();
        WriteHeader(bytes, RType.System, SystemMsgV1.WireSize);
        System.Text.Encoding.ASCII.GetBytes(message, bytes[16..]);          // msg           @16
        return MemoryMarshal.Read<SystemMsgV1>(bytes);
    }

    private static SymbolMappingMsgV1 CreateSymbolMappingMsgV1()
    {
        Span<byte> bytes = stackalloc byte[SymbolMappingMsgV1.WireSize];
        bytes.Clear();
        WriteHeader(bytes, RType.SymbolMapping, SymbolMappingMsgV1.WireSize);
        "ES.c.0"u8.CopyTo(bytes[16..]);                                     // stype_in_sym  @16
        "ESH4"u8.CopyTo(bytes[38..]);                                       // stype_out_sym @38
        bytes[60] = 0xFF;                                                   // _dummy        @60
        bytes[61] = 0xFF;
        bytes[62] = 0xFF;
        bytes[63] = 0xFF;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[64..], 111);         // start_ts      @64
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[72..], 222);         // end_ts        @72
        return MemoryMarshal.Read<SymbolMappingMsgV1>(bytes);
    }

    private static InstrumentDefMsgV1 CreateZeroedInstrumentDefMsgV1()
    {
        Span<byte> bytes = stackalloc byte[InstrumentDefMsgV1.WireSize];
        bytes.Clear();
        WriteHeader(bytes, RType.InstrumentDef, InstrumentDefMsgV1.WireSize);
        return MemoryMarshal.Read<InstrumentDefMsgV1>(bytes);
    }

    private static InstrumentDefMsgV2 CreateZeroedInstrumentDefMsgV2()
    {
        Span<byte> bytes = stackalloc byte[InstrumentDefMsgV2.WireSize];
        bytes.Clear();
        WriteHeader(bytes, RType.InstrumentDef, InstrumentDefMsgV2.WireSize);
        return MemoryMarshal.Read<InstrumentDefMsgV2>(bytes);
    }

    private static InstrumentDefMsgV1 CreateInstrumentDefMsgV1()
    {
        Span<byte> bytes = stackalloc byte[InstrumentDefMsgV1.WireSize];
        bytes.Clear();
        WriteHeader(bytes, RType.InstrumentDef, InstrumentDefMsgV1.WireSize);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], 4);           // ts_recv       @16
        BinaryPrimitives.WriteInt64LittleEndian(bytes[24..], 5);            // min_px_incr   @24
        BinaryPrimitives.WriteInt64LittleEndian(bytes[80..], 999);          // trading_ref_px@80
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[120..], 0xDEADBEEF); // raw_inst_id   @120
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[176..], 19_000);     // trading_ref_dt@176
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[182..], 19_001);     // decay_start   @182
        "MSFT"u8.CopyTo(bytes[200..]);                                      // raw_symbol    @200
        "EQ"u8.CopyTo(bytes[248..]);                                        // asset         @248
        bytes[325] = (byte)'K';                                             // instrument_cls@325
        BinaryPrimitives.WriteInt64LittleEndian(bytes[328..], 4_200);       // strike_price  @328
        bytes[342] = (byte)'F';                                             // match_algo    @342
        bytes[343] = 17;                                                    // md_sec_status @343
        bytes[346] = 18;                                                    // settl_px_type @346
        bytes[349] = (byte)'M';                                             // sec_upd_action@349
        bytes[353] = (byte)'Y';                                             // user_defined  @353
        return MemoryMarshal.Read<InstrumentDefMsgV1>(bytes);
    }

    private static InstrumentDefMsgV2 CreateInstrumentDefMsgV2()
    {
        Span<byte> bytes = stackalloc byte[InstrumentDefMsgV2.WireSize];
        bytes.Clear();
        WriteHeader(bytes, RType.InstrumentDef, InstrumentDefMsgV2.WireSize);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], 4);           // ts_recv       @16
        BinaryPrimitives.WriteInt64LittleEndian(bytes[112..], 4_200);       // strike_price  @112
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[128..], 0xDEADBEEF); // raw_inst_id   @128
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[176..], 19_000);     // trading_ref_dt@176

        // A symbol that fills all 71 bytes, so there is no terminator to rely on.
        bytes.Slice(200, CStr71.Length).Fill((byte)'A');                    // raw_symbol    @200
        "EQ"u8.CopyTo(bytes[297..]);                                        // asset         @297
        bytes[374] = (byte)'K';                                             // instrument_cls@374
        bytes[375] = (byte)'F';                                             // match_algo    @375
        return MemoryMarshal.Read<InstrumentDefMsgV2>(bytes);
    }
}
