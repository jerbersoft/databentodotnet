namespace DatabentoDotNet.Dbn;

/// <summary>
/// The side of the market: for resting orders, the side of the book; for trades, the side of
/// the aggressor.
/// </summary>
/// <remarks>
/// Char-valued: each variant's numeric value is its ASCII character code, and that character is
/// the only wire/text form this type has — there is no separate string representation. Use
/// <see cref="WireStrings.ToChar(Side)"/> to read it as a <see cref="char"/>. Rust marks
/// <see cref="None"/> as the type's default, but C#'s implicit <c>default(Side)</c> is the zero
/// value <c>(Side)0</c>, which has no name here — reference <see cref="None"/> explicitly where
/// upstream's default matters.
/// </remarks>
public enum Side : byte
{
    /// <summary>A sell order, or the sell side of a trade's aggressor.</summary>
    Ask = (byte)'A',

    /// <summary>A buy order, or the buy side of a trade's aggressor.</summary>
    Bid = (byte)'B',

    /// <summary>No side specified by the original source.</summary>
    None = (byte)'N',
}
