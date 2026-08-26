using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// Open, high, low, close and volume for one bar. The record of the OHLCV schemas at every
/// cadence.
/// </summary>
/// <remarks>
/// The cadence is carried by the rtype, not by the layout: one struct serves
/// <see cref="RType.Ohlcv1S"/>, <see cref="RType.Ohlcv1M"/>, <see cref="RType.Ohlcv1H"/>,
/// <see cref="RType.Ohlcv1D"/>, <see cref="RType.OhlcvEod"/> and the deprecated
/// <see cref="RType.OhlcvDeprecated"/>. It has no <c>ts_recv</c>, so its index timestamp is
/// <see cref="RecordHeader.TsEvent"/>.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct OhlcvMsg : IRecord<OhlcvMsg>
{
    /// <summary>The common header.</summary>
    public readonly RecordHeader Header;

    /// <summary>The open price for the bar, where every 1 unit corresponds to 1e-9.</summary>
    public readonly long Open;

    /// <summary>The high price for the bar, where every 1 unit corresponds to 1e-9.</summary>
    public readonly long High;

    /// <summary>The low price for the bar, where every 1 unit corresponds to 1e-9.</summary>
    public readonly long Low;

    /// <summary>The close price for the bar, where every 1 unit corresponds to 1e-9.</summary>
    public readonly long Close;

    /// <summary>The total volume traded during the bar.</summary>
    public readonly ulong Volume;

    /// <inheritdoc/>
    /// <remarks>
    /// This record has no <c>ts_recv</c>, so its index timestamp is the header's
    /// <see cref="RecordHeader.TsEvent"/> — upstream's default, not an override.
    /// </remarks>
    public ulong IndexTs => Header.TsEvent;

    /// <inheritdoc/>
    public static bool HasRType(RType rtype) => rtype is RType.Ohlcv1S
        or RType.Ohlcv1M
        or RType.Ohlcv1H
        or RType.Ohlcv1D
        or RType.OhlcvEod
        or RType.OhlcvDeprecated;

    /// <inheritdoc/>
    public static int WireSize => Unsafe.SizeOf<OhlcvMsg>();
}
