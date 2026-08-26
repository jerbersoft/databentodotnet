namespace DatabentoDotNet.Dbn.Enums;

/// <summary>
/// The primary enum for a <c>StatusMsg</c> update: what the instrument's trading status changed
/// to.
/// </summary>
/// <remarks>
/// Purely numeric — this type has no wire string form. Upstream marks it
/// <c>#[non_exhaustive]</c>; Databento may add variants in a future release without that being
/// a breaking change.
/// </remarks>
public enum StatusAction : ushort
{
    /// <summary>No change.</summary>
    None = 0,

    /// <summary>The instrument is in a pre-open period.</summary>
    PreOpen = 1,

    /// <summary>The instrument is in a pre-cross period.</summary>
    PreCross = 2,

    /// <summary>The instrument is quoting but not trading.</summary>
    Quoting = 3,

    /// <summary>The instrument is in a cross/auction.</summary>
    Cross = 4,

    /// <summary>The instrument is being opened through a trading rotation.</summary>
    Rotation = 5,

    /// <summary>A new price indication is available for the instrument.</summary>
    NewPriceIndication = 6,

    /// <summary>The instrument is trading.</summary>
    Trading = 7,

    /// <summary>Trading in the instrument has been halted.</summary>
    Halt = 8,

    /// <summary>Trading in the instrument has been paused.</summary>
    Pause = 9,

    /// <summary>Trading in the instrument has been suspended.</summary>
    Suspend = 10,

    /// <summary>The instrument is in a pre-close period.</summary>
    PreClose = 11,

    /// <summary>Trading in the instrument has closed.</summary>
    Close = 12,

    /// <summary>The instrument is in a post-close period.</summary>
    PostClose = 13,

    /// <summary>A change in short-selling restrictions.</summary>
    SsrChange = 14,

    /// <summary>The instrument is not available for trading, either closed or halted.</summary>
    NotAvailableForTrading = 15,
}
