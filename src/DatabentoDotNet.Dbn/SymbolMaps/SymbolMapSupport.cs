using System.Globalization;

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
}
