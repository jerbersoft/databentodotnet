using System.Text.Json.Serialization;

namespace DatabentoDotNet.Historical;

/// <summary>The details about a field in a schema.</summary>
/// <remarks>
/// Port of upstream's <c>FieldDetail</c> (<c>databento-rs/src/historical/metadata.rs:259-266</c>).
/// Returned by <c>metadata.list_fields</c>, one entry per field that an encoding/schema
/// combination carries.
/// </remarks>
public sealed record FieldDetail
{
    /// <summary>The field name.</summary>
    public required string Name { get; init; }

    /// <summary>The field type name.</summary>
    /// <remarks>
    /// <c>type</c> on the wire (<c>metadata.rs:264</c>) — one of the two renames this endpoint
    /// group needs, because <c>type</c> is a C# keyword and the property cannot simply be called
    /// <c>Type</c>.
    /// </remarks>
    [JsonPropertyName("type")]
    public required string TypeName { get; init; }
}
