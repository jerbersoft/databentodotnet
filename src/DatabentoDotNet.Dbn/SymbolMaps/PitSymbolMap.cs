using NodaTime;
using NodaTime.Text;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// A point-in-time symbol map: resolves an instrument ID to its symbol with no date involved at
/// all. Useful for live symbology, or a historical request over a single day where the mapping is
/// known not to change.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>PitSymbolMap</c> (<c>symbol_map.rs:73-374</c>). Build one from a single
/// day of a decoded stream's metadata with <see cref="FromMetadata"/>, or grow one incrementally
/// as a live or replayed stream is consumed with <see cref="OnRecord"/>. Resolve with
/// <see cref="TryGetSymbol"/>.
/// </para>
/// <para>
/// <b>No date or timestamp is ever involved in resolution</b> — this is the whole point of
/// "point-in-time": the caller already committed to one date when the map was built (or has kept
/// it current via <see cref="OnRecord"/>), so <see cref="TryGetSymbol"/> is unconditional
/// <c>instrument_id -&gt; symbol</c>. This is a real, deliberate divergence from
/// <see cref="TsSymbolMap"/>, not an oversight to "fix" into checking a timestamp too.
/// </para>
/// <para>
/// <b>The incremental update path (<see cref="OnRecord"/>, <see cref="OnSymbolMapping(in SymbolMappingMsg)"/>)
/// is what M2's live client depends on</b>: called once per record as a stream is consumed, so it
/// stays allocation-light — it allocates exactly the one <see cref="string"/> a genuine new
/// mapping requires, the same as upstream's own <c>to_owned()</c>, and nothing else.
/// </para>
/// </remarks>
public sealed class PitSymbolMap
{
    private readonly Dictionary<uint, string> _map = [];

    /// <summary>Creates a new, empty point-in-time symbol map.</summary>
    public PitSymbolMap()
    {
    }

    /// <summary><see langword="true"/> when there are no mappings.</summary>
    public bool IsEmpty => _map.Count == 0;

    /// <summary>The number of instrument IDs currently mapped.</summary>
    public int Count => _map.Count;

    /// <summary>
    /// Builds a point-in-time symbol map from a decoded stream's metadata, resolved for one
    /// specific date.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of upstream's <c>PitSymbolMap::from_metadata</c> (<c>symbol_map.rs:237-273</c>),
    /// reached via <c>Metadata::symbol_map_for_date()</c> (<c>metadata.rs:113-115</c>).
    /// </para>
    /// <para>
    /// <b>The date-range check compares at full nanosecond precision, not date granularity.</b>
    /// <paramref name="date"/> is promoted to <paramref name="date"/> at 00:00 UTC and compared
    /// against <see cref="Metadata.End"/> — itself a nanosecond timestamp — directly, not against
    /// <see cref="Metadata.End"/> truncated to a date. The upper bound is therefore exclusive at
    /// nanosecond granularity: an <see cref="Metadata.End"/> of exactly midnight on day D excludes
    /// day D entirely (<c>00:00 &gt;= 00:00</c>), while an <see cref="Metadata.End"/> even one
    /// nanosecond past midnight on day D includes all of day D
    /// (<c>00:00 &lt; 00:00:00.000000001</c>). Truncating <see cref="Metadata.End"/> to a date
    /// before comparing gets this boundary backwards — pinned upstream by
    /// <c>test_symbol_map_for_date_out_of_range</c> (<c>symbol_map.rs:870-890</c>) and ported
    /// as <c>SymbolMapTests</c>' own midnight-exact / midnight-plus-one-nanosecond tests.
    /// </para>
    /// </remarks>
    /// <param name="metadata">The metadata to build the map from.</param>
    /// <param name="date">The date to resolve every mapping for.</param>
    /// <returns>The resulting map.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="metadata"/> is <see langword="null"/>.</exception>
    /// <exception cref="DbnDecodeException">
    /// Neither <see cref="Metadata.StypeIn"/> nor <see cref="Metadata.StypeOut"/> is
    /// <see cref="SType.InstrumentId"/>; a mapping's instrument-ID string does not parse as a
    /// <see cref="uint"/>; or <paramref name="date"/> falls outside <paramref name="metadata"/>'s
    /// query range.
    /// </exception>
    public static PitSymbolMap FromMetadata(Metadata metadata, LocalDate date)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var isInverse = SymbolMapSupport.IsInverse(metadata);
        var midnightUtcNanoseconds = DbnTime.ToUnixNanosecondsAtMidnightUtc(date);

        if (date < DbnTime.ToUtcDate(metadata.Start) || (metadata.End is { } end && midnightUtcNanoseconds >= end))
        {
            throw new DbnDecodeException(
                $"Cannot build a symbol map for {LocalDatePattern.Iso.Format(date)}: the date is outside the metadata's query range.");
        }

        var result = new PitSymbolMap();
        foreach (var mapping in metadata.Mappings)
        {
            MappingInterval interval = default;
            var found = false;
            foreach (var candidate in mapping.Intervals)
            {
                if (date >= candidate.StartDate && date < candidate.EndDate)
                {
                    interval = candidate;
                    found = true;
                    break;
                }
            }

            if (!found || interval.Symbol.Length == 0)
            {
                continue;
            }

            if (isInverse)
            {
                var instrumentId = SymbolMapSupport.ParseInstrumentId(mapping.RawSymbol);
                result._map[instrumentId] = interval.Symbol;
            }
            else
            {
                var instrumentId = SymbolMapSupport.ParseInstrumentId(interval.Symbol);
                result._map[instrumentId] = mapping.RawSymbol;
            }
        }

