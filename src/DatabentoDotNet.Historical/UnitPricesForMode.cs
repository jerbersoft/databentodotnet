using DatabentoDotNet.Dbn;

namespace DatabentoDotNet.Historical;

/// <summary>The unit prices for a particular <see cref="FeedMode"/>.</summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>UnitPricesForMode</c>
/// (<c>databento-rs/src/historical/metadata.rs:269-275</c>). Returned by
/// <c>metadata.list_unit_prices</c>, one entry per feed mode Databento prices separately.
/// </para>
/// <para>
/// <b><see cref="UnitPrices"/> holds <see langword="decimal"/>, not <see langword="double"/>.</b>
/// Upstream's field is <c>f64</c> (<c>metadata.rs:274</c>) only because Rust's standard library has
/// no decimal type to reach for; a unit price here is multiplied by a record count before anyone
/// sees a dollar figure, which makes binary floating point a money bug waiting for a request large
/// enough to surface it.
/// </para>
/// </remarks>
public sealed record UnitPricesForMode
{
    /// <summary>The data feed mode.</summary>
    public required FeedMode Mode { get; init; }

    /// <summary>The unit prices in US dollars by data record schema.</summary>
    public required IReadOnlyDictionary<Schema, decimal> UnitPrices { get; init; }
}
