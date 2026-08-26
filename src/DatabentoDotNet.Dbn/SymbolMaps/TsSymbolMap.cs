namespace DatabentoDotNet.Dbn;

/// <summary>
/// A timeseries symbol map: resolves an instrument ID to its symbol on a specific date. Useful
/// for a historical request spanning multiple days, where the same instrument ID can mean a
/// different symbol on different dates (a continuous contract rolling to a new front-month
/// instrument, for example).
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>TsSymbolMap</c> (<c>symbol_map.rs:34-211</c>). Build one with
/// <see cref="FromMetadata"/> from a decoded stream's <see cref="Metadata"/>, then resolve with
/// <see cref="TryGetSymbol"/> for each record's date and instrument ID.
/// </para>
/// <para>
/// <b>Storage is one entry per instrument-day, not per interval.</b> <see cref="Insert"/> expands
/// a <c>[startDate, endDate)</c> interval into one dictionary entry for every calendar day in it,
/// trading memory for an O(1) exact-date lookup with no range search — the same trade-off
/// upstream makes. A query spanning many instruments over a wide date range can therefore build a
/// large map; that is expected, not a bug to "optimize away".
/// </para>
/// <para>
/// <b>The same symbol string is reused across every day of an interval, deliberately.</b> Upstream
/// stores <c>Arc&lt;String&gt;</c> so that expanding one interval into many day-keys costs a
/// reference-count bump per day, not a string copy. A .NET <see cref="string"/> is already a
/// reference type, so <see cref="Insert"/> gets the same sharing for free simply by storing the
/// same <see cref="string"/> instance in every day-key it writes — never by re-slicing or
/// re-allocating per day. Do not "simplify" this into a fresh string per day.
/// </para>
/// <para>
/// <b>No date-range validation against <see cref="Metadata.Start"/>/<see cref="Metadata.End"/>
/// happens here.</b> <see cref="FromMetadata"/> inserts every interval of every mapping
/// regardless of whether it falls inside the metadata's own query range — that is a property of
/// well-formed input, not something this type enforces. Contrast <see cref="PitSymbolMap.FromMetadata"/>,
/// which does validate its single date against the range.
/// </para>
/// </remarks>
public sealed class TsSymbolMap
{
    private readonly Dictionary<(DateOnly Date, uint InstrumentId), string> _map = [];

    /// <summary>Creates a new, empty timeseries symbol map.</summary>
    public TsSymbolMap()
    {
    }

    /// <summary><see langword="true"/> when there are no mappings.</summary>
    public bool IsEmpty => _map.Count == 0;

    /// <summary>The number of instrument-day entries in the map.</summary>
    /// <remarks>
    /// One entry per calendar day a mapping is valid for, not one per requested symbol or per
    /// mapping interval — see the remarks on <see cref="TsSymbolMap"/>.
    /// </remarks>
    public int Count => _map.Count;

