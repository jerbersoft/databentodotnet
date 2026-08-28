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
    /// <c>type</c> on the wire (<c>metadata.rs:264</c>). <c>type</c> is not a C# keyword — a
    /// property named <c>Type</c> compiles cleanly, and <c>SnakeCaseLower</c> would map it to
    /// <c>type</c> with no <see cref="JsonPropertyNameAttribute"/> needed at all. This is named
    /// <c>TypeName</c> instead, with the attribute below to still hit the wire spelling: <c>Type</c>
    /// reads as <see cref="System.Type"/>, and <c>TypeName</c> mirrors upstream's own
    /// <c>type_name</c> (<c>metadata.rs:265</c>).
    /// </remarks>
    [JsonPropertyName("type")]
    public required string TypeName { get; init; }
}
