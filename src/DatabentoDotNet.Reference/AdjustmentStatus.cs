using System.Text.Json.Serialization;
using DatabentoDotNet.Reference.Json;

namespace DatabentoDotNet.Reference;

/// <summary>
/// The adjustment status.
/// </summary>
/// <remarks>
/// <para>
/// One of the nine closed reference enums: byte-backed, one character on the wire, and an
/// unrecognised code is an error rather than an opaque value. <see cref="ReferenceWireStrings"/>
/// carries the argument for all three.
/// </para>
/// <para>
/// <b>This is the one of the nine with no independent check, and that is expected rather than an
/// omission.</b> The other eight are confirmed against the vendored
/// <c>corporate_actions.list_enums</c> response, but that endpoint documents the <em>corporate
/// actions</em> dictionary and this is an <c>adjustment_factors</c> field — there is no
/// <c>ADJSTATUS</c> group in it, and the test suite asserts that absence rather than assuming it.
/// The table below is transcribed from <c>databento-rs/src/reference/enums.rs:79</c> and nothing
/// else; #57 is what will confirm it against real <c>adjustment_factors</c> rows.
/// </para>
/// </remarks>
[JsonConverter(typeof(AdjustmentStatusJsonConverter))]
public enum AdjustmentStatus : byte
{
    /// <summary>Apply (<c>A</c>).</summary>
    Apply = (byte)'A',

    /// <summary>Rescind (<c>R</c>).</summary>
    Rescind = (byte)'R',

    /// <summary>Pending (<c>P</c>).</summary>
    Pending = (byte)'P',
}
