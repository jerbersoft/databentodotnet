namespace DatabentoDotNet.Dbn.Enums;

/// <summary>
/// The type of matching algorithm used for the instrument at the exchange.
/// </summary>
/// <remarks>
/// Char-valued: each variant's numeric value is its ASCII character code, and that character is
/// the only wire/text form this type has — there is no separate string representation. Use
/// <see cref="WireStrings.ToChar(MatchAlgorithm)"/> to read it as a <see cref="char"/>. The char
/// values are not alphabetical or sequential, and several letters coincide with unrelated
/// meanings in other enums (e.g. <c>'C'</c> here is <see cref="ProRata"/>, but <c>'C'</c> in
/// <see cref="InstrumentClass"/> is <see cref="InstrumentClass.Call"/>) — these are separate
/// byte spaces. Rust marks <see cref="Undefined"/> as the type's default, but C#'s implicit
/// <c>default(MatchAlgorithm)</c> is the zero value <c>(MatchAlgorithm)0</c>, which has no name
/// here — reference <see cref="Undefined"/> explicitly where upstream's default matters.
/// </remarks>
public enum MatchAlgorithm : byte
{
    /// <summary>No matching algorithm was specified.</summary>
    Undefined = (byte)' ',

    /// <summary>First-in-first-out matching.</summary>
    Fifo = (byte)'F',

    /// <summary>A configurable match algorithm.</summary>
    Configurable = (byte)'K',

    /// <summary>
    /// Trade quantity is allocated to resting orders based on a pro-rata percentage: resting
    /// order quantity divided by total quantity.
    /// </summary>
    ProRata = (byte)'C',

    /// <summary>Like <see cref="Fifo"/> but with LMM allocations prior to FIFO allocations.</summary>
    FifoLmm = (byte)'T',

    /// <summary>
    /// Like <see cref="ProRata"/> but includes a configurable allocation to the first order
    /// that improves the market. Minimum order thresholds may exist.
    /// </summary>
    ThresholdProRata = (byte)'O',

    /// <summary>
    /// Like <see cref="FifoLmm"/> but includes a configurable allocation to the first order
    /// that improves the market.
    /// </summary>
    FifoTopLmm = (byte)'S',

    /// <summary>Like <see cref="ThresholdProRata"/> but includes a special priority to LMMs.</summary>
    ThresholdProRataLmm = (byte)'Q',

    /// <summary>Special variant used only for Eurodollar futures on CME.</summary>
    EurodollarFutures = (byte)'Y',

    /// <summary>
    /// Trade quantity is shared between all orders at the best price. Orders with the highest
    /// time priority receive a higher matched quantity.
    /// </summary>
    TimeProRata = (byte)'P',

    /// <summary>
    /// A two-pass FIFO algorithm. The first pass fills the institutional group the aggressing
    /// order is associated with; the second pass matches orders without one.
    /// </summary>
    InstitutionalPrioritization = (byte)'V',

    /// <summary>
    /// Like <see cref="ProRata"/>, but includes a configurable allocation to the first order
    /// that improves the market.
    /// </summary>
    Allocation = (byte)'A',
}
