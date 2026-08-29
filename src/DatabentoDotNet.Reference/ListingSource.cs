using System.Text.Json.Serialization;
using DatabentoDotNet.Reference.Json;

namespace DatabentoDotNet.Reference;

/// <summary>
/// Listing source.
/// </summary>
/// <remarks>
/// <para>
/// One of the nine closed reference enums: byte-backed, one character on the wire, and an
/// unrecognised code is an error rather than an opaque value. <see cref="ReferenceWireStrings"/>
/// carries the argument for all three.
/// </para>
/// <para>
/// Ported from <c>databento-rs/src/reference/enums.rs:2966</c>, and checked against the
/// <c>LISTSOURCE</c> group of the vendored <c>corporate_actions.list_enums</c> response, which
/// reports the same two codes. A <c>security_master</c> response field.
/// </para>
/// </remarks>
[JsonConverter(typeof(ListingSourceJsonConverter))]
public enum ListingSource : byte
{
    /// <summary>Main WCA supported listing (<c>M</c>).</summary>
    Main = (byte)'M',

    /// <summary>Secondary listing (<c>S</c>).</summary>
    Secondary = (byte)'S',
}
