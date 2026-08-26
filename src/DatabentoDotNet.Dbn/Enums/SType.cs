namespace DatabentoDotNet.Dbn;

/// <summary>
/// A symbology type, i.e. the namespace a symbol string is drawn from.
/// </summary>
/// <remarks>
/// Wire strings use <c>snake_case</c> — contrast with <see cref="RType"/>/<see cref="Schema"/>,
/// which use <c>kebab-case</c>. Do not assume one casing convention applies everywhere. See
/// <see cref="WireStrings"/> for the string conversions, including four legacy aliases
/// (<c>product_id</c>, <c>native</c>, <c>nasdaq</c>, <c>cms</c>) that parse but are never
/// emitted.
/// </remarks>
public enum SType : byte
{
    /// <summary>Symbology using a unique numeric ID.</summary>
    InstrumentId = 0,

    /// <summary>Symbology using the original symbols provided by the publisher.</summary>
    RawSymbol = 1,

    /// <summary>
    /// A set of Databento-specific symbologies for referring to groups of symbols. Deprecated
    /// upstream since <c>dbn</c> 0.5.0 in favor of <see cref="Continuous"/> and
    /// <see cref="Parent"/>; retained here for decode compatibility.
    /// </summary>
    Smart = 2,

    /// <summary>
    /// A Databento-specific symbology where one symbol may point to different instruments at
    /// different points in time, e.g. always referring to the front-month future.
    /// </summary>
    Continuous = 3,

    /// <summary>
    /// A Databento-specific symbology for referring to a group of symbols by one "parent"
    /// symbol, e.g. <c>ES.FUT</c> for all ES futures.
    /// </summary>
    Parent = 4,

    /// <summary>Symbology for US equities using NASDAQ Integrated suffix conventions.</summary>
    NasdaqSymbol = 5,

    /// <summary>Symbology for US equities using CMS suffix conventions.</summary>
    CmsSymbol = 6,

    /// <summary>Symbology using International Securities Identification Numbers (ISIN), ISO 6166.</summary>
    Isin = 7,

    /// <summary>
    /// Symbology using US domestic Committee on Uniform Securities Identification Procedure
    /// (CUSIP) codes.
    /// </summary>
    UsCode = 8,

    /// <summary>Symbology using Bloomberg composite global IDs.</summary>
    BbgCompId = 9,

    /// <summary>Symbology using Bloomberg composite tickers.</summary>
    BbgCompTicker = 10,

    /// <summary>Symbology using Bloomberg FIGI exchange-level IDs.</summary>
    Figi = 11,

    /// <summary>Symbology using Bloomberg exchange-level tickers.</summary>
    FigiTicker = 12,

    /// <summary>
    /// Symbology using the Databento-specific listing ID, only available for the reference
    /// data API.
    /// </summary>
    ListingId = 13,

    /// <summary>
    /// Symbology using the Databento-specific issuer ID, only available for the reference data
    /// API.
    /// </summary>
    IssuerId = 14,

    /// <summary>
    /// Symbology using the Databento-specific security ID, only available for the reference
    /// data API.
    /// </summary>
    SecurityId = 15,
}
