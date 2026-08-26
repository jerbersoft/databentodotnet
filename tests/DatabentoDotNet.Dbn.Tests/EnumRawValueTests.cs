using DatabentoDotNet.Dbn.Enums;

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
}
