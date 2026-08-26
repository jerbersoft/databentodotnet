using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// A subsampled consolidated best bid and offer. The record of the
/// <see cref="Schema.Cbbo1S"/> and <see cref="Schema.Cbbo1M"/> schemas.
/// </summary>
/// <remarks>
/// Upstream aliases this struct as <c>Cbbo1SMsg</c> and <c>Cbbo1MMsg</c>; all three are the same
/// type, not three layouts. Unlike <see cref="BboMsg"/> it carries no sequence number — the
/// byte budget goes entirely to the eight-byte reserved block after
/// <see cref="TsRecv"/>. Field order is transcribed from the <c>#[repr(C)]</c> Rust declaration,
/// not from its <c>encode_order</c> attributes.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct CbboMsg : IRecord<CbboMsg>
{
    /// <summary>The common header.</summary>
    public readonly RecordHeader Header;

    /// <summary>
    /// The price of the last trade, where every 1 unit corresponds to 1e-9.
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

    private readonly ReservedBytes8 _reserved3;

    /// <summary>The top of the consolidated order book.</summary>
    public readonly ConsolidatedBidAskPairArray1 Levels;

    /// <summary>The side that initiated the last trade, as its raw ASCII character.</summary>
    public char SideChar => (char)RawSide;

    /// <summary>
    /// The side that initiated the last trade. Undefined wire bytes cast through to an unnamed
    /// value rather than throwing; see <see cref="RawSide"/>.
    /// </summary>
    public Side Side => (Side)RawSide;

    /// <inheritdoc/>
    public static bool HasRType(RType rtype) => rtype is RType.Cbbo1S or RType.Cbbo1M;

    /// <inheritdoc/>
    public static int WireSize => Unsafe.SizeOf<CbboMsg>();
}
