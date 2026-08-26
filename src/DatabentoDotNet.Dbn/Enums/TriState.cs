namespace DatabentoDotNet.Dbn;

/// <summary>
/// Represents an unknown, true, or false value. Equivalent to a nullable <see cref="bool"/> but
/// with a human-readable wire representation.
/// </summary>
/// <remarks>
/// Char-valued: each variant's numeric value is its ASCII character code, and that character is
/// the only wire/text form this type has — there is no separate string representation. Use
/// <see cref="WireStrings.ToChar(TriState)"/> to read it as a <see cref="char"/>. Rust marks
/// <see cref="NotAvailable"/> as the type's default, but C#'s implicit <c>default(TriState)</c>
/// is the zero value <c>(TriState)0</c>, which has no name here — <see cref="NotAvailable"/>'s
/// wire value is <c>'~'</c> (<c>0x7E</c>), not <c>0</c>. Reference <see cref="NotAvailable"/>
/// explicitly where upstream's default matters.
/// </remarks>
public enum TriState : byte
{
    /// <summary>The value is not applicable or not known. Equivalent to <see langword="null"/>.</summary>
    NotAvailable = (byte)'~',

    /// <summary>False.</summary>
    No = (byte)'N',

    /// <summary>True.</summary>
    Yes = (byte)'Y',
}
