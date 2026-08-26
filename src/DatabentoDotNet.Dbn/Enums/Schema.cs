namespace DatabentoDotNet.Dbn;

/// <summary>
/// A data record schema. Each schema has a particular record type associated with it.
/// </summary>
/// <remarks>
/// Wire strings are <c>kebab-case</c> and are <b>not</b> a mechanical lowercase of the variant
/// name: <see cref="Mbp1"/> is <c>mbp-1</c> (not <c>mbp1</c>), <see cref="Ohlcv1S"/> is
/// <c>ohlcv-1s</c>, and so on — a hyphen is inserted before a leading digit that a naive
/// case-transition splitter would miss. See <see cref="WireStrings"/> for the exact mapping;
/// there are no parse-only aliases for this type.
/// </remarks>
public enum Schema : ushort
{
    /// <summary>Market by order.</summary>
    Mbo = 0,

    /// <summary>Market by price with a book depth of 1.</summary>
    Mbp1 = 1,

    /// <summary>Market by price with a book depth of 10.</summary>
    Mbp10 = 2,

    /// <summary>All trade events with the BBO immediately before the effect of the trade.</summary>
    Tbbo = 3,

    /// <summary>All trade events.</summary>
    Trades = 4,

    /// <summary>Open, high, low, close, and volume at a one-second interval.</summary>
    Ohlcv1S = 5,

    /// <summary>Open, high, low, close, and volume at a one-minute interval.</summary>
    Ohlcv1M = 6,

    /// <summary>Open, high, low, close, and volume at an hourly interval.</summary>
    Ohlcv1H = 7,

    /// <summary>Open, high, low, close, and volume at a daily interval based on the UTC date.</summary>
    Ohlcv1D = 8,

    /// <summary>Instrument definitions.</summary>
    Definition = 9,

    /// <summary>Additional data disseminated by publishers.</summary>
    Statistics = 10,

    /// <summary>Trading status events.</summary>
    Status = 11,

    /// <summary>Auction imbalance events.</summary>
    Imbalance = 12,

    /// <summary>
    /// Open, high, low, close, and volume at a daily cadence based on the end of the trading
    /// session.
    /// </summary>
    OhlcvEod = 13,

    /// <summary>Consolidated best bid and offer.</summary>
    Cmbp1 = 14,

    /// <summary>Consolidated best bid and offer subsampled at one-second intervals, plus trades.</summary>
    Cbbo1S = 15,

    /// <summary>Consolidated best bid and offer subsampled at one-minute intervals, plus trades.</summary>
    Cbbo1M = 16,

    /// <summary>All trade events with the consolidated BBO immediately before the effect of the trade.</summary>
    Tcbbo = 17,

    /// <summary>Best bid and offer subsampled at one-second intervals, plus trades.</summary>
    Bbo1S = 18,

    /// <summary>Best bid and offer subsampled at one-minute intervals, plus trades.</summary>
    Bbo1M = 19,
}
