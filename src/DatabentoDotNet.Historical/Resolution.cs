using DatabentoDotNet.Dbn;

namespace DatabentoDotNet.Historical;

/// <summary>
/// The answer to a <c>symbology.resolve</c> request: what each symbol resolved to, over which
/// dates, and which symbols did not fully resolve.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>Resolution</c> (<c>symbology.rs:127-142</c>).
/// </para>
/// <para>
/// <b><see cref="Mappings"/> holds every symbol that was asked for, including the ones that
/// resolved to nothing.</b> That is the API's behaviour rather than this port's, and it was
/// verified against <c>hist.databento.com</c> rather than inferred: resolving
/// <c>ESH4,ESM4,NOTAREALSYMBOL</c> returns
/// <c>"result":{"ESH4":[…],"ESM4":[…],"NOTAREALSYMBOL":[]}</c> alongside
/// <c>"not_found":["NOTAREALSYMBOL"]</c>. So
/// <see cref="System.Collections.Generic.IReadOnlyDictionary{TKey,TValue}.ContainsKey"/> answers
/// "did I ask for this", never "did this resolve": an empty interval list is what says nothing
/// resolved, and <see cref="NotFound"/> and <see cref="Partial"/> are what name the shortfall.
/// </para>
/// <para>
/// <b>Nothing resolving is not an error.</b> That same response arrived as HTTP 200, with
/// <c>"status": 2, "message": "Not found"</c> in the body — fields upstream's own deserializer
/// ignores and this port ignores with it. No <see cref="DatabentoApiException"/> is thrown for it,
/// so <see cref="NotFound"/> is the only signal a caller gets that a symbol was rejected.
/// </para>
/// </remarks>
public sealed record Resolution
{
    /// <summary>
    /// Each requested symbol, mapped to the intervals it resolved over — empty for a symbol that
    /// resolved to nothing. Interval dates are half-open, as
    /// <see cref="MappingInterval"/> documents.
    /// </summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<MappingInterval>> Mappings { get; init; }

    /// <summary>
    /// The symbols that resolved for part of the requested range but not all of it. These appear
    /// in <see cref="Mappings"/> as well, carrying the intervals they did resolve over.
    /// </summary>
    /// <remarks>
    /// <b>Unlike the rest of this type's remarks, that second sentence is inferred rather than
    /// probed.</b> It follows from <c>result</c> holding a key for every requested symbol, which
    /// was verified — including for a symbol that resolved to nothing at all, the harder case. A
    /// partial resolution itself could not be produced to check directly: raw symbols resolve
    /// across the whole requested window even outside a contract's listed life, and a range
    /// starting before a dataset's first day is refused with HTTP 422 rather than partially
    /// resolved. The mock covers this bucket with a body marked synthetic for that reason.
    /// </remarks>
    public required IReadOnlyList<string> Partial { get; init; }

    /// <summary>
    /// The symbols that did not resolve at all. These appear in <see cref="Mappings"/> too, with
    /// an empty interval list.
    /// </summary>
    public required IReadOnlyList<string> NotFound { get; init; }

    /// <summary>The symbology the request's symbols were expressed in.</summary>
    /// <remarks>
    /// <para>
    /// <b>Echoed from the request that produced this resolution, not read from the response.</b>
    /// Upstream does the same (<c>symbology.rs:47-48</c>), and it is the right call — but not for
    /// the reason it is easy to assume. The response <em>does</em> carry <c>stype_in</c> and
    /// <c>stype_out</c>; a live body reads
    /// <c>{"result":{…},"symbols":[…],"stype_in":"raw_symbol","stype_out":"instrument_id",…}</c>.
    /// </para>
    /// <para>
    /// It is echoed anyway because these two values are what makes <see cref="ToSymbolMap"/>
    /// readable in the right direction, and the request is where the caller's intent actually
    /// lives. Reading the server's echo instead would buy nothing and add a way for this object to
    /// disagree with the request that produced it.
    /// </para>
    /// </remarks>
    public required SType StypeIn { get; init; }

    /// <summary>
    /// The symbology the symbols resolved <em>to</em>. Echoed from the request, for the reason
    /// <see cref="StypeIn"/> gives.
    /// </summary>
    public required SType StypeOut { get; init; }

    /// <summary>
    /// Builds a timestamped symbol map from this resolution — instrument id and date to text
    /// symbol.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of upstream's <c>Resolution::symbol_map</c> (<c>symbology.rs:150-186</c>). This is the
    /// end of the <c>ALL_SYMBOLS</c> workflow <see cref="ResolveParams.FromMetadata"/> starts: a
    /// stream that named every instrument by id becomes readable, because the map this returns is
    /// the same <see cref="TsSymbolMap"/> the decoder side already takes.
    /// </para>
    /// <para>
    /// <b>Which side of a mapping holds the instrument id depends on <see cref="StypeIn"/>.</b>
    /// When the request resolved <em>from</em> instrument ids, the dictionary keys are the ids and
    /// the intervals carry the text symbols; otherwise the keys are the text symbols and the
    /// intervals carry the ids. A map is always keyed by id, so one of the two is parsed as a
    /// number — and which one flips with the request.
    /// </para>
    /// </remarks>
    /// <returns>A map from instrument id and date to the symbol in the other symbology.</returns>
    /// <exception cref="FormatException">
    /// A value that has to be an instrument id is not a number — the key when
    /// <see cref="StypeIn"/> is <see cref="SType.InstrumentId"/>, and
    /// <see cref="MappingInterval.Symbol"/> otherwise. The message names the offending value.
    /// </exception>
    /// <exception cref="DbnDecodeException">
    /// An interval's <see cref="MappingInterval.StartDate"/> is after its
    /// <see cref="MappingInterval.EndDate"/>, which <see cref="TsSymbolMap.Insert"/> refuses. Two
    /// intervals that <em>overlap</em> are not an error: the later insert wins for the shared
    /// days, matching how the same map is built from a stream's own metadata.
    /// </exception>
    public TsSymbolMap ToSymbolMap()
    {
        var map = new TsSymbolMap();
        var resolvingFromIds = StypeIn == SType.InstrumentId;

        foreach (var (symbol, intervals) in Mappings)
        {
            var keyId = resolvingFromIds ? ParseInstrumentId(symbol) : default;

            foreach (var interval in intervals)
            {
                var instrumentId = resolvingFromIds ? keyId : ParseInstrumentId(interval.Symbol);
                var text = resolvingFromIds ? interval.Symbol : symbol;
                map.Insert(instrumentId, interval.StartDate, interval.EndDate, text);
            }
        }

        return map;
    }

    private static uint ParseInstrumentId(string value) =>
        uint.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
            ? id
            : throw new FormatException($"'{value}' is not an instrument ID.");
}
