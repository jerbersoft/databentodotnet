namespace DatabentoDotNet.Dbn;

/// <summary>
/// The type of <c>InstrumentDefMsg</c> update.
/// </summary>
/// <remarks>
/// Char-valued: each variant's numeric value is its ASCII character code, and that character is
/// the only wire/text form this type has — there is no separate string representation. Use
/// <see cref="WireStrings.ToChar(SecurityUpdateAction)"/> to read it as a <see cref="char"/>.
/// Rust marks <see cref="Add"/> as the type's default, but C#'s implicit
/// <c>default(SecurityUpdateAction)</c> is the zero value <c>(SecurityUpdateAction)0</c>, which
/// has no name here — reference <see cref="Add"/> explicitly where upstream's default matters.
/// </remarks>
public enum SecurityUpdateAction : byte
{
    /// <summary>A new instrument definition.</summary>
    Add = (byte)'A',

    /// <summary>A modified instrument definition of an existing one.</summary>
    Modify = (byte)'M',

    /// <summary>Removal of an instrument definition.</summary>
    Delete = (byte)'D',

    /// <summary>
    /// Deprecated upstream since <c>dbn</c> 0.3.0 but still present in legacy files; retained
    /// here for decode compatibility, not as a value new code should emit.
    /// </summary>
    Invalid = (byte)'~',
}
