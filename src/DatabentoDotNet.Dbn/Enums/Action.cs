namespace DatabentoDotNet.Dbn.Enums;

/// <summary>
/// An order event or order book operation.
/// </summary>
/// <remarks>
/// Char-valued: each variant's numeric value is its ASCII character code, and that character is
/// the only wire/text form this type has — there is no separate string representation. Use
/// <see cref="WireStrings.ToChar(Action)"/> to read it as a <see cref="char"/>. Rust marks
/// <see cref="None"/> as the type's default, but C#'s implicit <c>default(Action)</c> is the
/// zero value <c>(Action)0</c>, which has no name here — reference <see cref="None"/> explicitly
/// where upstream's default matters.
/// </remarks>
public enum Action : byte
{
    /// <summary>An existing order was modified: price and/or size.</summary>
    Modify = (byte)'M',

    /// <summary>An aggressing order traded. Does not affect the book.</summary>
    Trade = (byte)'T',

    /// <summary>An existing order was filled. Does not affect the book.</summary>
    Fill = (byte)'F',

    /// <summary>An order was fully or partially cancelled.</summary>
    Cancel = (byte)'C',

    /// <summary>A new order was added to the book.</summary>
    Add = (byte)'A',

    /// <summary>Reset the book; clear all orders for an instrument.</summary>
    Clear = (byte)'R',

    /// <summary>Has no effect on the book, but may carry flags or other information.</summary>
    None = (byte)'N',
}
