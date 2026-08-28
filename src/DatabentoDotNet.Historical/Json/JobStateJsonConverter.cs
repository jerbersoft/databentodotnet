using System.Text.Json;
using System.Text.Json.Serialization;

namespace DatabentoDotNet.Historical.Json;

/// <summary>Reads and writes <see cref="JobState"/> as its wire string.</summary>
/// <remarks>
/// <para>
/// An unrecognised state throws rather than yielding <c>default</c>, for the reason
/// <see cref="SchemaJsonConverter"/> gives about <see cref="DatabentoDotNet.Dbn.Schema"/>: the zero
/// value here is <see cref="JobState.Received"/>, a perfectly ordinary state, so a silent fallback
/// would be indistinguishable from a real job that has just been accepted.
/// </para>
/// <para>
/// <b>Throwing is what turns Databento adding an eighth state into a visible failure.</b>
/// <see cref="JobState"/> already has three members upstream lacks, found by asking the API rather
/// than by reading its client; the same widening will be needed again one day, and it should
/// arrive as an exception naming the unknown spelling rather than as a listing in which some jobs
/// are quietly <see cref="JobState.Received"/>.
/// </para>
/// </remarks>
public sealed class JobStateJsonConverter : JsonConverter<JobState>
{
    /// <inheritdoc/>
    public override JobState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Parse(reader.GetString());

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, JobState value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWireString());

    private static JobState Parse(string? value) =>
        BatchWireStrings.TryParseJobState(value, out var state)
            ? state
            : throw new JsonException(
                $"'{value}' is not a batch job state this library can name. The API's own list is "
                + "received, queued, processing, finalizing, done, expired and purged; a spelling "
                + "outside it means Databento has added a state.");
}