    /// <summary>
    /// Builds a timeseries symbol map from a decoded stream's metadata, covering every mapping
    /// interval it carries.
    /// </summary>
    /// <remarks>
    /// Port of upstream's <c>TsSymbolMap::from_metadata</c> / <c>TryFrom&lt;&amp;Metadata&gt;</c>
    /// (<c>symbol_map.rs:107-109, 173-211</c>), reached via <c>Metadata::symbol_map()</c>
    /// (<c>metadata.rs:126-128</c>).
    /// </remarks>
    /// <param name="metadata">The metadata to build the map from.</param>
    /// <returns>The resulting map.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="metadata"/> is <see langword="null"/>.</exception>
    /// <exception cref="DbnDecodeException">
    /// Neither <see cref="Metadata.StypeIn"/> nor <see cref="Metadata.StypeOut"/> is
    /// <see cref="SType.InstrumentId"/>, so <paramref name="metadata"/> cannot yield a symbol map;
    /// or a mapping's instrument-ID string does not parse as a <see cref="uint"/>.
    /// </exception>
    public static TsSymbolMap FromMetadata(Metadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var result = new TsSymbolMap();
        var isInverse = SymbolMapSupport.IsInverse(metadata);

        foreach (var mapping in metadata.Mappings)
        {
            if (isInverse)
            {
                // mapping.raw_symbol IS the instrument-ID string; each interval's symbol is the
                // human-readable symbol to store.
                var instrumentId = SymbolMapSupport.ParseInstrumentId(mapping.RawSymbol);
                foreach (var interval in mapping.Intervals)
                {
                    // Empty symbol: the old symbology format's way of saying "unresolved". Skip
                    // rather than inserting a garbage mapping.
                    if (interval.Symbol.Length == 0)
                    {
                        continue;
                    }

                    result.Insert(instrumentId, interval.StartDate, interval.EndDate, interval.Symbol);
                }
            }
            else
            {
                // mapping.raw_symbol IS the human-readable symbol; each interval's symbol is the
                // instrument-ID string to parse. The same string instance is passed to every
                // Insert call below so every day of every interval shares one allocation.
                var symbol = mapping.RawSymbol;
                foreach (var interval in mapping.Intervals)
                {
                    if (interval.Symbol.Length == 0)
                    {
                        continue;
                    }

                    var instrumentId = SymbolMapSupport.ParseInstrumentId(interval.Symbol);
                    result.Insert(instrumentId, interval.StartDate, interval.EndDate, symbol);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Inserts a mapping for <paramref name="instrumentId"/>, valid on every calendar day from
    /// <paramref name="startDate"/> (inclusive) up to but not including <paramref name="endDate"/>
    /// (exclusive).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of upstream's <c>TsSymbolMap::insert</c> (<c>symbol_map.rs:117-146</c>). Writes one
    /// dictionary entry per calendar day in the half-open range
    /// <c>[startDate, endDate)</c>, overwriting any mapping already present for a given
    /// day/instrument pair.
    /// </para>
    /// <para>
    /// <paramref name="startDate"/> equal to <paramref name="endDate"/> is a silent no-op, matching
    /// upstream's own comment on the degenerate case ("Shouldn't happen but better to just
    /// ignore") — it is not an error, and it does not insert a single-day entry either.
    /// </para>
    /// </remarks>
    /// <param name="instrumentId">The instrument ID the mapping is for.</param>
    /// <param name="startDate">The first day the mapping is valid for, inclusive.</param>
    /// <param name="endDate">The day the mapping stops being valid, exclusive.</param>
    /// <param name="symbol">The symbol to map <paramref name="instrumentId"/> to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="symbol"/> is <see langword="null"/>.</exception>
    /// <exception cref="DbnDecodeException">
    /// <paramref name="startDate"/> comes after <paramref name="endDate"/>.
    /// </exception>
    public void Insert(uint instrumentId, DateOnly startDate, DateOnly endDate, string symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        var comparison = startDate.CompareTo(endDate);
        if (comparison > 0)
        {
            throw new DbnDecodeException(
                $"Cannot insert a symbol mapping: start_date {startDate:yyyy-MM-dd} comes after end_date {endDate:yyyy-MM-dd}.");
        }

        if (comparison == 0)
        {
            return;
        }

        var day = startDate;
        while (true)
        {
            _map[(day, instrumentId)] = symbol;
            day = day.AddDays(1);
            if (day >= endDate)
            {
                break;
            }
        }
    }

    /// <summary>Looks up the symbol for an instrument ID on a specific date.</summary>
    /// <remarks>
    /// Port of upstream's <c>TsSymbolMap::get</c> (<c>symbol_map.rs:150-152</c>), shaped as a
    /// <c>Try*</c> member because a miss — an unmapped instrument ID, or a date the map has no
    /// entry for — is an expected outcome, not an exceptional one.
    /// </remarks>
    /// <param name="date">The date to resolve the symbol for.</param>
    /// <param name="instrumentId">The instrument ID to resolve.</param>
    /// <param name="symbol">
    /// Receives the resolved symbol, or <see langword="null"/> when there is no mapping for
    /// <paramref name="instrumentId"/> on <paramref name="date"/>.
    /// </param>
    /// <returns><see langword="true"/> if a mapping was found.</returns>
    public bool TryGetSymbol(DateOnly date, uint instrumentId, out string? symbol)
        => _map.TryGetValue((date, instrumentId), out symbol);
}
