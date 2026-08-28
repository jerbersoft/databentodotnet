using System.Text.Json;
using System.Text.Json.Serialization;

namespace DatabentoDotNet.Historical.Json;

/// <summary>
/// Reads and writes <see cref="SplitDuration"/> as its wire string, reading JSON <c>null</c> as
/// <see cref="SplitDuration.None"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The <c>null</c> case is the whole reason this converter exists</b>, and it is the API's
/// behaviour rather than a convenience. Upstream found it first and says so in its own hand-written
/// deserializer: "The API returns <c>null</c> instead of <c>"none"</c> for no time-based splitting"
/// (<c>batch.rs:672-681</c>). #39 confirmed it against <c>batch.get_job_details</c>, where a job
/// submitted without a split carries <c>"split_duration":null</c>.
/// </para>
/// <para>
/// <b><see cref="JsonConverter{T}.HandleNull"/> is overridden to <see langword="true"/>, which is
/// what makes the case reachable at all.</b> Left at its default, the serializer never calls
/// <see cref="Read"/> for a <c>null</c> token on a non-nullable value type, and the asymmetry
/// would be silent: <c>"day"</c> would work, <c>null</c> would not, and the failure would surface
/// only against a job that happened to have been submitted without a split.
/// </para>
/// <para>
/// The asymmetry runs one way only. <see cref="Write"/> emits <c>none</c> rather than <c>null</c>,
/// because that is what a <em>request</em> carries — see
/// <see cref="BatchWireStrings.ToWireString(SplitDuration)"/>. Round-tripping this library's own
/// output therefore takes the string branch, and only the API's output takes the <c>null</c> one.
/// </para>
/// </remarks>
public sealed class SplitDurationJsonConverter : JsonConverter<SplitDuration>
{
    /// <summary>
    /// <see langword="true"/>: a <c>null</c> token is a value here, not an absence.
    /// </summary>
    public override bool HandleNull => true;

    /// <inheritdoc/>
    public override SplitDuration Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? SplitDuration.None : Parse(reader.GetString());

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, SplitDuration value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWireString());

    private static SplitDuration Parse(string? value) =>
        BatchWireStrings.TryParseSplitDuration(value, out var duration)
            ? duration
            : throw new JsonException($"'{value}' is not a split duration this library can name.");
}
