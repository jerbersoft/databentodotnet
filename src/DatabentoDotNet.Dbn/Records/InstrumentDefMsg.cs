using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// The definition of an instrument. The record of the <see cref="Schema.Definition"/> schema, and
/// the largest record in DBN at 520 bytes.
/// </summary>
/// <remarks>
/// <para>
/// This is the DBN v3 layout. Versions 1 and 2 are 360 and 400 bytes and differ in more than
/// size — see <see cref="InstrumentDefMsgV1"/> and <see cref="InstrumentDefMsgV2"/>, and
/// <see cref="InstrumentDefMsgV1.UpgradeTo"/> for how the gap is closed.
/// </para>
/// <para>
/// Field order is transcribed from the <c>#[repr(C)]</c> Rust declaration. This struct is where
/// that matters most: it carries 42 <c>encode_order</c> attributes, all of which control CSV
/// and JSON column order only and none of which affect memory layout. A field list built from them
/// would compile, satisfy a size assertion, and decode garbage.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct InstrumentDefMsg : IRecord<InstrumentDefMsg>
{
    /// <summary>The common header.</summary>
    public readonly RecordHeader Header;

    /// <summary>
    /// The capture-server-received timestamp, in nanoseconds since the UNIX epoch. This, not
    /// <see cref="RecordHeader.TsEvent"/>, is the record's index timestamp.
    /// </summary>
    public readonly ulong TsRecv;

    /// <summary>
    /// The minimum constant tick for the instrument, where every 1 unit corresponds to 1e-9.
    /// </summary>
    public readonly long MinPriceIncrement;

    /// <summary>
    /// The multiplier converting the venue's display price to the conventional price, where every 1
    /// unit corresponds to 1e-9.
    /// </summary>
    public readonly long DisplayFactor;

    /// <summary>
    /// The last eligible trade time, in nanoseconds since the UNIX epoch.
    /// <see cref="DbnConstants.UndefTimestamp"/> when null, as for equities.
    /// </summary>
    public readonly ulong Expiration;

    /// <summary>
    /// The time of instrument activation, in nanoseconds since the UNIX epoch.
    /// <see cref="DbnConstants.UndefTimestamp"/> when null, as for equities.
    /// </summary>
    public readonly ulong Activation;

    /// <summary>
    /// The allowable high limit price for the trading day, where every 1 unit corresponds to 1e-9.
    /// </summary>
    public readonly long HighLimitPrice;

    /// <summary>
    /// The allowable low limit price for the trading day, where every 1 unit corresponds to 1e-9.
    /// </summary>
    public readonly long LowLimitPrice;

    /// <summary>
    /// The differential value for price banding, where every 1 unit corresponds to 1e-9.
    /// </summary>
    public readonly long MaxPriceVariation;

    /// <summary>
    /// The contract size for each instrument, paired with <see cref="UnitOfMeasure"/>, where every
    /// 1 unit corresponds to 1e-9.
    /// </summary>
    public readonly long UnitOfMeasureQty;

    /// <summary>
    /// The minimum price increment amount disseminated by the venue, where every 1 unit corresponds
    /// to 1e-9.
    /// </summary>
    public readonly long MinPriceIncrementAmount;

    /// <summary>
    /// The value used for price calculation in spread and leg pricing, where every 1 unit
    /// corresponds to 1e-9.
    /// </summary>
    public readonly long PriceRatio;

    /// <summary>The strike price of the option, where every 1 unit corresponds to 1e-9.</summary>
    public readonly long StrikePrice;

    /// <summary>
    /// The instrument ID assigned by the publisher, which may be the same as
    /// <see cref="RecordHeader.InstrumentId"/>.
    /// </summary>
    public readonly ulong RawInstrumentId;

    /// <summary>
    /// The tied price of the leg, if any, where every 1 unit corresponds to 1e-9. New in DBN v3.
    /// </summary>
    public readonly long LegPrice;

    /// <summary>
    /// The associated delta of the leg, if any, where every 1 unit corresponds to 1e-9. New in DBN
    /// v3.
    /// </summary>
    public readonly long LegDelta;

    /// <summary>Venue-specific instrument attributes.</summary>
    public readonly int InstAttribValue;

    /// <summary>The instrument ID of the first underlying instrument.</summary>
    public readonly uint UnderlyingId;

    /// <summary>The implied book depth on the price-level data feed.</summary>
    public readonly int MarketDepthImplied;

    /// <summary>The outright book depth on the price-level data feed.</summary>
    public readonly int MarketDepth;

    /// <summary>The market segment of the instrument.</summary>
    public readonly uint MarketSegmentId;

    /// <summary>The maximum trading volume for the instrument.</summary>
    public readonly uint MaxTradeVol;

    /// <summary>The minimum order entry quantity for the instrument.</summary>
    public readonly int MinLotSize;

    /// <summary>The minimum quantity required for a block trade of the instrument.</summary>
    public readonly int MinLotSizeBlock;

    /// <summary>
    /// The minimum quantity required for a round lot. Multiples of this quantity are round lots
    /// too.
    /// </summary>
    public readonly int MinLotSizeRoundLot;

    /// <summary>The minimum trading volume for the instrument.</summary>
    public readonly uint MinTradeVol;

    /// <summary>The number of deliverables per instrument, i.e. peak days.</summary>
    public readonly int ContractMultiplier;

    /// <summary>
    /// The quantity a contract decays by daily once <see cref="DecayStartDate"/> is reached.
    /// </summary>
    public readonly int DecayQuantity;

    /// <summary>The fixed contract value assigned to each instrument.</summary>
    public readonly int OriginalContractSize;

    /// <summary>The numeric ID assigned to the leg instrument. New in DBN v3.</summary>
    public readonly uint LegInstrumentId;

    /// <summary>The numerator of the leg's price ratio within the spread. New in DBN v3.</summary>
    public readonly int LegRatioPriceNumerator;

    /// <summary>
    /// The denominator of the leg's price ratio within the spread. New in DBN v3.
    /// </summary>
    public readonly int LegRatioPriceDenominator;

    /// <summary>
    /// The numerator of the leg's quantity ratio within the spread. New in DBN v3.
    /// </summary>
    public readonly int LegRatioQtyNumerator;

    /// <summary>
    /// The denominator of the leg's quantity ratio within the spread. New in DBN v3.
    /// </summary>
    public readonly int LegRatioQtyDenominator;

    /// <summary>
    /// The numeric ID of the leg instrument's underlying instrument. New in DBN v3.
    /// </summary>
    public readonly uint LegUnderlyingId;

    /// <summary>The channel ID assigned at the venue.</summary>
    public readonly short ApplId;

    /// <summary>The calendar year reflected in the instrument symbol.</summary>
    public readonly ushort MaturityYear;

    /// <summary>The date at which a contract begins to decay.</summary>
    public readonly ushort DecayStartDate;

    /// <summary>The channel ID assigned by Databento, an integer counting up from zero.</summary>
    public readonly ushort ChannelId;

    /// <summary>
    /// The number of legs in the strategy or spread; 0 for outrights. New in DBN v3.
    /// </summary>
    public readonly ushort LegCount;

    /// <summary>The 0-based index of the leg. New in DBN v3.</summary>
    public readonly ushort LegIndex;

    /// <summary>The currency used for price fields.</summary>
    public readonly CStr4 Currency;

    /// <summary>
    /// The currency used for settlement, if different from <see cref="Currency"/>.
    /// </summary>
    public readonly CStr4 SettlCurrency;

    /// <summary>The strategy type of the spread.</summary>
    public readonly CStr6 SecSubType;

    /// <summary>The instrument raw symbol assigned by the publisher.</summary>
    public readonly CStr71 RawSymbol;

    /// <summary>The security group code of the instrument.</summary>
    public readonly CStr21 Group;

    /// <summary>The exchange used to identify the instrument.</summary>
    public readonly CStr5 Exchange;

    /// <summary>The underlying asset code (product code) of the instrument.</summary>
    public readonly CStr11 Asset;

    /// <summary>The ISO standard instrument categorization code.</summary>
    public readonly CStr7 Cfi;

    /// <summary>
    /// The security type of the instrument, e.g. <c>FUT</c> for a future or future spread.
    /// </summary>
    public readonly CStr7 SecurityType;

    /// <summary>
    /// The unit of measure for the instrument's original contract size, e.g. <c>USD</c> or
    /// <c>LBS</c>.
    /// </summary>
    public readonly CStr31 UnitOfMeasure;

    /// <summary>The symbol of the first underlying instrument.</summary>
    public readonly CStr21 Underlying;

    /// <summary>The currency of <see cref="StrikePrice"/>.</summary>
    public readonly CStr4 StrikePriceCurrency;

    /// <summary>The leg instrument's raw symbol assigned by the publisher. New in DBN v3.</summary>
    public readonly CStr71 LegRawSymbol;

    /// <summary>
    /// The raw wire byte behind <see cref="InstrumentClass"/>: the classification of the
    /// instrument. Not validated on decode — pass it to
    /// <see cref="EnumValues.TryFromInstrumentClass(byte, out InstrumentClass)"/> for a checked
    /// conversion.
    /// </summary>
    public readonly byte RawInstrumentClass;

    /// <summary>
    /// The raw wire byte behind <see cref="MatchAlgorithm"/>: the matching algorithm used for the
    /// instrument, typically FIFO. Not validated on decode — pass it to
    /// <see cref="EnumValues.TryFromMatchAlgorithm(byte, out MatchAlgorithm)"/> for a checked
    /// conversion.
    /// </summary>
    public readonly byte RawMatchAlgorithm;

    /// <summary>The price denominator of the main fraction.</summary>
    public readonly byte MainFraction;

    /// <summary>
    /// The number of digits to the right of the tick mark, for displaying fractional prices.
    /// </summary>
    public readonly byte PriceDisplayFormat;

    /// <summary>The price denominator of the sub fraction.</summary>
    public readonly byte SubFraction;

    /// <summary>The product complex of the instrument.</summary>
    public readonly byte UnderlyingProduct;

    /// <summary>
    /// The raw wire byte behind <see cref="SecurityUpdateAction"/>: whether the instrument
    /// definition was added, modified, or deleted. Not validated on decode — pass it to
    /// <see cref="EnumValues.TryFromSecurityUpdateAction(byte, out SecurityUpdateAction)"/> for a
    /// checked conversion.
    /// </summary>
    public readonly byte RawSecurityUpdateAction;

    /// <summary>The calendar month reflected in the instrument symbol.</summary>
    public readonly byte MaturityMonth;

    /// <summary>The calendar day reflected in the instrument symbol, or 0.</summary>
    public readonly byte MaturityDay;

    /// <summary>The calendar week reflected in the instrument symbol, or 0.</summary>
    public readonly byte MaturityWeek;

    /// <summary>
    /// The raw wire byte behind <see cref="UserDefinedInstrument"/>: whether the instrument is user
    /// defined. Not validated on decode — pass it to
    /// <see cref="EnumValues.TryFromUserDefinedInstrument(byte, out UserDefinedInstrument)"/> for a
    /// checked conversion.
    /// </summary>
    public readonly byte RawUserDefinedInstrument;

    /// <summary>The type of <see cref="ContractMultiplier"/>: 1 for hours, 2 for days.</summary>
    public readonly sbyte ContractMultiplierUnit;

    /// <summary>The schedule for delivering electricity.</summary>
    public readonly sbyte FlowScheduleType;

    /// <summary>The tick rule of the spread.</summary>
    public readonly byte TickRule;

    /// <summary>
    /// The raw wire byte behind <see cref="LegInstrumentClass"/>: the classification of the leg
    /// instrument. New in DBN v3. Not validated on decode — pass it to
    /// <see cref="EnumValues.TryFromInstrumentClass(byte, out InstrumentClass)"/> for a checked
    /// conversion.
    /// </summary>
    public readonly byte RawLegInstrumentClass;

    /// <summary>
    /// The raw wire byte behind <see cref="LegSide"/>: the side taken for the leg when purchasing
    /// the spread. New in DBN v3. Not validated on decode — pass it to
    /// <see cref="EnumValues.TryFromSide(byte, out Side)"/> for a checked conversion.
    /// </summary>
    public readonly byte RawLegSide;

    private readonly ReservedBytes17 _reserved;

    /// <summary>The classification of the instrument, as its raw ASCII character.</summary>
    public char InstrumentClassChar => (char)RawInstrumentClass;

    /// <summary>
    /// The classification of the instrument. Undefined wire bytes cast through to an unnamed value
    /// rather than throwing; see <see cref="RawInstrumentClass"/>.
    /// </summary>
    public InstrumentClass InstrumentClass => (InstrumentClass)RawInstrumentClass;

    /// <summary>
    /// The matching algorithm used for the instrument, typically FIFO, as its raw ASCII character.
    /// </summary>
    public char MatchAlgorithmChar => (char)RawMatchAlgorithm;

    /// <summary>
    /// The matching algorithm used for the instrument, typically FIFO. Undefined wire bytes cast
    /// through to an unnamed value rather than throwing; see <see cref="RawMatchAlgorithm"/>.
    /// </summary>
    public MatchAlgorithm MatchAlgorithm => (MatchAlgorithm)RawMatchAlgorithm;

    /// <summary>
    /// Whether the instrument definition was added, modified, or deleted, as its raw ASCII
    /// character.
    /// </summary>
    public char SecurityUpdateActionChar => (char)RawSecurityUpdateAction;

    /// <summary>
    /// Whether the instrument definition was added, modified, or deleted. Undefined wire bytes cast
    /// through to an unnamed value rather than throwing; see <see cref="RawSecurityUpdateAction"/>.
    /// </summary>
    public SecurityUpdateAction SecurityUpdateAction =>
        (SecurityUpdateAction)RawSecurityUpdateAction;

    /// <summary>Whether the instrument is user defined, as its raw ASCII character.</summary>
    public char UserDefinedInstrumentChar => (char)RawUserDefinedInstrument;

    /// <summary>
    /// Whether the instrument is user defined. Undefined wire bytes cast through to an unnamed
    /// value rather than throwing; see <see cref="RawUserDefinedInstrument"/>.
    /// </summary>
    public UserDefinedInstrument UserDefinedInstrument =>
        (UserDefinedInstrument)RawUserDefinedInstrument;

    /// <summary>
    /// The classification of the leg instrument, as its raw ASCII character. New in DBN v3.
    /// </summary>
    public char LegInstrumentClassChar => (char)RawLegInstrumentClass;

    /// <summary>
    /// The classification of the leg instrument. New in DBN v3. Undefined wire bytes cast through
    /// to an unnamed value rather than throwing; see <see cref="RawLegInstrumentClass"/>.
    /// </summary>
    public InstrumentClass LegInstrumentClass => (InstrumentClass)RawLegInstrumentClass;

    /// <summary>
    /// The side taken for the leg when purchasing the spread, as its raw ASCII character. New in
    /// DBN v3.
    /// </summary>
    public char LegSideChar => (char)RawLegSide;

    /// <summary>
    /// The side taken for the leg when purchasing the spread. New in DBN v3. Undefined wire bytes
    /// cast through to an unnamed value rather than throwing; see <see cref="RawLegSide"/>.
    /// </summary>
    public Side LegSide => (Side)RawLegSide;

    /// <inheritdoc/>
    public static bool HasRType(RType rtype) => rtype == RType.InstrumentDef;

    /// <inheritdoc/>
    public static int WireSize => Unsafe.SizeOf<InstrumentDefMsg>();

    /// <summary>
    /// Upgrades a DBN v1 instrument definition to this, the v3 layout. See
    /// <see cref="InstrumentDefMsgV1.UpgradeTo"/>.
    /// </summary>
    /// <param name="old">The record to upgrade.</param>
    internal InstrumentDefMsg(in InstrumentDefMsgV1 old)
    {
        // Assignments are in this struct's declaration order, so the list below reads as a
        // straight diff against the source layout. Every field the source does not have takes
        // the value upstream's InstrumentDefMsg::default() gives it, which is NOT uniformly
        // zero. Nothing can be silently skipped: a constructor must definitely-assign every
        // field, so the compiler is what enforces completeness here.
        Header = new RecordHeader(
            RType.InstrumentDef,
            WireSize,
            old.Header.PublisherId,
            old.Header.InstrumentId,
            old.Header.TsEvent);

        TsRecv = old.TsRecv;
        MinPriceIncrement = old.MinPriceIncrement;
        DisplayFactor = old.DisplayFactor;
        Expiration = old.Expiration;
        Activation = old.Activation;
        HighLimitPrice = old.HighLimitPrice;
        LowLimitPrice = old.LowLimitPrice;
        MaxPriceVariation = old.MaxPriceVariation;
        UnitOfMeasureQty = old.UnitOfMeasureQty;
        MinPriceIncrementAmount = old.MinPriceIncrementAmount;
        PriceRatio = old.PriceRatio;
        StrikePrice = old.StrikePrice;
        // Widened from 32-bit in v1 and v2. A plain widening is correct here: this field has no
        // sentinel, unlike StatMsg.Quantity.
        RawInstrumentId = old.RawInstrumentId;
        // New in v3: the price sentinel, not zero. Zero is a real price, so defaulting a leg
        // price to zero would invent a fact rather than admit an absence.
        LegPrice = DbnConstants.UndefPrice;
        LegDelta = DbnConstants.UndefPrice;
        InstAttribValue = old.InstAttribValue;
        UnderlyingId = old.UnderlyingId;
        MarketDepthImplied = old.MarketDepthImplied;
        MarketDepth = old.MarketDepth;
        MarketSegmentId = old.MarketSegmentId;
        MaxTradeVol = old.MaxTradeVol;
        MinLotSize = old.MinLotSize;
        MinLotSizeBlock = old.MinLotSizeBlock;
        MinLotSizeRoundLot = old.MinLotSizeRoundLot;
        MinTradeVol = old.MinTradeVol;
        ContractMultiplier = old.ContractMultiplier;
        DecayQuantity = old.DecayQuantity;
        OriginalContractSize = old.OriginalContractSize;
        LegInstrumentId = 0;
        LegRatioPriceNumerator = 0;
        LegRatioPriceDenominator = 0;
        LegRatioQtyNumerator = 0;
        LegRatioQtyDenominator = 0;
        LegUnderlyingId = 0;
        ApplId = old.ApplId;
        MaturityYear = old.MaturityYear;
        DecayStartDate = old.DecayStartDate;
        ChannelId = old.ChannelId;
        LegCount = 0;
        LegIndex = 0;
        Currency = old.Currency;
        SettlCurrency = old.SettlCurrency;
        SecSubType = old.SecSubType;
        // Grew from 22 bytes in v1. Copy the old bytes, NUL padding included, and leave the rest
        // of the wider buffer zeroed.
        var rawSymbol = default(CStr71);
        old.RawSymbol.AsSpan().CopyTo(rawSymbol);
        RawSymbol = rawSymbol;
        Group = old.Group;
        Exchange = old.Exchange;
        // Grew from 7 bytes in v1 and v2. Same treatment as RawSymbol above.
        var asset = default(CStr11);
        old.Asset.AsSpan().CopyTo(asset);
        Asset = asset;
        Cfi = old.Cfi;
        SecurityType = old.SecurityType;
        UnitOfMeasure = old.UnitOfMeasure;
        Underlying = old.Underlying;
        StrikePriceCurrency = old.StrikePriceCurrency;
        LegRawSymbol = default;
        RawInstrumentClass = old.RawInstrumentClass;
        RawMatchAlgorithm = old.RawMatchAlgorithm;
        MainFraction = old.MainFraction;
        PriceDisplayFormat = old.PriceDisplayFormat;
        SubFraction = old.SubFraction;
        UnderlyingProduct = old.UnderlyingProduct;
        RawSecurityUpdateAction = old.RawSecurityUpdateAction;
        MaturityMonth = old.MaturityMonth;
        MaturityDay = old.MaturityDay;
        MaturityWeek = old.MaturityWeek;
        RawUserDefinedInstrument = old.RawUserDefinedInstrument;
        ContractMultiplierUnit = old.ContractMultiplierUnit;
        FlowScheduleType = old.FlowScheduleType;
        TickRule = old.TickRule;
        // A raw zero byte, deliberately not a valid InstrumentClass discriminant: there
        // is no leg, so there is no class to report. Upstream defaults it the same way.
        RawLegInstrumentClass = 0;
        // Side, unlike InstrumentClass, has a meaningful 'none' discriminant, so upstream
        // defaults this one to a valid value rather than a raw zero.
        RawLegSide = (byte)Side.None;
        _reserved = default;
    }

    /// <summary>
    /// Upgrades a DBN v2 instrument definition to this, the v3 layout. See
    /// <see cref="InstrumentDefMsgV2.UpgradeTo"/>.
    /// </summary>
    /// <param name="old">The record to upgrade.</param>
    internal InstrumentDefMsg(in InstrumentDefMsgV2 old)
    {
        // Assignments are in this struct's declaration order, so the list below reads as a
        // straight diff against the source layout. Every field the source does not have takes
        // the value upstream's InstrumentDefMsg::default() gives it, which is NOT uniformly
        // zero. Nothing can be silently skipped: a constructor must definitely-assign every
        // field, so the compiler is what enforces completeness here.
        Header = new RecordHeader(
            RType.InstrumentDef,
            WireSize,
            old.Header.PublisherId,
            old.Header.InstrumentId,
            old.Header.TsEvent);

        TsRecv = old.TsRecv;
        MinPriceIncrement = old.MinPriceIncrement;
        DisplayFactor = old.DisplayFactor;
        Expiration = old.Expiration;
        Activation = old.Activation;
        HighLimitPrice = old.HighLimitPrice;
        LowLimitPrice = old.LowLimitPrice;
        MaxPriceVariation = old.MaxPriceVariation;
        UnitOfMeasureQty = old.UnitOfMeasureQty;
        MinPriceIncrementAmount = old.MinPriceIncrementAmount;
        PriceRatio = old.PriceRatio;
        StrikePrice = old.StrikePrice;
        // Widened from 32-bit in v1 and v2. A plain widening is correct here: this field has no
        // sentinel, unlike StatMsg.Quantity.
        RawInstrumentId = old.RawInstrumentId;
        // New in v3: the price sentinel, not zero. Zero is a real price, so defaulting a leg
        // price to zero would invent a fact rather than admit an absence.
        LegPrice = DbnConstants.UndefPrice;
        LegDelta = DbnConstants.UndefPrice;
        InstAttribValue = old.InstAttribValue;
        UnderlyingId = old.UnderlyingId;
        MarketDepthImplied = old.MarketDepthImplied;
        MarketDepth = old.MarketDepth;
        MarketSegmentId = old.MarketSegmentId;
        MaxTradeVol = old.MaxTradeVol;
        MinLotSize = old.MinLotSize;
        MinLotSizeBlock = old.MinLotSizeBlock;
        MinLotSizeRoundLot = old.MinLotSizeRoundLot;
        MinTradeVol = old.MinTradeVol;
        ContractMultiplier = old.ContractMultiplier;
        DecayQuantity = old.DecayQuantity;
        OriginalContractSize = old.OriginalContractSize;
        LegInstrumentId = 0;
        LegRatioPriceNumerator = 0;
        LegRatioPriceDenominator = 0;
        LegRatioQtyNumerator = 0;
        LegRatioQtyDenominator = 0;
        LegUnderlyingId = 0;
        ApplId = old.ApplId;
        MaturityYear = old.MaturityYear;
        DecayStartDate = old.DecayStartDate;
        ChannelId = old.ChannelId;
        LegCount = 0;
        LegIndex = 0;
        Currency = old.Currency;
        SettlCurrency = old.SettlCurrency;
        SecSubType = old.SecSubType;
        RawSymbol = old.RawSymbol;
        Group = old.Group;
        Exchange = old.Exchange;
        // Grew from 7 bytes in v1 and v2. Same treatment as RawSymbol above.
        var asset = default(CStr11);
        old.Asset.AsSpan().CopyTo(asset);
        Asset = asset;
        Cfi = old.Cfi;
        SecurityType = old.SecurityType;
        UnitOfMeasure = old.UnitOfMeasure;
        Underlying = old.Underlying;
        StrikePriceCurrency = old.StrikePriceCurrency;
        LegRawSymbol = default;
        RawInstrumentClass = old.RawInstrumentClass;
        RawMatchAlgorithm = old.RawMatchAlgorithm;
        MainFraction = old.MainFraction;
        PriceDisplayFormat = old.PriceDisplayFormat;
        SubFraction = old.SubFraction;
        UnderlyingProduct = old.UnderlyingProduct;
        RawSecurityUpdateAction = old.RawSecurityUpdateAction;
        MaturityMonth = old.MaturityMonth;
        MaturityDay = old.MaturityDay;
        MaturityWeek = old.MaturityWeek;
        RawUserDefinedInstrument = old.RawUserDefinedInstrument;
        ContractMultiplierUnit = old.ContractMultiplierUnit;
        FlowScheduleType = old.FlowScheduleType;
        TickRule = old.TickRule;
        // A raw zero byte, deliberately not a valid InstrumentClass discriminant: there
        // is no leg, so there is no class to report. Upstream defaults it the same way.
        RawLegInstrumentClass = 0;
        // Side, unlike InstrumentClass, has a meaningful 'none' discriminant, so upstream
        // defaults this one to a valid value rather than a raw zero.
        RawLegSide = (byte)Side.None;
        _reserved = default;
    }
}
