using System.Text.Json;
using System.Text.Json.Serialization;

namespace DatabentoDotNet.Historical.Json;

/// <summary>Reads and writes <see cref="DatasetCondition"/> as its wire string.</summary>
/// <remarks>
/// Upstream gives this enum a <c>Deserialize</c> impl and no <c>Serialize</c>
/// (<c>metadata.rs:434-439</c>), because the client only ever receives one. <see cref="Write"/>
/// exists so the type is usable in a consumer's own serialization; nothing in this library calls
/// it.
/// </remarks>
public sealed class DatasetConditionJsonConverter : JsonConverter<DatasetCondition>
{
    /// <inheritdoc/>
    public override DatasetCondition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return MetadataWireStrings.TryParseDatasetCondition(value, out var condition)
            ? condition
            : throw new JsonException($"'{value}' is not a dataset condition this library can name.");
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, DatasetCondition value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWireString());
}
