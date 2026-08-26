using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn;

/// <summary>The DBN v1 layout of <see cref="InstrumentDefMsg"/>, 360 bytes.</summary>
/// <remarks>
/// <para>
/// Version 1 differs from v3 by more than its size. It carries four fields v3 dropped
/// (<see cref="TradingReferencePrice"/>, <see cref="TradingReferenceDate"/>,
/// <see cref="MdSecurityTradingStatus"/> and <see cref="SettlPriceType"/>) and none of the
/// thirteen leg and spread fields v3 added; <see cref="RawInstrumentId"/> is 32-bit rather than
/// 64-bit; <see cref="RawSymbol"/> is 22 bytes rather than 71 and <see cref="Asset"/> 7 rather
/// than 11; and <see cref="StrikePrice"/> sits near the tail of the record rather than among
/// the other price fields at the front.
/// </para>
/// <para>
/// The five reserved blocks are v1's own: <c>_reserved4</c> is what 8-byte-aligns
/// <see cref="StrikePrice"/> at its unusual position, and v2 removed all of them once the field
/// moved.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct InstrumentDefMsgV1 : IRecord<InstrumentDefMsgV1>
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
    /// The trading session close price, where every 1 unit corresponds to 1e-9. Removed in DBN v3.
    /// </summary>
    public readonly long TradingReferencePrice;

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

    /// <summary>Venue-specific instrument attributes.</summary>
    public readonly int InstAttribValue;

    /// <summary>The instrument ID of the first underlying instrument.</summary>
    public readonly uint UnderlyingId;

    /// <summary>
    /// The instrument ID assigned by the publisher, which may be the same as
    /// <see cref="RecordHeader.InstrumentId"/>.
    /// </summary>
    public readonly uint RawInstrumentId;

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

    private readonly ReservedBytes4 _reserved2;

    /// <summary>The number of deliverables per instrument, i.e. peak days.</summary>
    public readonly int ContractMultiplier;

    /// <summary>
    /// The quantity a contract decays by daily once <see cref="DecayStartDate"/> is reached.
    /// </summary>
    public readonly int DecayQuantity;

    /// <summary>The fixed contract value assigned to each instrument.</summary>
    public readonly int OriginalContractSize;

    private readonly ReservedBytes4 _reserved3;

    /// <summary>
    /// The date the <c>TradingReferencePrice</c> is for, as days since the UNIX epoch. Removed in
    /// DBN v3.
    /// </summary>
    public readonly ushort TradingReferenceDate;

    /// <summary>The channel ID assigned at the venue.</summary>
    public readonly short ApplId;

    /// <summary>The calendar year reflected in the instrument symbol.</summary>
    public readonly ushort MaturityYear;

    /// <summary>The date at which a contract begins to decay.</summary>
    public readonly ushort DecayStartDate;

    /// <summary>The channel ID assigned by Databento, an integer counting up from zero.</summary>
    public readonly ushort ChannelId;

    /// <summary>The currency used for price fields.</summary>
    public readonly CStr4 Currency;

    /// <summary>
    /// The currency used for settlement, if different from <see cref="Currency"/>.
    /// </summary>
    public readonly CStr4 SettlCurrency;

    /// <summary>The strategy type of the spread.</summary>
    public readonly CStr6 SecSubType;

    /// <summary>The instrument raw symbol assigned by the publisher.</summary>
    public readonly CStr22 RawSymbol;

    /// <summary>The security group code of the instrument.</summary>
    public readonly CStr21 Group;

    /// <summary>The exchange used to identify the instrument.</summary>
    public readonly CStr5 Exchange;

    /// <summary>The underlying asset code (product code) of the instrument.</summary>
    public readonly CStr7 Asset;

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

    /// <summary>
    /// The raw wire byte behind <see cref="InstrumentClass"/>: the classification of the
    /// instrument. Not validated on decode — pass it to
    /// <see cref="EnumValues.TryFromInstrumentClass(byte, out InstrumentClass)"/> for a checked
    /// conversion.
    /// </summary>
    public readonly byte RawInstrumentClass;

    private readonly ReservedBytes2 _reserved4;

    /// <summary>The strike price of the option, where every 1 unit corresponds to 1e-9.</summary>
    public readonly long StrikePrice;

    private readonly ReservedBytes6 _reserved5;

    /// <summary>
    /// The raw wire byte behind <see cref="MatchAlgorithm"/>: the matching algorithm used for the
    /// instrument, typically FIFO. Not validated on decode — pass it to
    /// <see cref="EnumValues.TryFromMatchAlgorithm(byte, out MatchAlgorithm)"/> for a checked
    /// conversion.
    /// </summary>
    public readonly byte RawMatchAlgorithm;

    /// <summary>The venue's security trading status. Removed in DBN v3.</summary>
    public readonly byte MdSecurityTradingStatus;

    /// <summary>The price denominator of the main fraction.</summary>
    public readonly byte MainFraction;

    /// <summary>
    /// The number of digits to the right of the tick mark, for displaying fractional prices.
    /// </summary>
    public readonly byte PriceDisplayFormat;

    /// <summary>
    /// A bit field indicating how the settlement price was calculated. Removed in DBN v3.
    /// </summary>
    public readonly byte SettlPriceType;

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

    private readonly ReservedBytes3 _dummy;

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

    /// <inheritdoc/>
    public static bool HasRType(RType rtype) => rtype == RType.InstrumentDef;

    /// <inheritdoc/>
    public static int WireSize => Unsafe.SizeOf<InstrumentDefMsgV1>();

    /// <summary>
    /// Converts this record to the current-version <see cref="InstrumentDefMsg"/>.
    /// </summary>
    /// <remarks>
    /// A value-level conversion into larger storage, never an in-place reinterpret: the target is
    /// 520 bytes to this record's 360 and almost nothing after <c>price_ratio</c> is at the same
    /// offset. <see cref="RecordHeader.Length"/> is recomputed for the new size, the four fields
    /// v3 dropped are discarded, and the thirteen fields v3 added take upstream's defaults for
    /// them — which for a price field is <see cref="DbnConstants.UndefPrice"/>, not zero.
    /// </remarks>
    /// <returns>The equivalent v3 record.</returns>
    public InstrumentDefMsg UpgradeTo() => new(in this);
}
