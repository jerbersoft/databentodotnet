using System.Text.Json;
using System.Text.Json.Serialization;

namespace DatabentoDotNet.Historical.Json;

/// <summary>Reads and writes <see cref="FeedMode"/> as its wire string.</summary>
/// <remarks>
/// Upstream gives this enum a <c>Deserialize</c> impl and no <c>Serialize</c>
/// (<c>metadata.rs:393-398</c>), because the client only ever receives one. <see cref="Write"/>
/// exists so the type is usable in a consumer's own serialization; nothing in this library calls
/// it.
/// </remarks>
public sealed class FeedModeJsonConverter : JsonConverter<FeedMode>
{
    /// <inheritdoc/>
    public override FeedMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return MetadataWireStrings.TryParseFeedMode(value, out var mode)
            ? mode
            : throw new JsonException($"'{value}' is not a feed mode this library can name.");
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, FeedMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWireString());
}
