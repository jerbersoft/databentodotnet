using System.Text.Json;
using System.Text.Json.Serialization;
using DatabentoDotNet.Dbn;

namespace DatabentoDotNet.Historical.Json;

/// <summary>Reads and writes <see cref="SType"/> as the codec's wire string.</summary>
/// <remarks>
/// <para>
/// The symbology types travel on every request this library sends, but until <c>batch.*</c> no
/// response carried one: <c>symbology.resolve</c> echoes both and this library reads neither,
/// taking them from the request instead (see <see cref="Resolution.StypeIn"/> for why). A batch job
/// is the first response whose symbology types are the only record of what was asked for, months
/// after the request object is gone.
/// </para>
/// <para>
/// Parse-only aliases are <see cref="WireStrings.TryParseSType"/>'s business, not this converter's;
/// an unrecognised value throws for the reason <see cref="SchemaJsonConverter"/> gives.
/// </para>
/// </remarks>
public sealed class STypeJsonConverter : JsonConverter<SType>
{
    /// <inheritdoc/>
    public override SType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        WireStrings.TryParseSType(reader.GetString(), out var stype)
            ? stype
            : throw new JsonException("The value is not a symbology type this library can name.");

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, SType value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWireString());
}
