using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DatabentoDotNet.Dbn.Enums;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// A subsampled best bid and offer. The record of the <see cref="Schema.Bbo1S"/> and
/// <see cref="Schema.Bbo1M"/> schemas.
/// </summary>
/// <remarks>
/// Upstream aliases this struct as <c>Bbo1SMsg</c> and <c>Bbo1MMsg</c>; all three are the same
/// type, not three layouts. It is deliberately layout-compatible with <see cref="Mbp1Msg"/> —
/// every field sits at the same offset as its <see cref="Mbp1Msg"/> counterpart, and the
/// reserved blocks stand exactly where <c>RawAction</c>, <c>Depth</c> and <c>TsInDelta</c> sit
/// there. Field order is
/// transcribed from the <c>#[repr(C)]</c> Rust declaration, not from its <c>encode_order</c>
/// attributes.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct BboMsg : IRecord<BboMsg>
{
    /// <summary>The common header.</summary>
    public readonly RecordHeader Header;

    /// <summary>
    /// The price of the last trade, where every 1 unit corresponds to 1e-9.
    /// <see cref="DbnConstants.UndefPrice"/> if there has been no trade this session.
    /// </summary>
    public readonly long Price;

    /// <summary>The quantity of the last trade.</summary>
    public readonly uint Size;

    private readonly byte _reserved1;

    /// <summary>
    /// The raw wire byte behind <see cref="Side"/>. Not validated on decode — pass it to
    /// <see cref="EnumValues.TryFromSide(byte, out Side)"/> for a checked conversion.
    /// </summary>
    public readonly byte RawSide;

    /// <summary>A bit field indicating event end, message characteristics, and data quality.</summary>
    public readonly FlagSet Flags;

    private readonly byte _reserved2;

    /// <summary>
    /// The end of the aggregation interval, in nanoseconds since the UNIX epoch. This, not
    /// <see cref="RecordHeader.TsEvent"/>, is the record's index timestamp.
    /// </summary>
    public readonly ulong TsRecv;

    private readonly ReservedBytes4 _reserved3;

    /// <summary>The message sequence number of the last update assigned at the venue.</summary>
    public readonly uint Sequence;

    /// <summary>The top of the order book.</summary>
    public readonly BidAskPairArray1 Levels;

    /// <summary>The side that initiated the last trade, as its raw ASCII character.</summary>
    public char SideChar => (char)RawSide;

    /// <summary>
    /// The side that initiated the last trade. Undefined wire bytes cast through to an unnamed
    /// value rather than throwing; see <see cref="RawSide"/>.
    /// </summary>
    public Side Side => (Side)RawSide;

    /// <inheritdoc/>
    public static bool HasRType(RType rtype) => rtype is RType.Bbo1S or RType.Bbo1M;

    /// <inheritdoc/>
    public static int WireSize => Unsafe.SizeOf<BboMsg>();
}
