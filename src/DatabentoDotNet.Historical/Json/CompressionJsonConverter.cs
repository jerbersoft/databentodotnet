using System.Text.Json;
using System.Text.Json.Serialization;
using DatabentoDotNet.Dbn;

namespace DatabentoDotNet.Historical.Json;

/// <summary>
/// Reads and writes <see cref="Compression"/> as the codec's wire string, reading JSON <c>null</c>
/// as <see cref="Compression.None"/>.
/// </summary>
/// <remarks>
/// <para>
/// The <c>null</c> case is upstream's <c>deserialize_compression</c> (<c>batch.rs:768-774</c>),
/// whose comment reads "Handles <c>Compression::None</c> being serialized as null in JSON". #39
/// confirmed it: a CSV batch job submitted without compression comes back with
/// <c>"compression":null</c> from both <c>batch.list_jobs</c> and <c>batch.get_job_details</c>.
/// </para>
/// <para>
/// <see cref="JsonConverter{T}.HandleNull"/> is overridden for the reason
/// <see cref="SplitDurationJsonConverter"/> gives — without it the <c>null</c> branch below is
/// unreachable, and the gap shows up only against a job that used no compression.
/// </para>
/// </remarks>
public sealed class CompressionJsonConverter : JsonConverter<Compression>
{
    /// <summary>
    /// <see langword="true"/>: a <c>null</c> token is a value here, not an absence.
    /// </summary>
    public override bool HandleNull => true;

    /// <inheritdoc/>
    public override Compression Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return Compression.None;
        }

        return WireStrings.TryParseCompression(reader.GetString(), out var compression)
            ? compression
            : throw new JsonException("The value is not a compression this library can name.");
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, Compression value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWireString());
}
