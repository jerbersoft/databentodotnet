namespace DatabentoDotNet.Historical;

/// <summary>A type of data feed.</summary>
/// <remarks>
/// Port of upstream's <c>FeedMode</c> (<c>databento-rs/src/historical/metadata.rs:205-214</c>).
/// Received from <c>metadata.list_unit_prices</c> and never sent, so there is deliberately no
/// place in this library that renders one onto a request.
/// </remarks>
public enum FeedMode
{
    /// <summary>The historical batch data feed.</summary>
    Historical,

    /// <summary>The historical streaming data feed.</summary>
    HistoricalStreaming,

    /// <summary>The Live data feed, for real-time and intraday historical data.</summary>
    Live,
}
