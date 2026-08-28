using System.Text.Json;
using System.Text.Json.Serialization;
using DatabentoDotNet.Dbn;

namespace DatabentoDotNet.Historical.Json;

/// <summary>
/// Reads and writes <see cref="Schema"/> as the codec's wire string, in both the value and the
/// property-name position.
/// </summary>
/// <remarks>
/// <para>
/// <b>The property-name half is the point.</b> Two <c>metadata.*</c> responses are JSON objects
/// keyed by schema — <c>list_unit_prices</c>' <c>unit_prices</c> (<c>metadata.rs:274</c>) and
/// <c>get_dataset_range</c>' <c>schema</c> (<c>metadata.rs:317</c>) — and
/// <see cref="System.Text.Json"/>'s built-in enum key handling matches the C# member name, not the
/// wire string. So <c>{"ohlcv-1s":…}</c>, which is what the API sends, fails without this, while
/// <c>{"Ohlcv1S":…}</c>, which it never sends, succeeds. A test written from the C# names would
/// pass against a converter that does not work.
/// </para>
/// <para>
/// One converter covers <c>List&lt;Schema&gt;</c>, both keyed dictionaries, and any future field,
/// because <see cref="JsonConverter{T}"/> dispatches the key position to
/// <see cref="ReadAsPropertyName"/> separately from <see cref="Read"/>.
/// </para>
/// <para>
/// An unrecognised schema throws, rather than yielding <c>default</c>: the issue's Definition of
/// done requires that "a schema the codec cannot name must be an error at the boundary, not an
/// unmapped enum value that reaches a caller as <c>0</c>" — and <c>0</c> is
/// <see cref="Schema.Mbo"/>, a perfectly ordinary schema, so a silent fallback would be
/// indistinguishable from real data.
/// </para>
/// </remarks>
public sealed class SchemaJsonConverter : JsonConverter<Schema>
{
    /// <inheritdoc/>
    public override Schema Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Parse(reader.GetString());

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, Schema value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWireString());

    /// <inheritdoc/>
    public override Schema ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Parse(reader.GetString());

    /// <inheritdoc/>
    public override void WriteAsPropertyName(Utf8JsonWriter writer, Schema value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.ToWireString());

    private static Schema Parse(string? value) =>
        WireStrings.TryParseSchema(value, out var schema)
            ? schema
            : throw new JsonException($"'{value}' is not a schema this library can name.");
}
