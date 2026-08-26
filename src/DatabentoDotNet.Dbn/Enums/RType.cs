namespace DatabentoDotNet.Dbn;

/// <summary>
/// A record type: a sentinel for the concrete record layout, carried in every record's
/// <see cref="RecordHeader.RawRType"/> field and surfaced typed as
/// <see cref="RecordHeader.RType"/>.
/// </summary>
/// <remarks>
/// Discriminants are non-contiguous hex values, not a dense range. <see cref="RecordHeader"/>
/// documents its <c>RawRType</c> field as covering <c>0x00..0x0F</c> for market-by-price book
/// depth, but only three values in that nibble — <c>0x00</c>, <c>0x01</c>, and <c>0x0A</c> —
/// are actually defined here; the other 13 values in the nibble are conceptually reserved for
/// other book depths but have no <see cref="RType"/> variant today and are rejected by
/// <see cref="EnumValues.TryFromRType(byte, out RType)"/> like any other undefined byte. See
/// <see cref="WireStrings"/> for string conversions.
/// </remarks>
public enum RType : byte
{
    /// <summary>Market-by-price with a book depth of 0 (used for the <see cref="Schema.Trades"/> schema).</summary>
    Mbp0 = 0x00,

    /// <summary>Market-by-price with a book depth of 1 (also used for the <see cref="Schema.Tbbo"/> schema).</summary>
    Mbp1 = 0x01,

    /// <summary>Market-by-price with a book depth of 10.</summary>
    Mbp10 = 0x0A,

    /// <summary>
    /// Open/high/low/close/volume at an unspecified cadence. Deprecated upstream since
    /// <c>dbn</c> 0.3.3 in favor of the per-cadence OHLCV rtypes below; retained here for
    /// decode compatibility with older files.
    /// </summary>
    OhlcvDeprecated = 0x11,

    /// <summary>An exchange status record.</summary>
    Status = 0x12,

    /// <summary>An instrument definition record.</summary>
    InstrumentDef = 0x13,

    /// <summary>An order imbalance record.</summary>
    Imbalance = 0x14,

    /// <summary>An error message from the gateway.</summary>
    Error = 0x15,

    /// <summary>A symbol mapping record.</summary>
    SymbolMapping = 0x16,

    /// <summary>A non-error message from the gateway, including heartbeats.</summary>
    System = 0x17,

    /// <summary>A statistics record from the publisher (not calculated by Databento).</summary>
    Statistics = 0x18,

    /// <summary>Open/high/low/close/volume at a one-second cadence.</summary>
    Ohlcv1S = 0x20,

    /// <summary>Open/high/low/close/volume at a one-minute cadence.</summary>
    Ohlcv1M = 0x21,

    /// <summary>Open/high/low/close/volume at an hourly cadence.</summary>
    Ohlcv1H = 0x22,

    /// <summary>Open/high/low/close/volume at a daily cadence based on the UTC date.</summary>
    Ohlcv1D = 0x23,

    /// <summary>Open/high/low/close/volume at a daily cadence based on the end of the trading session.</summary>
    OhlcvEod = 0x24,

    /// <summary>A market-by-order record.</summary>
    Mbo = 0xA0,

    /// <summary>A consolidated best bid and offer record.</summary>
    Cmbp1 = 0xB1,

    /// <summary>Consolidated best bid and offer subsampled at a one-second interval.</summary>
    Cbbo1S = 0xC0,

    /// <summary>Consolidated best bid and offer subsampled at a one-minute interval.</summary>
    Cbbo1M = 0xC1,

    /// <summary>A trade record carrying the consolidated BBO immediately before the trade.</summary>
    Tcbbo = 0xC2,

    /// <summary>Best bid and offer subsampled at a one-second interval.</summary>
    Bbo1S = 0xC3,

    /// <summary>Best bid and offer subsampled at a one-minute interval.</summary>
    Bbo1M = 0xC4,
}
