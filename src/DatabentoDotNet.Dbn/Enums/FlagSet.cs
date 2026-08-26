namespace DatabentoDotNet.Dbn.Enums;

/// <summary>
/// Bit-set flags carried by <c>MboMsg</c> and the record types derived from it.
/// </summary>
/// <remarks>
/// Unlike every other enum in this namespace, every raw byte is a valid <see cref="FlagSet"/> —
/// there is no rejection path, so this type has no <c>EnumValues.TryFrom</c> counterpart. Bit 0
/// (<c>0x01</c>) is reserved and has no named flag in the wire format; bits set there pass
/// through unchanged rather than being rejected.
/// </remarks>
[Flags]
public enum FlagSet : byte
{
    /// <summary>No flags set.</summary>
    None = 0x00,

    /// <summary>Used to indicate a publisher-specific event.</summary>
    PublisherSpecific = 0x02,

    /// <summary>Indicates an unrecoverable gap was detected in the channel.</summary>
    MaybeBadBook = 0x04,

    /// <summary>Indicates the <c>ts_recv</c> value is inaccurate due to clock issues or packet reordering.</summary>
    BadTsRecv = 0x08,

    /// <summary>Indicates an aggregated price-level record, not an individual order.</summary>
    Mbp = 0x10,

    /// <summary>Indicates the record was sourced from a replay, such as a snapshot server.</summary>
    Snapshot = 0x20,

    /// <summary>Indicates a top-of-book record, not an individual order.</summary>
    Tob = 0x40,

    /// <summary>Indicates it's the last record in the event from the venue for a given instrument ID.</summary>
    Last = 0x80,
}
