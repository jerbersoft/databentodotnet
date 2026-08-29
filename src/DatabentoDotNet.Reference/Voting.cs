using System.Text.Json.Serialization;
using DatabentoDotNet.Reference.Json;

namespace DatabentoDotNet.Reference;

/// <summary>
/// The type of voting.
/// </summary>
/// <remarks>
/// <para>
/// One of the nine closed reference enums: byte-backed, one character on the wire, and an
/// unrecognised code is an error rather than an opaque value. <see cref="ReferenceWireStrings"/>
/// carries the argument for all three.
/// </para>
/// <para>
/// <b><see cref="Voting"/> names both this type and its fourth member</b>, which is upstream's
/// spelling and is kept, for the reason <see cref="MandVolu"/> gives.
/// </para>
/// <para>
/// <b>Upstream declares the field <c>Option&lt;Voting&gt;</c> (<c>security.rs:283</c>), but the
/// dictionary lists no blank for it.</b> That is not a contradiction: an <c>Option</c> in serde
/// also covers a field that is absent or JSON <see langword="null"/>, which is a different thing
/// from the "A Blank value is possible" entry <c>FRACCD</c>, <c>FRACTIONS</c> and <c>PAYTYPE</c>
/// carry and the <c>VOTING</c> group does not. A <c>Voting?</c> response field therefore needs no
/// second converter — <see cref="System.Text.Json"/> answers a null token for a
/// <see cref="Nullable{T}"/> itself, without reaching <see cref="Json.VotingJsonConverter"/> at
/// all. Only the empty <em>string</em> needs one, and only the two enums the dictionary says may be
/// blank can receive it.
/// </para>
/// <para>
/// Ported from <c>databento-rs/src/reference/enums.rs:3456</c>, and checked against the
/// <c>VOTING</c> group of the vendored <c>corporate_actions.list_enums</c> response, which reports
/// the same four codes.
/// </para>
/// </remarks>
[JsonConverter(typeof(VotingJsonConverter))]
public enum Voting : byte
{
    /// <summary>Limited voting (<c>L</c>).</summary>
    Limited = (byte)'L',

    /// <summary>Multiple voting (<c>M</c>).</summary>
    Multiple = (byte)'M',

    /// <summary>No voting (<c>N</c>).</summary>
    No = (byte)'N',

    /// <summary>Voting (<c>V</c>).</summary>
    Voting = (byte)'V',
}
