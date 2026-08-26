using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// One price level: the bid and ask sides of a single book level.
/// </summary>
/// <remarks>
/// Not a record in its own right — it has no <see cref="RecordHeader"/> and no rtype. It is
/// embedded in <see cref="Mbp1Msg"/>, <see cref="Mbp10Msg"/> and <see cref="BboMsg"/>. Field
/// order is transcribed from the <c>#[repr(C)]</c> Rust declaration; see the remarks on
/// <see cref="RecordHeader"/> for why that ordering is load-bearing.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct BidAskPair
{
    /// <summary>The bid price, where every 1 unit corresponds to 1e-9.</summary>
    public readonly long BidPx;

    /// <summary>The ask price, where every 1 unit corresponds to 1e-9.</summary>
    public readonly long AskPx;

    /// <summary>The bid size.</summary>
    public readonly uint BidSz;

    /// <summary>The ask size.</summary>
    public readonly uint AskSz;

    /// <summary>The bid order count.</summary>
    public readonly uint BidCt;

    /// <summary>The ask order count.</summary>
    public readonly uint AskCt;
}
