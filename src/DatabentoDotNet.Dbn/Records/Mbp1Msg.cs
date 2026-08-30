using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// Market-by-price with a book depth of 1. The record of the <see cref="Schema.Mbp1"/> and
/// <see cref="Schema.Tbbo"/> schemas.
/// </summary>
/// <remarks>
/// Upstream aliases this struct as <c>TbboMsg</c> for the TBBO schema; the two are the same
/// type, not two layouts. Its first 48 bytes are byte-identical to <see cref="TradeMsg"/>.
/// Field order is transcribed from the <c>#[repr(C)]</c> Rust declaration, not from its
/// <c>encode_order</c> attributes.
/// </remarks>
/// <example>
/// <code>
/// if (record.TryGet(out Mbp1Msg quote))
/// {
///     // Levels is an inline array of one, part of the record's own bytes rather than a reference
///     // to somewhere else — which is what lets the whole record be reinterpreted in place.
///     BidAskPair top = quote.Levels[0];
///
///     decimal bid = (decimal)top.BidPx / DbnConstants.FixedPriceScale;
///     decimal ask = (decimal)top.AskPx / DbnConstants.FixedPriceScale;
///
///     // A side with no orders carries UndefPrice, not zero. Dividing it gives a number in the
///     // billions that looks like a price.
///     if (top.BidPx != DbnConstants.UndefPrice &amp;&amp; top.AskPx != DbnConstants.UndefPrice)
///     {
///         Console.WriteLine(
///             $"{DbnTime.ToInstant(quote.IndexTs)} {bid} x {top.BidSz} / {ask} x {top.AskSz}");
///     }
/// }
/// </code>
/// </example>
[StructLayout(LayoutKind.Sequential)]
public readonly struct Mbp1Msg : IRecord<Mbp1Msg>
{
    /// <summary>The common header.</summary>
    public readonly RecordHeader Header;

    /// <summary>The order price, where every 1 unit corresponds to 1e-9.</summary>
    public readonly long Price;

    /// <summary>The order quantity.</summary>
    public readonly uint Size;

    /// <summary>
    /// The raw wire byte behind <see cref="Action"/>. Not validated on decode — pass it to
    /// <see cref="EnumValues.TryFromAction(byte, out Action)"/> for a checked conversion.
    /// </summary>
    public readonly byte RawAction;

    /// <summary>
    /// The raw wire byte behind <see cref="Side"/>. Not validated on decode — pass it to
    /// <see cref="EnumValues.TryFromSide(byte, out Side)"/> for a checked conversion.
    /// </summary>
    public readonly byte RawSide;

    /// <summary>A bit field indicating event end, message characteristics, and data quality.</summary>
    public readonly FlagSet Flags;

    /// <summary>The book level where the update event occurred.</summary>
    public readonly byte Depth;

    /// <summary>
    /// The capture-server-received timestamp, in nanoseconds since the UNIX epoch. This, not
    /// <see cref="RecordHeader.TsEvent"/>, is the record's index timestamp.
    /// </summary>
    public readonly ulong TsRecv;

    /// <summary>
    /// The matching-engine-sending timestamp, in nanoseconds before <see cref="TsRecv"/>.
    /// </summary>
    public readonly int TsInDelta;

    /// <summary>The message sequence number assigned at the venue.</summary>
    public readonly uint Sequence;

    /// <summary>The top of the order book.</summary>
    public readonly BidAskPairArray1 Levels;

    /// <summary>The event action as its raw ASCII character.</summary>
    public char ActionChar => (char)RawAction;

    /// <summary>
    /// The event action. Undefined wire bytes cast through to an unnamed value rather than
    /// throwing; see <see cref="RawAction"/>.
    /// </summary>
    public Action Action => (Action)RawAction;

    /// <summary>The side that initiates the event, as its raw ASCII character.</summary>
    public char SideChar => (char)RawSide;

    /// <summary>
    /// The side that initiates the event. Undefined wire bytes cast through to an unnamed value
    /// rather than throwing; see <see cref="RawSide"/>.
    /// </summary>
    public Side Side => (Side)RawSide;

    /// <inheritdoc/>
    /// <remarks>
    /// <see cref="TsRecv"/>, not <see cref="RecordHeader.TsEvent"/> — see the remarks on
    /// <see cref="IRecord{TSelf}.IndexTs"/>.
    /// </remarks>
    public ulong IndexTs => TsRecv;

    /// <inheritdoc/>
    public static bool HasRType(RType rtype) => rtype == RType.Mbp1;

    /// <inheritdoc/>
    public static int WireSize => Unsafe.SizeOf<Mbp1Msg>();
}
