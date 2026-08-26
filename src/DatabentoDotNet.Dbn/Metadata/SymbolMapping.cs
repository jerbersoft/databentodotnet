namespace DatabentoDotNet.Dbn;

/// <summary>
/// One requested raw symbol and the intervals over which it resolved to an output symbol.
/// </summary>
/// <remarks>
/// A symbol's resolution is not constant over a query range — an instrument can be renamed, and a
/// continuous contract points at a different instrument every roll — so a mapping is a list of
/// dated intervals rather than a single value. A raw symbol that resolved to nothing over the
/// whole range has an empty <see cref="Intervals"/> list and also appears in
/// <see cref="Metadata.NotFound"/>.
/// </remarks>
public sealed class SymbolMapping
{
    /// <summary>The requested symbol, in the stream's input symbology.</summary>
    public required string RawSymbol { get; init; }

    /// <summary>
    /// The dated intervals <see cref="RawSymbol"/> resolved over, in the order they appear on the
    /// wire. Never <see langword="null"/>; empty when the symbol never resolved.
    /// </summary>
    public IReadOnlyList<MappingInterval> Intervals { get; init; } = [];
}
