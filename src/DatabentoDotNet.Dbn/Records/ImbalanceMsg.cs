using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// An auction imbalance. The record of the <see cref="Schema.Imbalance"/> schema.
/// </summary>
/// <remarks>
/// Byte-identical in every DBN version, so there is no version-specific variant of it. Field
/// order is transcribed from the <c>#[repr(C)]</c> Rust declaration, not from its
/// <c>encode_order</c> attributes.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct ImbalanceMsg : IRecord<ImbalanceMsg>
{
    /// <summary>The common header.</summary>
    public readonly RecordHeader Header;

    /// <summary>
    /// The capture-server-received timestamp, in nanoseconds since the UNIX epoch. This, not
    /// <see cref="RecordHeader.TsEvent"/>, is the record's index timestamp.
    /// </summary>
    public readonly ulong TsRecv;

    /// <summary>
    /// The price the imbalance shares are calculated at, where every 1 unit corresponds to 1e-9.
    /// <see cref="DbnConstants.UndefPrice"/> when unused.
    /// </summary>
    public readonly long RefPrice;

    /// <summary>
    /// The projected auction timestamp, in nanoseconds since the UNIX epoch.
    /// <see cref="DbnConstants.UndefTimestamp"/> when unused.
    /// </summary>
    public readonly ulong AuctionTime;

    /// <summary>
    /// The hypothetical auction-clearing price for both cross and continuous orders, where every
    /// 1 unit corresponds to 1e-9. <see cref="DbnConstants.UndefPrice"/> when unused.
    /// </summary>
    public readonly long ContBookClrPrice;

    /// <summary>
    /// The hypothetical auction-clearing price for cross orders only, where every 1 unit
    /// corresponds to 1e-9. <see cref="DbnConstants.UndefPrice"/> when unused.
    /// </summary>
    public readonly long AuctInterestClrPrice;

    /// <summary>
    /// The price sell-short interest will be filled at while a short-sell restriction is in
    /// effect, where every 1 unit corresponds to 1e-9.
    /// <see cref="DbnConstants.UndefPrice"/> when unused.
    /// </summary>
    public readonly long SsrFillingPrice;

    /// <summary>
    /// The price at which the highest number of shares would trade, subject to auction collars,
    /// where every 1 unit corresponds to 1e-9. <see cref="DbnConstants.UndefPrice"/> when unused.
    /// </summary>
    public readonly long IndMatchPrice;

    /// <summary>
    /// The upper limit of the auction collar, where every 1 unit corresponds to 1e-9.
    /// <see cref="DbnConstants.UndefPrice"/> when unused.
    /// </summary>
    public readonly long UpperCollar;

    /// <summary>
    /// The lower limit of the auction collar, where every 1 unit corresponds to 1e-9.
    /// <see cref="DbnConstants.UndefPrice"/> when unused.
    /// </summary>
    public readonly long LowerCollar;

    /// <summary>
    /// The quantity of shares eligible to be matched at <see cref="RefPrice"/>.
    /// <see cref="DbnConstants.UndefOrderSize"/> when unused.
    /// </summary>
    public readonly uint PairedQty;

    /// <summary>
    /// The quantity of shares not paired at <see cref="RefPrice"/>.
    /// <see cref="DbnConstants.UndefOrderSize"/> when unused.
    /// </summary>
    public readonly uint TotalImbalanceQty;

    /// <summary>
    /// The total market-order imbalance quantity at <see cref="RefPrice"/>.
    /// <see cref="DbnConstants.UndefOrderSize"/> when unused.
    /// </summary>
    public readonly uint MarketImbalanceQty;

    /// <summary>
    /// During the closing auction, the number of unpaired shares priced at or better than
    /// <see cref="RefPrice"/>. <see cref="DbnConstants.UndefOrderSize"/> when unused.
    /// </summary>
    public readonly uint UnpairedQty;

    /// <summary>
    /// The raw wire byte behind <see cref="AuctionTypeChar"/>: a venue-specific auction type
    /// code, <c>'~'</c> when unused. This one has no enum in the DBN spec — refer to the
    /// venue-specific documentation.
    /// </summary>
    public readonly byte RawAuctionType;

    /// <summary>
    /// The raw wire byte behind <see cref="Side"/>. Not validated on decode — pass it to
    /// <see cref="EnumValues.TryFromSide(byte, out Side)"/> for a checked conversion.
    /// </summary>
    public readonly byte RawSide;

    /// <summary>A venue-specific status code.</summary>
    public readonly byte AuctionStatus;

    /// <summary>A venue-specific status code.</summary>
    public readonly byte FreezeStatus;

    /// <summary>The number of times the halt period has been extended.</summary>
    public readonly byte NumExtensions;

    /// <summary>
    /// The raw wire byte behind <see cref="UnpairedSide"/>. Not validated on decode — pass it to
    /// <see cref="EnumValues.TryFromSide(byte, out Side)"/> for a checked conversion.
    /// </summary>
    public readonly byte RawUnpairedSide;

    /// <summary>
    /// The raw wire byte behind <see cref="SignificantImbalanceChar"/>: a venue-specific code.
    /// For Nasdaq this is the raw Price Variation Indicator. No enum in the DBN spec.
    /// </summary>
    public readonly byte RawSignificantImbalance;

    private readonly ReservedBytes1 _reserved;

    /// <summary>The venue-specific auction type as its raw ASCII character.</summary>
    public char AuctionTypeChar => (char)RawAuctionType;

    /// <summary>
    /// The market side of <see cref="TotalImbalanceQty"/>, as its raw ASCII character.
    /// </summary>
    public char SideChar => (char)RawSide;

    /// <summary>
    /// The market side of <see cref="TotalImbalanceQty"/>. Undefined wire bytes cast through to
    /// an unnamed value rather than throwing; see <see cref="RawSide"/>.
    /// </summary>
    public Side Side => (Side)RawSide;

    /// <summary>The side of <see cref="UnpairedQty"/>, as its raw ASCII character.</summary>
    public char UnpairedSideChar => (char)RawUnpairedSide;

    /// <summary>
    /// The side of <see cref="UnpairedQty"/>. Undefined wire bytes cast through to an unnamed
    /// value rather than throwing; see <see cref="RawUnpairedSide"/>.
    /// </summary>
    public Side UnpairedSide => (Side)RawUnpairedSide;

    /// <summary>The venue-specific significant-imbalance code as its raw ASCII character.</summary>
    public char SignificantImbalanceChar => (char)RawSignificantImbalance;

    /// <inheritdoc/>
    public static bool HasRType(RType rtype) => rtype == RType.Imbalance;

    /// <inheritdoc/>
    public static int WireSize => Unsafe.SizeOf<ImbalanceMsg>();
}
