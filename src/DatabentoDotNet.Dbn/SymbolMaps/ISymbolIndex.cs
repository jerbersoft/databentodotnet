namespace DatabentoDotNet.Dbn;

/// <summary>
/// Resolves a symbol for a decoded record, without the caller having to know which timestamp the
/// record indexes on or which kind of symbol map is answering. Implemented by
/// <see cref="TsSymbolMap"/> and <see cref="PitSymbolMap"/>.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>SymbolIndex</c> trait (<c>symbol_map.rs:79-83</c>). It exists so code
/// can be written against "a symbol map" rather than against one of the two: a live session
/// keeps a <see cref="PitSymbolMap"/> updated record by record, a historical request builds a
/// <see cref="TsSymbolMap"/> from metadata, and a consumer that wants a symbol for the record in
/// front of it should not have to care which it was handed.
/// </para>
/// <para>
/// <b>The two implementations answer differently, and that is the design.</b>
/// <see cref="TsSymbolMap"/> keys on the record's own index date; <see cref="PitSymbolMap"/>
/// ignores the record's timestamp entirely and keys on the instrument ID alone, because the
/// caller already committed to one date when the map was built. Both match upstream's own impls
/// (<c>symbol_map.rs:165-171, 336-340</c>). A <see cref="PitSymbolMap"/> that started consulting
/// the record's date would not be a point-in-time map any more.
/// </para>
/// <para>
/// <b>There is no indexer, deliberately.</b> Upstream pairs <c>get_for_rec</c> with
/// <c>Index&lt;&amp;R&gt;</c> impls (<c>symbol_map.rs:342-364</c>) that <c>unwrap()</c>, so a
/// miss panics; a C# indexer carries the same expectation, since that is what
/// <see cref="System.Collections.Generic.Dictionary{TKey, TValue}"/> does. But a miss here is
/// routine, not exceptional — a live stream resolves nothing for an instrument until its mapping
/// record arrives, and a timeseries map holds nothing for a date outside the query's range. An
/// indexer would make the ordinary case throw, so the whole surface is <c>Try*</c>.
/// </para>
/// </remarks>
public interface ISymbolIndex
{
    /// <summary>Looks up the symbol for a decoded record.</summary>
    /// <remarks>
    /// This is the overload a decoder loop uses, since <see cref="RecordRef"/> is what
    /// <c>TryNextRecord</c> hands out and no downcast is needed to resolve a symbol.
    /// </remarks>
    /// <param name="record">The record to resolve.</param>
    /// <param name="symbol">
    /// Receives the resolved symbol, or <see langword="null"/> when the map has no mapping for
    /// this record.
    /// </param>
    /// <returns><see langword="true"/> if a mapping was found.</returns>
    bool TryGetSymbol(RecordRef record, out string? symbol);

    /// <summary>Looks up the symbol for a decoded record of a known type.</summary>
    /// <remarks>
    /// For a record that has been downcast or copied out of the read buffer — one held in a
    /// collection, say, where no <see cref="RecordRef"/> survives to call the other overload
    /// with. Takes the record by <see langword="in"/> so a 520-byte
    /// <see cref="InstrumentDefMsg"/> is read in place rather than copied to be asked its
    /// symbol.
    /// </remarks>
    /// <typeparam name="TRecord">The record struct.</typeparam>
    /// <param name="record">The record to resolve.</param>
    /// <param name="symbol">
    /// Receives the resolved symbol, or <see langword="null"/> when the map has no mapping for
    /// this record.
    /// </param>
    /// <returns><see langword="true"/> if a mapping was found.</returns>
    bool TryGetSymbol<TRecord>(in TRecord record, out string? symbol)
        where TRecord : unmanaged, IRecord<TRecord>;
}