        return result;
    }

    /// <summary>
    /// Updates this map from one record if it carries a symbol mapping; otherwise does nothing.
    /// </summary>
    /// <remarks>
    /// Port of upstream's <c>PitSymbolMap::on_record</c> (<c>symbol_map.rs:280-288</c>). Tries the
    /// current-version <see cref="SymbolMappingMsg"/> layout first, then the DBN v1 layout
    /// (<see cref="SymbolMappingMsgV1"/>); any other record type — including an instrument
    /// definition — is a silent no-op. Definition records update the map only through the
    /// separate <see cref="OnInstrumentDef(in InstrumentDefMsg)"/> family, which a caller must
    /// call explicitly; upstream does not fold that path into <c>on_record</c> either.
    /// </remarks>
    /// <param name="record">The record to inspect.</param>
    public void OnRecord(RecordRef record)
    {
        if (record.Has<SymbolMappingMsg>())
        {
            OnSymbolMapping(in record.Get<SymbolMappingMsg>());
            return;
        }

        if (record.Has<SymbolMappingMsgV1>())
        {
            OnSymbolMapping(in record.Get<SymbolMappingMsgV1>());
        }
    }

    /// <summary>Updates this map from a current-version (DBN v2/v3) symbol-mapping record.</summary>
    /// <remarks>
    /// Port of upstream's <c>PitSymbolMap::on_symbol_mapping</c> (<c>symbol_map.rs:294-304</c>).
    /// Maps <see cref="RecordHeader.InstrumentId"/> to <see cref="SymbolMappingMsg.StypeOutSymbol"/>
    /// — the output symbol, since a live symbol-mapping record always resolves an input symbol to
    /// an instrument ID and reports back what that instrument's output symbol is.
    /// </remarks>
    /// <param name="record">The symbol-mapping record.</param>
    public void OnSymbolMapping(in SymbolMappingMsg record)
        => _map[record.Header.InstrumentId] = record.StypeOutSymbol.ToString();

    /// <summary>Updates this map from a DBN v1 symbol-mapping record.</summary>
    /// <remarks>See the remarks on <see cref="OnSymbolMapping(in SymbolMappingMsg)"/>.</remarks>
    /// <param name="record">The symbol-mapping record.</param>
    public void OnSymbolMapping(in SymbolMappingMsgV1 record)
        => _map[record.Header.InstrumentId] = record.StypeOutSymbol.ToString();

    /// <summary>Updates this map from a current-version (DBN v3) instrument definition record.</summary>
    /// <remarks>
    /// <para>
    /// Port of upstream's <c>PitSymbolMap::on_instrument_def</c> (<c>symbol_map.rs:310-318</c>).
    /// An alternate incremental-update path to <see cref="OnSymbolMapping(in SymbolMappingMsg)"/>:
    /// a definition record carries its own <c>raw_symbol</c>, so a stream of definitions can keep
    /// a map current without any symbol-mapping records at all. Neither this method nor its two
    /// sibling overloads is called from <see cref="OnRecord"/> — a caller who wants this path must
    /// call it explicitly, matching upstream.
    /// </para>
    /// </remarks>
    /// <param name="record">The instrument definition record.</param>
    public void OnInstrumentDef(in InstrumentDefMsg record)
        => _map[record.Header.InstrumentId] = record.RawSymbol.ToString();

    /// <summary>Updates this map from a DBN v1 instrument definition record.</summary>
    /// <remarks>See the remarks on <see cref="OnInstrumentDef(in InstrumentDefMsg)"/>.</remarks>
    /// <param name="record">The instrument definition record.</param>
    public void OnInstrumentDef(in InstrumentDefMsgV1 record)
        => _map[record.Header.InstrumentId] = record.RawSymbol.ToString();

    /// <summary>Updates this map from a DBN v2 instrument definition record.</summary>
    /// <remarks>See the remarks on <see cref="OnInstrumentDef(in InstrumentDefMsg)"/>.</remarks>
    /// <param name="record">The instrument definition record.</param>
    public void OnInstrumentDef(in InstrumentDefMsgV2 record)
        => _map[record.Header.InstrumentId] = record.RawSymbol.ToString();

    /// <summary>Looks up the symbol currently mapped to an instrument ID.</summary>
    /// <remarks>
    /// Port of upstream's <c>PitSymbolMap::get</c> (<c>symbol_map.rs:321-323</c>), shaped as a
    /// <c>Try*</c> member because an unmapped instrument ID is an expected outcome, not an
    /// exceptional one.
    /// </remarks>
    /// <param name="instrumentId">The instrument ID to resolve.</param>
    /// <param name="symbol">
    /// Receives the resolved symbol, or <see langword="null"/> when <paramref name="instrumentId"/>
    /// has no mapping.
    /// </param>
    /// <returns><see langword="true"/> if a mapping was found.</returns>
    public bool TryGetSymbol(uint instrumentId, out string? symbol) => _map.TryGetValue(instrumentId, out symbol);
}
