using System.Text.Json;
using System.Text.Json.Serialization;

namespace DatabentoDotNet.Historical.Json;

/// <summary>Reads and writes <see cref="Delivery"/> as its wire string.</summary>
/// <remarks>
/// One defined value, so this converter's only interesting behaviour is the failure: an
/// unrecognised mechanism throws rather than reading as <see cref="Delivery.Download"/>. Upstream
/// documents download as the only mechanism "at this time" (<c>batch.rs:415-416</c>), and the day
/// that stops being true a job delivered some other way must not come back claiming this library
/// can fetch its files.
/// </remarks>
public sealed class DeliveryJsonConverter : JsonConverter<Delivery>
{
    /// <inheritdoc/>
    public override Delivery Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Parse(reader.GetString());

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, Delivery value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWireString());

    private static Delivery Parse(string? value) =>
        BatchWireStrings.TryParseDelivery(value, out var delivery)
            ? delivery
            : throw new JsonException($"'{value}' is not a delivery mechanism this library can name.");
}
