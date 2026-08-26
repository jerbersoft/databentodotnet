namespace DatabentoDotNet.Dbn;

/// <summary>
/// Whether the instrument is user-defined.
/// </summary>
/// <remarks>
/// Char-valued: each variant's numeric value is its ASCII character code, and that character is
/// the only wire/text form this type has — there is no separate string representation. Use
/// <see cref="WireStrings.ToChar(UserDefinedInstrument)"/> to read it as a <see cref="char"/>.
/// Rust marks <see cref="No"/> as the type's default, but C#'s implicit
/// <c>default(UserDefinedInstrument)</c> is the zero value <c>(UserDefinedInstrument)0</c>,
/// which has no name here — reference <see cref="No"/> explicitly where upstream's default
/// matters.
/// </remarks>
public enum UserDefinedInstrument : byte
{
    /// <summary>The instrument is not user-defined.</summary>
    No = (byte)'N',

    /// <summary>The instrument is user-defined.</summary>
    Yes = (byte)'Y',
}
