using System.Text.Json;
using System.Text.Json.Serialization;
using DatabentoDotNet.Dbn;

namespace DatabentoDotNet.Historical.Json;

/// <summary>Reads and writes <see cref="Encoding"/> as the codec's wire string.</summary>
/// <remarks>
/// <para>
/// Needed by <see cref="BatchJob.Encoding"/> and by nothing else in this library: every other
/// request hard-codes <c>dbn</c> and no other response carries the field. A batch job is the one
/// place a caller chooses, so it is the one place the choice comes back.
/// </para>
/// <para>
/// <c>dbz</c> — the pre-rename file extension — parses as <see cref="Encoding.Dbn"/> and is never
/// written, which is <see cref="WireStrings.TryParseEncoding"/>'s behaviour rather than this
/// converter's.
/// </para>
/// </remarks>
public sealed class EncodingJsonConverter : JsonConverter<Encoding>
{
    /// <inheritdoc/>
    public override Encoding Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        WireStrings.TryParseEncoding(reader.GetString(), out var encoding)
            ? encoding
            : throw new JsonException("The value is not an encoding this library can name.");

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, Encoding value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWireString());
}
