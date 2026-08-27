using DatabentoDotNet.Dbn;

namespace DatabentoDotNet;

/// <summary>Which of the three forms a <see cref="Symbols"/> set takes.</summary>
/// <remarks>
/// The discriminant of upstream's <c>Symbols</c> sum type, made explicit because C# has no sum
/// types. It is not a wire value — nothing in the live protocol carries it — so it has no entry
/// in the codec's wire-string tables and no defined numbering.
/// </remarks>
public enum SymbolsKind
{
    /// <summary>
    /// A <see langword="default"/> <see cref="Symbols"/>, which names nothing and cannot be sent.
    /// Upstream has no equivalent: a Rust <c>enum</c> has no zero value, and a C# struct always
    /// has one.
    /// </summary>
    None = 0,

    /// <summary>Every symbol in the dataset — <see cref="Symbols.AllWireValue"/> on the wire.</summary>
    All,

    /// <summary>Symbols named as text, in whatever symbology the subscription's <c>stype_in</c> names.</summary>
    Symbols,

    /// <summary>Symbols named by numeric instrument ID, to pair with <see cref="SType.InstrumentId"/>.</summary>
    Ids,
}
