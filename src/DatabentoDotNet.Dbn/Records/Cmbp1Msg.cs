using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// Consolidated market-by-price with a book depth of 1. The record of the
/// <see cref="Schema.Cmbp1"/> and <see cref="Schema.Tcbbo"/> schemas.
/// </summary>
/// <remarks>
/// Upstream aliases this struct as <c>TcbboMsg</c> for the TCBBO schema; the two are the same
/// type, not two layouts. Its levels are <see cref="ConsolidatedBidAskPair"/>, not
/// <see cref="BidAskPair"/> — same 32 bytes, different meaning for the last 8. Field order is
/// transcribed from the <c>#[repr(C)]</c> Rust declaration, not from its <c>encode_order</c>
/// attributes.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct Cmbp1Msg : IRecord<Cmbp1Msg>
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

    private readonly byte _reserved1;

    /// <summary>
    /// The capture-server-received timestamp, in nanoseconds since the UNIX epoch. This, not
    /// <see cref="RecordHeader.TsEvent"/>, is the record's index timestamp.
    /// </summary>
    public readonly ulong TsRecv;

    /// <summary>
    /// The matching-engine-sending timestamp, in nanoseconds before <see cref="TsRecv"/>.
    /// </summary>
    public readonly int TsInDelta;

    private readonly ReservedBytes4 _reserved2;

    /// <summary>The top of the consolidated order book.</summary>
    public readonly ConsolidatedBidAskPairArray1 Levels;

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
    public static bool HasRType(RType rtype) => rtype is RType.Cmbp1 or RType.Tcbbo;

    /// <inheritdoc/>
    public static int WireSize => Unsafe.SizeOf<Cmbp1Msg>();
}
