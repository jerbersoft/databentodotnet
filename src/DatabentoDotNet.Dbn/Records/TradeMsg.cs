using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// A trade. The record of the <see cref="Schema.Trades"/> schema, and market-by-price with a
/// book depth of 0.
/// </summary>
/// <remarks>
/// Field order is transcribed from the <c>#[repr(C)]</c> Rust declaration, not from its
/// <c>encode_order</c> attributes. Note that it differs from <see cref="MboMsg"/>: here
/// <see cref="RawAction"/> and <see cref="RawSide"/> precede <see cref="Flags"/>, whereas in
/// <see cref="MboMsg"/> they follow it.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct TradeMsg : IRecord<TradeMsg>
{
    /// <summary>The common header.</summary>
    public readonly RecordHeader Header;

    /// <summary>The trade price, where every 1 unit corresponds to 1e-9.</summary>
    public readonly long Price;

    /// <summary>The trade quantity.</summary>
    public readonly uint Size;

    /// <summary>
    /// The raw wire byte behind <see cref="Action"/>. Always <c>'T'</c> for this schema. Not
    /// validated on decode — pass it to
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

    /// <summary>The event action as its raw ASCII character.</summary>
    public char ActionChar => (char)RawAction;

    /// <summary>
    /// The event action. Undefined wire bytes cast through to an unnamed value rather than
    /// throwing; see <see cref="RawAction"/>.
    /// </summary>
    public Action Action => (Action)RawAction;

    /// <summary>The aggressor side as its raw ASCII character.</summary>
    public char SideChar => (char)RawSide;

    /// <summary>
    /// The aggressor side. Undefined wire bytes cast through to an unnamed value rather than
    /// throwing; see <see cref="RawSide"/>.
    /// </summary>
    public Side Side => (Side)RawSide;

    /// <inheritdoc/>
    /// <remarks>
    /// <see cref="TsRecv"/>, not <see cref="RecordHeader.TsEvent"/> — see the remarks on
    /// <see cref="IRecord{TSelf}.IndexTs"/>.
    /// </remarks>
    public ulong IndexTs => TsRecv;

    /// <inheritdoc/>
    public static bool HasRType(RType rtype) => rtype == RType.Mbp0;

    /// <inheritdoc/>
    public static int WireSize => Unsafe.SizeOf<TradeMsg>();
}
