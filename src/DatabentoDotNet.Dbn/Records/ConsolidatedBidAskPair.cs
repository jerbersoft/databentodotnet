using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// One consolidated price level: the bid and ask sides of a single book level, each tagged with
/// the publisher the quote came from.
/// </summary>
/// <remarks>
/// Not a record in its own right — it has no <see cref="RecordHeader"/> and no rtype. It is
/// embedded in <see cref="Cmbp1Msg"/> and <see cref="CbboMsg"/>. It is the same 32 bytes as
/// <see cref="BidAskPair"/>, but the last 8 bytes carry publisher IDs rather than order counts,
/// so the two are not interchangeable.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct ConsolidatedBidAskPair
{
    /// <summary>The bid price, where every 1 unit corresponds to 1e-9.</summary>
    public readonly long BidPx;

    /// <summary>The ask price, where every 1 unit corresponds to 1e-9.</summary>
    public readonly long AskPx;

    /// <summary>The bid size.</summary>
    public readonly uint BidSz;

    /// <summary>The ask size.</summary>
    public readonly uint AskSz;

    /// <summary>The publisher ID of the venue publishing the best bid.</summary>
    public readonly ushort BidPb;

    private readonly ReservedBytes2 _reserved1;

    /// <summary>The publisher ID of the venue publishing the best ask.</summary>
    public readonly ushort AskPb;

    private readonly ReservedBytes2 _reserved2;
}
