namespace DatabentoDotNet.Dbn;

/// <summary>
/// Further information about a <c>StatusMsg</c> update.
/// </summary>
/// <remarks>
/// Purely numeric — this type has no wire string form. Upstream marks it
/// <c>#[non_exhaustive]</c>; Databento may add variants in a future release without that being
/// a breaking change.
/// </remarks>
public enum TradingEvent : ushort
{
    /// <summary>No additional information given.</summary>
    None = 0,

    /// <summary>Order entry is allowed. Modification and cancellation are not allowed.</summary>
    NoCancel = 1,

    /// <summary>A change of trading session occurred. Daily statistics are reset.</summary>
    ChangeTradingSession = 2,

    /// <summary>Implied matching is available.</summary>
    ImpliedMatchingOn = 3,

    /// <summary>Implied matching is not available.</summary>
    ImpliedMatchingOff = 4,
}
