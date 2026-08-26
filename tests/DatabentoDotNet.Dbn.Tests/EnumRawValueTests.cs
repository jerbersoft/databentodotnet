using DatabentoDotNet.Dbn.Enums;

// DatabentoDotNet.Dbn.Enums.Action collides with System.Action (brought in via implicit
// usings) whenever both namespaces are in scope in the same file — disambiguate explicitly
// rather than dropping the enum's upstream-matching name.
using Action = DatabentoDotNet.Dbn.Enums.Action;

namespace DatabentoDotNet.Dbn.Tests;

/// <summary>
/// Validates <see cref="EnumValues"/>'s <c>TryFrom*</c> methods — the numeric-decode half of enum conversion,
/// equivalent to upstream's <c>num_enum</c>-derived <c>TryFrom&lt;u8&gt;</c>/
/// <c>TryFrom&lt;u16&gt;</c>. This is a distinct failure mode from an unrecognized wire
/// <em>string</em> (covered in <see cref="EnumWireStringTests"/>); an out-of-range raw byte must
/// fail via <see langword="false"/>, never by throwing.
/// </summary>
public class EnumRawValueTests
{
    [Theory]
    [InlineData(0x00, RType.Mbp0)]
    [InlineData(0x01, RType.Mbp1)]
    [InlineData(0x0A, RType.Mbp10)]
    [InlineData(0x11, RType.OhlcvDeprecated)]
    [InlineData(0x12, RType.Status)]
    [InlineData(0x13, RType.InstrumentDef)]
    [InlineData(0x14, RType.Imbalance)]
    [InlineData(0x15, RType.Error)]
    [InlineData(0x16, RType.SymbolMapping)]
    [InlineData(0x17, RType.System)]
    [InlineData(0x18, RType.Statistics)]
    [InlineData(0x20, RType.Ohlcv1S)]
    [InlineData(0x21, RType.Ohlcv1M)]
    [InlineData(0x22, RType.Ohlcv1H)]
    [InlineData(0x23, RType.Ohlcv1D)]
    [InlineData(0x24, RType.OhlcvEod)]
    [InlineData(0xA0, RType.Mbo)]
    [InlineData(0xB1, RType.Cmbp1)]
    [InlineData(0xC0, RType.Cbbo1S)]
    [InlineData(0xC1, RType.Cbbo1M)]
    [InlineData(0xC2, RType.Tcbbo)]
    [InlineData(0xC3, RType.Bbo1S)]
    [InlineData(0xC4, RType.Bbo1M)]
    public void RType_TryFrom_AcceptsExactlyTheDefinedDiscriminants(byte raw, RType expected)
    {
        Assert.True(EnumValues.TryFromRType(raw, out RType value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(0x02)]
    [InlineData(0x03)]
    [InlineData(0x04)]
    [InlineData(0x05)]
    [InlineData(0x06)]
    [InlineData(0x07)]
    [InlineData(0x08)]
    [InlineData(0x09)]
    [InlineData(0x0B)]
    [InlineData(0x0C)]
    [InlineData(0x0D)]
    [InlineData(0x0E)]
    [InlineData(0x0F)]
    public void RType_TryFrom_RejectsUndefinedBytesInTheMbpNibble(byte raw)
    {
        // Only 0x00, 0x01, and 0x0A are real RType discriminants in the 0x00..0x0F nibble;
        // the doc comment describes the whole range as "MBP levels size" but 13 of the 16
        // values have no defined variant and must be rejected, not treated as valid.
        Assert.False(EnumValues.TryFromRType(raw, out RType value));
        Assert.Equal(default, value);
    }

    [Fact]
    public void RType_TryFrom_RejectsAnyOtherUndefinedByte()
    {
        Assert.False(EnumValues.TryFromRType((byte)0xFF, out RType value));
        Assert.Equal(default, value);
    }

    [Theory]
    [InlineData((byte)'A', Side.Ask)]
    [InlineData((byte)'B', Side.Bid)]
    [InlineData((byte)'N', Side.None)]
    public void Side_TryFrom_AcceptsExactlyTheDefinedDiscriminants(byte raw, Side expected)
    {
        Assert.True(EnumValues.TryFromSide(raw, out Side value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void Side_TryFrom_RejectsUndefinedByte()
    {
        Assert.False(EnumValues.TryFromSide((byte)'Z', out Side value));
        Assert.Equal(default, value);
    }

    [Theory]
    [InlineData(0, SType.InstrumentId)]
    [InlineData(1, SType.RawSymbol)]
    [InlineData(2, SType.Smart)]
    [InlineData(3, SType.Continuous)]
    [InlineData(4, SType.Parent)]
    [InlineData(5, SType.NasdaqSymbol)]
    [InlineData(6, SType.CmsSymbol)]
    [InlineData(7, SType.Isin)]
    [InlineData(8, SType.UsCode)]
    [InlineData(9, SType.BbgCompId)]
    [InlineData(10, SType.BbgCompTicker)]
    [InlineData(11, SType.Figi)]
    [InlineData(12, SType.FigiTicker)]
    [InlineData(13, SType.ListingId)]
    [InlineData(14, SType.IssuerId)]
    [InlineData(15, SType.SecurityId)]
    public void SType_TryFrom_AcceptsExactlyTheDefinedDiscriminants(byte raw, SType expected)
    {
        Assert.True(EnumValues.TryFromSType(raw, out SType value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void SType_TryFrom_RejectsByteAboveRange()
    {
        Assert.False(EnumValues.TryFromSType((byte)16, out SType value));
        Assert.Equal(default, value);
    }

    [Theory]
    [InlineData((ushort)0, Schema.Mbo)]
    [InlineData((ushort)19, Schema.Bbo1M)]
    public void Schema_TryFrom_AcceptsBoundaryDiscriminants(ushort raw, Schema expected)
    {
        Assert.True(EnumValues.TryFromSchema(raw, out Schema value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void Schema_TryFrom_RejectsWordAboveRange()
    {
        Assert.False(EnumValues.TryFromSchema((ushort)20, out Schema value));
        Assert.Equal(default, value);
    }

    [Theory]
    [InlineData((ushort)26, StatType.AuctionCollarLowerPrice)]
    [InlineData((ushort)10001, StatType.VenueSpecificVolume1)]
    [InlineData((ushort)10002, StatType.VenueSpecificPrice1)]
    public void StatType_TryFrom_AcceptsValuesAcrossTheGap(ushort raw, StatType expected)
    {
        Assert.True(EnumValues.TryFromStatType(raw, out StatType value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)27)]
    [InlineData((ushort)10000)]
    [InlineData((ushort)10003)]
    public void StatType_TryFrom_RejectsValuesInsideTheGap(ushort raw)
    {
        // StatType jumps from 26 straight to 10001 — everything in between is undefined,
        // never a dense range.
        Assert.False(EnumValues.TryFromStatType(raw, out StatType value));
        Assert.Equal(default, value);
    }

    [Theory]
    [InlineData((ushort)18, StatusReason.FilingReqsSatisfied)]
    [InlineData((ushort)30, StatusReason.NewsPending)]
    [InlineData((ushort)130, StatusReason.QuotationNotAvailable)]
    public void StatusReason_TryFrom_AcceptsValuesAcrossFamilyGaps(ushort raw, StatusReason expected)
    {
        Assert.True(EnumValues.TryFromStatusReason(raw, out StatusReason value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData((ushort)7)]
    [InlineData((ushort)19)]
    [InlineData((ushort)125)]
    [InlineData((ushort)131)]
    public void StatusReason_TryFrom_RejectsReservedGapValues(ushort raw)
    {
        Assert.False(EnumValues.TryFromStatusReason(raw, out StatusReason value));
        Assert.Equal(default, value);
    }

    [Theory]
    [InlineData((byte)1, ErrorCode.AuthFailed)]
    [InlineData((byte)8, ErrorCode.ReplayDataAgedOut)]
    [InlineData((byte)255, ErrorCode.Unset)]
    public void ErrorCode_TryFrom_AcceptsValuesIncludingTheUnsetSentinel(byte raw, ErrorCode expected)
    {
        Assert.True(EnumValues.TryFromErrorCode(raw, out ErrorCode value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)9)]
    [InlineData((byte)254)]
    public void ErrorCode_TryFrom_RejectsValuesBetweenDefinedRangeAndSentinel(byte raw)
    {
        Assert.False(EnumValues.TryFromErrorCode(raw, out ErrorCode value));
        Assert.Equal(default, value);
    }

    [Theory]
    [InlineData((byte)0, SystemCode.Heartbeat)]
    [InlineData((byte)4, SystemCode.EndOfInterval)]
    [InlineData((byte)255, SystemCode.Unset)]
    public void SystemCode_TryFrom_AcceptsValuesIncludingTheUnsetSentinel(byte raw, SystemCode expected)
    {
        Assert.True(EnumValues.TryFromSystemCode(raw, out SystemCode value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData((byte)5)]
    [InlineData((byte)254)]
    public void SystemCode_TryFrom_RejectsValuesBetweenDefinedRangeAndSentinel(byte raw)
    {
        Assert.False(EnumValues.TryFromSystemCode(raw, out SystemCode value));
        Assert.Equal(default, value);
    }

    [Theory]
    [InlineData((byte)1, VersionUpgradePolicy.AsIs)]
    [InlineData((byte)2, VersionUpgradePolicy.UpgradeToV2)]
    [InlineData((byte)3, VersionUpgradePolicy.UpgradeToV3)]
    public void VersionUpgradePolicy_TryFrom_AcceptsExactlyTheDefinedDiscriminants(byte raw, VersionUpgradePolicy expected)
    {
        Assert.True(EnumValues.TryFromVersionUpgradePolicy(raw, out VersionUpgradePolicy value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void VersionUpgradePolicy_TryFrom_RejectsZero()
    {
        // Upstream's discriminants start at 1 — 0 is not a valid VersionUpgradePolicy.
        Assert.False(EnumValues.TryFromVersionUpgradePolicy((byte)0, out VersionUpgradePolicy value));
        Assert.Equal(default, value);
    }

    [Fact]
    public void FlagSet_EveryRawByteIsValid_NoTryFromNeeded()
    {
        // Unlike every other enum, any u8 is a valid FlagSet in upstream — verify the
        // reserved bit 0 and the full byte both cast cleanly without a validation step.
        var reservedBit = (FlagSet)0x01;
        var allBits = (FlagSet)0xFF;
        Assert.Equal((byte)0x01, (byte)reservedBit);
        Assert.Equal((byte)0xFF, (byte)allBits);
    }

    [Theory]
    [InlineData((byte)'M', Action.Modify)]
    [InlineData((byte)'T', Action.Trade)]
    [InlineData((byte)'F', Action.Fill)]
    [InlineData((byte)'C', Action.Cancel)]
    [InlineData((byte)'A', Action.Add)]
    [InlineData((byte)'R', Action.Clear)]
    [InlineData((byte)'N', Action.None)]
    public void Action_TryFrom_AcceptsExactlyTheDefinedDiscriminants(byte raw, Action expected)
    {
        Assert.True(EnumValues.TryFromAction(raw, out Action value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void Action_TryFrom_RejectsPlausibleButUndefinedByte()
    {
        // 'D' is a plausible action-sounding byte (Delete) — SecurityUpdateAction defines it,
        // Action does not.
        Assert.False(EnumValues.TryFromAction((byte)'D', out Action value));
        Assert.Equal(default, value);
    }

    [Theory]
    [InlineData((byte)'B', InstrumentClass.Bond)]
    [InlineData((byte)'C', InstrumentClass.Call)]
    [InlineData((byte)'F', InstrumentClass.Future)]
    [InlineData((byte)'I', InstrumentClass.Index)]
    [InlineData((byte)'K', InstrumentClass.Stock)]
    [InlineData((byte)'M', InstrumentClass.MixedSpread)]
    [InlineData((byte)'P', InstrumentClass.Put)]
    [InlineData((byte)'S', InstrumentClass.FutureSpread)]
    [InlineData((byte)'T', InstrumentClass.OptionSpread)]
    [InlineData((byte)'X', InstrumentClass.FxSpot)]
    [InlineData((byte)'Y', InstrumentClass.CommoditySpot)]
    public void InstrumentClass_TryFrom_AcceptsExactlyTheDefinedDiscriminants(byte raw, InstrumentClass expected)
    {
        Assert.True(EnumValues.TryFromInstrumentClass(raw, out InstrumentClass value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void InstrumentClass_TryFrom_RejectsPlausibleButUndefinedByte()
    {
        // 'A' is a plausible instrument-class-sounding byte (Add) — Action and
        // SecurityUpdateAction both define it, InstrumentClass does not.
        Assert.False(EnumValues.TryFromInstrumentClass((byte)'A', out InstrumentClass value));
        Assert.Equal(default, value);
    }

    [Theory]
    [InlineData((byte)' ', MatchAlgorithm.Undefined)]
    [InlineData((byte)'F', MatchAlgorithm.Fifo)]
    [InlineData((byte)'K', MatchAlgorithm.Configurable)]
    [InlineData((byte)'C', MatchAlgorithm.ProRata)]
    [InlineData((byte)'T', MatchAlgorithm.FifoLmm)]
    [InlineData((byte)'O', MatchAlgorithm.ThresholdProRata)]
    [InlineData((byte)'S', MatchAlgorithm.FifoTopLmm)]
    [InlineData((byte)'Q', MatchAlgorithm.ThresholdProRataLmm)]
    [InlineData((byte)'Y', MatchAlgorithm.EurodollarFutures)]
    [InlineData((byte)'P', MatchAlgorithm.TimeProRata)]
    [InlineData((byte)'V', MatchAlgorithm.InstitutionalPrioritization)]
    [InlineData((byte)'A', MatchAlgorithm.Allocation)]
    public void MatchAlgorithm_TryFrom_AcceptsExactlyTheDefinedDiscriminants(byte raw, MatchAlgorithm expected)
    {
        Assert.True(EnumValues.TryFromMatchAlgorithm(raw, out MatchAlgorithm value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void MatchAlgorithm_TryFrom_RejectsPlausibleButUndefinedByte()
    {
        // 'N' is a plausible match-algorithm-sounding byte (None) — Side and Action both use
        // 'N' for their "none" variant, MatchAlgorithm does not define 'N' at all.
        Assert.False(EnumValues.TryFromMatchAlgorithm((byte)'N', out MatchAlgorithm value));
        Assert.Equal(default, value);
    }

    [Theory]
    [InlineData((byte)'N', UserDefinedInstrument.No)]
    [InlineData((byte)'Y', UserDefinedInstrument.Yes)]
    public void UserDefinedInstrument_TryFrom_AcceptsExactlyTheDefinedDiscriminants(byte raw, UserDefinedInstrument expected)
    {
        Assert.True(EnumValues.TryFromUserDefinedInstrument(raw, out UserDefinedInstrument value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void UserDefinedInstrument_TryFrom_RejectsPlausibleButUndefinedByte()
    {
        // 'M' (Modify) is a defined byte elsewhere (Action, SecurityUpdateAction) but not here.
        Assert.False(EnumValues.TryFromUserDefinedInstrument((byte)'M', out UserDefinedInstrument value));
        Assert.Equal(default, value);
    }

    [Theory]
    [InlineData((byte)'A', SecurityUpdateAction.Add)]
    [InlineData((byte)'M', SecurityUpdateAction.Modify)]
    [InlineData((byte)'D', SecurityUpdateAction.Delete)]
    [InlineData((byte)'~', SecurityUpdateAction.Invalid)]
    public void SecurityUpdateAction_TryFrom_AcceptsExactlyTheDefinedDiscriminants(byte raw, SecurityUpdateAction expected)
    {
        Assert.True(EnumValues.TryFromSecurityUpdateAction(raw, out SecurityUpdateAction value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void SecurityUpdateAction_TryFrom_RejectsPlausibleButUndefinedByte()
    {
        // 'C' (Cancel) is a defined byte elsewhere (Action) but not here.
        Assert.False(EnumValues.TryFromSecurityUpdateAction((byte)'C', out SecurityUpdateAction value));
        Assert.Equal(default, value);
    }

    [Theory]
    [InlineData((byte)0, Encoding.Dbn)]
    [InlineData((byte)1, Encoding.Csv)]
    [InlineData((byte)2, Encoding.Json)]
    public void Encoding_TryFrom_AcceptsExactlyTheDefinedDiscriminants(byte raw, Encoding expected)
    {
        Assert.True(EnumValues.TryFromEncoding(raw, out Encoding value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void Encoding_TryFrom_RejectsByteOnePastTheDefinedRange()
    {
        Assert.False(EnumValues.TryFromEncoding((byte)3, out Encoding value));
        Assert.Equal(default, value);
    }

    [Theory]
    [InlineData((byte)0, Compression.None)]
    [InlineData((byte)1, Compression.Zstd)]
    public void Compression_TryFrom_AcceptsExactlyTheDefinedDiscriminants(byte raw, Compression expected)
    {
        Assert.True(EnumValues.TryFromCompression(raw, out Compression value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void Compression_TryFrom_RejectsByteOnePastTheDefinedRange()
    {
        Assert.False(EnumValues.TryFromCompression((byte)2, out Compression value));
        Assert.Equal(default, value);
    }

    [Theory]
    [InlineData((byte)1, StatUpdateAction.New)]
    [InlineData((byte)2, StatUpdateAction.Delete)]
    public void StatUpdateAction_TryFrom_AcceptsExactlyTheDefinedDiscriminants(byte raw, StatUpdateAction expected)
    {
        Assert.True(EnumValues.TryFromStatUpdateAction(raw, out StatUpdateAction value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)3)]
    public void StatUpdateAction_TryFrom_RejectsValuesJustOutsideTheDefinedRange(byte raw)
    {
        // Upstream's discriminants start at 1, not 0 — reject both ends of an off-by-one.
        Assert.False(EnumValues.TryFromStatUpdateAction(raw, out StatUpdateAction value));
        Assert.Equal(default, value);
    }

    [Theory]
    [InlineData((ushort)0, StatusAction.None)]
    [InlineData((ushort)1, StatusAction.PreOpen)]
    [InlineData((ushort)2, StatusAction.PreCross)]
    [InlineData((ushort)3, StatusAction.Quoting)]
    [InlineData((ushort)4, StatusAction.Cross)]
    [InlineData((ushort)5, StatusAction.Rotation)]
    [InlineData((ushort)6, StatusAction.NewPriceIndication)]
    [InlineData((ushort)7, StatusAction.Trading)]
    [InlineData((ushort)8, StatusAction.Halt)]
    [InlineData((ushort)9, StatusAction.Pause)]
    [InlineData((ushort)10, StatusAction.Suspend)]
    [InlineData((ushort)11, StatusAction.PreClose)]
    [InlineData((ushort)12, StatusAction.Close)]
    [InlineData((ushort)13, StatusAction.PostClose)]
    [InlineData((ushort)14, StatusAction.SsrChange)]
    [InlineData((ushort)15, StatusAction.NotAvailableForTrading)]
    public void StatusAction_TryFrom_AcceptsExactlyTheDefinedDiscriminants(ushort raw, StatusAction expected)
    {
        Assert.True(EnumValues.TryFromStatusAction(raw, out StatusAction value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void StatusAction_TryFrom_RejectsWordOnePastTheDefinedRange()
    {
        Assert.False(EnumValues.TryFromStatusAction((ushort)16, out StatusAction value));
        Assert.Equal(default, value);
    }

    [Theory]
    [InlineData((ushort)0, TradingEvent.None)]
    [InlineData((ushort)1, TradingEvent.NoCancel)]
    [InlineData((ushort)2, TradingEvent.ChangeTradingSession)]
    [InlineData((ushort)3, TradingEvent.ImpliedMatchingOn)]
    [InlineData((ushort)4, TradingEvent.ImpliedMatchingOff)]
    public void TradingEvent_TryFrom_AcceptsExactlyTheDefinedDiscriminants(ushort raw, TradingEvent expected)
    {
        Assert.True(EnumValues.TryFromTradingEvent(raw, out TradingEvent value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TradingEvent_TryFrom_RejectsWordOnePastTheDefinedRange()
    {
        Assert.False(EnumValues.TryFromTradingEvent((ushort)5, out TradingEvent value));
        Assert.Equal(default, value);
    }

    [Theory]
    [InlineData((byte)'~', TriState.NotAvailable)]
    [InlineData((byte)'N', TriState.No)]
    [InlineData((byte)'Y', TriState.Yes)]
    public void TriState_TryFrom_AcceptsExactlyTheDefinedDiscriminants(byte raw, TriState expected)
    {
        Assert.True(EnumValues.TryFromTriState(raw, out TriState value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TriState_TryFrom_RejectsPlausibleButUndefinedByte()
    {
        // 'T' reads as "True" but TriState's actual "true" byte is 'Y' (Yes) — 'T' is not
        // a defined discriminant.
        Assert.False(EnumValues.TryFromTriState((byte)'T', out TriState value));
        Assert.Equal(default, value);
    }
}
