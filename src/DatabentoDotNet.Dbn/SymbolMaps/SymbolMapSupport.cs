using System.Globalization;
using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// Logic shared by <see cref="TsSymbolMap"/> and <see cref="PitSymbolMap"/> when they build
/// themselves from <see cref="Metadata"/>.
/// </summary>
/// <remarks>
/// Not public: both callers live in this directory, and neither exposes this type through its
/// own surface. See <c>CStr.cs</c> for the same "internal helper gets its own file" shape used
/// elsewhere in this codec.
/// </remarks>
internal static class SymbolMapSupport
{
    /// <summary>
    /// Decides which of <see cref="SymbolMapping.RawSymbol"/> and each
    /// <see cref="MappingInterval.Symbol"/> is the human-readable symbol versus the instrument-ID
    /// string, for the given metadata.
    /// </summary>
    /// <remarks>
    /// Port of upstream's <c>Metadata::is_inverse</c> (<c>metadata.rs:183-194</c>). A symbol map
    /// can only be built when one side of the query's symbology is
    /// <see cref="SType.InstrumentId"/>: the common case is <c>stype_out</c>, which resolves raw
    /// symbols to IDs; the inverse case is <c>stype_in</c>, which resolves IDs to raw symbols. Not
    /// checking this and always assuming the common case would silently swap key and value on an
    /// inverse query — and worse, would silently <em>succeed</em> whenever the swapped field
    /// happens to parse as a <see cref="uint"/>, rather than fail loudly.
    /// </remarks>
    /// <param name="metadata">The metadata to inspect.</param>
    /// <returns>
    /// <see langword="true"/> when <c>stype_in</c> is <see cref="SType.InstrumentId"/> (the
    /// inverse case); <see langword="false"/> when <c>stype_out</c> is (the common case).
    /// </returns>
    /// <exception cref="DbnDecodeException">
    /// Neither <see cref="Metadata.StypeIn"/> nor <see cref="Metadata.StypeOut"/> is
    /// <see cref="SType.InstrumentId"/>, so no symbol map can be built from this metadata.
    /// </exception>
    internal static bool IsInverse(Metadata metadata)
    {
        if (metadata.StypeOut == SType.InstrumentId)
        {
            return false;
        }

        if (metadata.StypeIn == SType.InstrumentId)
        {
            return true;
        }

        throw new DbnDecodeException(
            $"Cannot build a symbol map: neither stype_in ({metadata.StypeIn?.ToString() ?? "none"}) "
            + $"nor stype_out ({metadata.StypeOut}) is InstrumentId.");
    }

    /// <summary>
    /// Parses a wire-format instrument-ID string, as found in either
    /// <see cref="SymbolMapping.RawSymbol"/> or <see cref="MappingInterval.Symbol"/> depending on
    /// <see cref="IsInverse"/>.
    /// </summary>
    /// <param name="text">The string to parse.</param>
    /// <returns>The parsed instrument ID.</returns>
    /// <exception cref="DbnDecodeException"><paramref name="text"/> is not a valid <see cref="uint"/>.</exception>
    internal static uint ParseInstrumentId(string text)
    {
        if (!uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var instrumentId))
        {
            throw new DbnDecodeException($"Cannot build a symbol map: '{text}' is not a valid instrument ID.");
        }

        return instrumentId;
    }

    /// <summary>
    /// Reads <see cref="RecordHeader.InstrumentId"/> off a record whose type is only known as a
    /// type parameter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IRecord{TSelf}"/> exposes <see cref="IRecord{TSelf}.IndexTs"/> but not the
    /// header, because every record struct declares its header as a <em>field</em> named
    /// <c>Header</c> and a C# struct cannot have a field and an interface property of the same
    /// name. Adding the header to the interface would therefore mean renaming that field on all
    /// twenty-one structs — a breaking change to the whole record surface, to reach a value that
    /// is already at a known offset.
    /// </para>
    /// <para>
    /// <b>That offset is zero, and it is a tested invariant rather than an assumption.</b>
    /// <c>RecordLayoutTests.AssertWireSize</c> asserts <c>OffsetOf&lt;T&gt;("Header") == 0</c> for
    /// every record type, and <c>EveryRecordStructInTheAssembly_IsInTheWireSizeList</c> fails if
    /// a record type is missing from that sweep. <see cref="WithTsOut{T}"/>'s constructor already
    /// writes <c>hd.length</c> through the same invariant.
    /// </para>
    /// <para>
    /// The reinterpret goes through a <see cref="ReadOnlySpan{T}"/> over the record rather than
    /// <c>Unsafe.AsRef</c>, so nothing here ever holds a writable reference to a record the
    /// caller passed by <see langword="in"/>. It is the same shape <see cref="RecordRef.Header"/>
    /// uses.
    /// </para>
    /// </remarks>
    /// <typeparam name="TRecord">The record struct.</typeparam>
    /// <param name="record">The record to read, in place.</param>
    /// <returns>The record's instrument ID.</returns>
    internal static uint InstrumentIdOf<TRecord>(in TRecord record)
        where TRecord : unmanaged, IRecord<TRecord>
        => MemoryMarshal.AsRef<RecordHeader>(
            MemoryMarshal.AsBytes(new ReadOnlySpan<TRecord>(in record))).InstrumentId;
}
