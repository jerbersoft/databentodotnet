namespace DatabentoDotNet.Dbn;

/// <summary>
/// The type of statistic contained in a <c>StatMsg</c>.
/// </summary>
/// <remarks>
/// Purely numeric — this type has no wire string form (no <c>as_str</c>/<c>FromStr</c> in the
/// Rust source). Discriminants are non-contiguous: values 1 through 26 are sequential, then the
/// two venue-specific values jump to 10001 and 10002. Upstream marks this type
/// <c>#[non_exhaustive]</c>; Databento may add variants in a future release without that being
/// a breaking change. This type has no default variant.
/// </remarks>
public enum StatType : ushort
{
    /// <summary>
    /// The price of the first trade of an instrument. <c>quantity</c> is set when provided by
    /// the venue.
    /// </summary>
    OpeningPrice = 1,

    /// <summary>
    /// The probable price of the first trade of an instrument, published during pre-open. Both
    /// <c>price</c> and <c>quantity</c> are set.
    /// </summary>
    IndicativeOpeningPrice = 2,

    /// <summary>
    /// The settlement price of an instrument. <c>flags</c> indicates whether the price is final
    /// or preliminary, and actual or theoretical; <c>ts_ref</c> is the settlement's trading date.
    /// </summary>
    SettlementPrice = 3,

    /// <summary>The lowest trade price of an instrument during the trading session.</summary>
    TradingSessionLowPrice = 4,

    /// <summary>The highest trade price of an instrument during the trading session.</summary>
    TradingSessionHighPrice = 5,

    /// <summary>
    /// The number of contracts cleared for an instrument on the previous trading date.
    /// <c>ts_ref</c> is the trading date of the volume.
    /// </summary>
    ClearedVolume = 6,

    /// <summary>The lowest offer price for an instrument during the trading session.</summary>
    LowestOffer = 7,

    /// <summary>The highest bid price for an instrument during the trading session.</summary>
    HighestBid = 8,

    /// <summary>
    /// The current number of outstanding contracts of an instrument. <c>ts_ref</c> is the
    /// trading date the open interest was calculated for.
    /// </summary>
    OpenInterest = 9,

    /// <summary>The volume-weighted average price (VWAP) for a fixing period.</summary>
    FixingPrice = 10,

    /// <summary>
    /// The last trade price during a trading session. <c>quantity</c> is set when provided by
    /// the venue.
    /// </summary>
    ClosePrice = 11,

    /// <summary>
    /// The change in price from the previous trading session's close to the most recent
    /// session.
    /// </summary>
    NetChange = 12,

    /// <summary>
    /// The volume-weighted average price (VWAP) during the trading session. <c>quantity</c> is
    /// the traded volume.
    /// </summary>
    Vwap = 13,

    /// <summary>The implied volatility associated with the settlement price.</summary>
    Volatility = 14,

    /// <summary>The option delta associated with the settlement price.</summary>
    Delta = 15,

    /// <summary>
    /// The auction uncrossing price, for auctions that are neither the official opening nor
    /// closing auction. <c>quantity</c> is set when provided by the venue.
    /// </summary>
    UncrossingPrice = 16,

    /// <summary>The exchange-defined upper price limit.</summary>
    UpperPriceLimit = 17,

    /// <summary>The exchange-defined lower price limit.</summary>
    LowerPriceLimit = 18,

    /// <summary>
    /// The number of block contracts cleared for an instrument on the previous trading date.
    /// <c>ts_ref</c> is the trading date of the volume.
    /// </summary>
    BlockVolume = 19,

    /// <summary>
    /// The probable price of the last trade of an instrument, published during the trading
    /// session.
    /// </summary>
    IndicativeClosePrice = 20,

    /// <summary>
    /// The Market-Wide Circuit Breaker (MWCB) level 1 threshold (7%), expressed as S&amp;P 500
    /// index points.
    /// </summary>
    MwcbLevel1 = 21,

    /// <summary>
    /// The Market-Wide Circuit Breaker (MWCB) level 2 threshold (13%), expressed as S&amp;P 500
    /// index points.
    /// </summary>
    MwcbLevel2 = 22,

    /// <summary>
    /// The Market-Wide Circuit Breaker (MWCB) level 3 threshold (20%), expressed as S&amp;P 500
    /// index points.
    /// </summary>
    MwcbLevel3 = 23,

    /// <summary>The auction collar reference price.</summary>
    AuctionCollarReferencePrice = 24,

    /// <summary>The auction collar upper price.</summary>
    AuctionCollarUpperPrice = 25,

    /// <summary>The auction collar lower price.</summary>
    AuctionCollarLowerPrice = 26,

    /// <summary>
    /// A venue-specific volume statistic. Refer to the venue's documentation for details.
    /// </summary>
    VenueSpecificVolume1 = 10001,

    /// <summary>
    /// A venue-specific price statistic. Refer to the venue's documentation for details.
    /// </summary>
    VenueSpecificPrice1 = 10002,
}
