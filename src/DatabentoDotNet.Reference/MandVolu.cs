using System.Text.Json.Serialization;
using DatabentoDotNet.Reference.Json;

namespace DatabentoDotNet.Reference;

/// <summary>
/// Mandatory or voluntary.
/// </summary>
/// <remarks>
/// <para>
/// One of the nine closed reference enums: byte-backed, one character on the wire, and an
/// unrecognised code is an error rather than an opaque value. <see cref="ReferenceWireStrings"/>
/// carries the argument for all three.
/// </para>
/// <para>
/// <b><see cref="MandVolu"/> names both this type and its third member</b>, which is upstream's
/// spelling and is kept. C# permits an enum member whose name equals its enclosing type — the
/// restriction that forbids it for a class member does not extend to enums — so the only cost is
/// that the member has to be written <c>MandVolu.MandVolu</c>, which reads no worse than the
/// alternative of inventing a name upstream does not use.
/// </para>
/// <para>
/// Ported from <c>databento-rs/src/reference/enums.rs:3102</c>, and checked against the
/// <c>MANDVOLU</c> group of the vendored <c>corporate_actions.list_enums</c> response, which
/// reports the same three codes.
/// </para>
/// </remarks>
[JsonConverter(typeof(MandVoluJsonConverter))]
public enum MandVolu : byte
{
    /// <summary>Mandatory (<c>M</c>).</summary>
    Mandatory = (byte)'M',

    /// <summary>Voluntary (<c>V</c>).</summary>
    Voluntary = (byte)'V',

    /// <summary>Mandatory and/or voluntary (<c>W</c>).</summary>
    MandVolu = (byte)'W',
}
