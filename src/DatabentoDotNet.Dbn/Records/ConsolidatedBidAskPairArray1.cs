using System.Runtime.CompilerServices;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// A one-element inline array of <see cref="ConsolidatedBidAskPair"/>: the <c>levels</c> field of
/// <see cref="Cmbp1Msg"/> and <see cref="CbboMsg"/>.
/// </summary>
/// <remarks>
/// Inline rather than a reference array so the levels stay part of the record's own bytes and
/// the whole record can be reinterpreted in place over the read buffer. Index it directly
/// (<c>msg.Levels[0]</c>); it also converts implicitly to a span.
/// </remarks>
[InlineArray(1)]
public struct ConsolidatedBidAskPairArray1
{
    private ConsolidatedBidAskPair _element0;
}
