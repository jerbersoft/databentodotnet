using System.Text.Json.Serialization;
using DatabentoDotNet.Reference.Json;

namespace DatabentoDotNet.Reference;

/// <summary>
/// The global status code. Indicates the global listing activity status of a security.
/// </summary>
/// <remarks>
/// <para>
/// One of the nine closed reference enums: byte-backed, one character on the wire, and an
/// unrecognised code is an error rather than an opaque value. <see cref="ReferenceWireStrings"/>
/// carries the argument for all three.
/// </para>
/// <para>
/// Ported from <c>databento-rs/src/reference/enums.rs:2910</c>, and checked against the
/// <c>GLOBSTATUS</c> group of the vendored <c>corporate_actions.list_enums</c> response, which
/// reports the same three codes.
/// </para>
/// </remarks>
[JsonConverter(typeof(GlobalStatusJsonConverter))]
public enum GlobalStatus : byte
{
    /// <summary>Active (<c>A</c>).</summary>
    Active = (byte)'A',

    /// <summary>In default (<c>D</c>).</summary>
    InDefault = (byte)'D',

    /// <summary>Inactive (<c>I</c>).</summary>
    Inactive = (byte)'I',
}
