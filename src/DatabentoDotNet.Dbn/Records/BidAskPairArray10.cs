using System.Runtime.CompilerServices;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// A ten-element inline array of <see cref="BidAskPair"/>: the <c>levels</c> field of
/// <see cref="Mbp10Msg"/>.
/// </summary>
/// <remarks>
/// Inline rather than a reference array so the levels stay part of the record's own bytes and
/// the whole record can be reinterpreted in place over the read buffer. Index it directly
/// (<c>msg.Levels[3]</c>); it also converts implicitly to a span.
/// </remarks>
[InlineArray(10)]
public struct BidAskPairArray10
{
    private BidAskPair _element0;
}
