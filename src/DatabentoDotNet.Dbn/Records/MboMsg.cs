using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// A market-by-order (MBO) tick. The record of the <see cref="Schema.Mbo"/> schema.
/// </summary>
/// <remarks>
/// Field order is transcribed from the <c>#[repr(C)]</c> Rust declaration, not from its
/// <c>encode_order</c> attributes — those control CSV and JSON column order only and have no
/// bearing on memory layout.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct MboMsg : IRecord<MboMsg>
{
    /// <summary>The common header.</summary>
    public readonly RecordHeader Header;

    /// <summary>The order ID assigned at the venue.</summary>
    public readonly ulong OrderId;

    /// <summary>The order price, where every 1 unit corresponds to 1e-9.</summary>
    public readonly long Price;

    /// <summary>The order quantity.</summary>
    public readonly uint Size;

    /// <summary>A bit field indicating event end, message characteristics, and data quality.</summary>
    public readonly FlagSet Flags;

    /// <summary>The channel ID assigned by Databento as an incrementing integer starting at zero.</summary>
    public readonly byte ChannelId;

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
    public static bool HasRType(RType rtype) => rtype == RType.Mbo;

    /// <inheritdoc/>
    public static int WireSize => Unsafe.SizeOf<MboMsg>();
}
