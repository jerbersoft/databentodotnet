using System.Text.Json.Serialization;
using DatabentoDotNet.Reference.Json;

namespace DatabentoDotNet.Reference;

/// <summary>
/// How fractions are handled at settlement.
/// </summary>
/// <remarks>
/// <para>
/// One of the nine closed reference enums: byte-backed, one character on the wire, and an
/// unrecognised code is an error rather than an opaque value. <see cref="ReferenceWireStrings"/>
/// carries the argument for all three.
/// </para>
/// <para>
/// <b>A blank is legal here, and it is not a member.</b> The dictionary lists a null-code entry
/// described as "A Blank value is possible" in both groups that report these codes, and upstream
/// models the field as <c>Option&lt;Fraction&gt;</c> (<c>corporate.rs:373</c>). So the field a
/// response model declares is <see cref="Nullable{T}"/> of this type carrying
/// <see cref="Json.NullableFractionJsonConverter"/>, which reads a blank as <see langword="null"/>.
/// <see cref="Json.FractionJsonConverter"/>, the one this type carries by attribute, rejects a
/// blank — a non-nullable field has no way to say "no value".
/// </para>
/// <para>
/// Ported from <c>databento-rs/src/reference/enums.rs:2741</c>, and checked against <b>both</b> the
/// <c>FRACCD</c> and <c>FRACTIONS</c> groups of the vendored <c>corporate_actions.list_enums</c>
/// response. Those are two groups with the same four codes and descriptions that differ only in
/// punctuation (<c>Round-Down</c> against <c>Round Down</c>); that they agree is asserted, so if
/// they ever stop agreeing the failure names which one moved.
/// </para>
/// </remarks>
[JsonConverter(typeof(FractionJsonConverter))]
public enum Fraction : byte
{
    /// <summary>Cash (<c>C</c>).</summary>
    Cash = (byte)'C',

    /// <summary>Round down (<c>D</c>).</summary>
    RoundDown = (byte)'D',

    /// <summary>Fractions (<c>F</c>).</summary>
    Fractions = (byte)'F',

    /// <summary>Round up (<c>U</c>).</summary>
    RoundUp = (byte)'U',
}
