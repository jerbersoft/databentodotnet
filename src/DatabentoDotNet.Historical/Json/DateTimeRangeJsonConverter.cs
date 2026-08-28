using System.Text.Json;
using System.Text.Json.Serialization;
using NodaTime;

namespace DatabentoDotNet.Historical.Json;

/// <summary>
/// Reads a <see cref="DateTimeRange"/> from the <c>{"start":…,"end":…}</c> object the
/// <c>get_dataset_range</c> response nests under each schema.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DateTimeRange"/> arrived in #33 as a request-only type — it renders
/// <see cref="DateTimeRange.StartUnixNanoseconds"/> onto a form and was never read back. This is
/// the first response that carries one (<c>metadata.rs:317</c>), and the wire spelling on the way
/// in is an ISO timestamp rather than the nanoseconds it goes out as.
/// </para>
/// <para>
/// An inverted or empty range is rejected by <see cref="DateTimeRange.Between"/> itself, and that
/// <see cref="ArgumentException"/> is translated to <see cref="JsonException"/> here rather than
/// escaping: a malformed body is a JSON problem from the caller's side, and letting an
/// <see cref="ArgumentException"/> out of a deserializer would name a parameter the caller never
/// passed.
/// </para>
/// </remarks>
public sealed class DateTimeRangeJsonConverter : JsonConverter<DateTimeRange>
{
    /// <inheritdoc/>
    public override DateTimeRange Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A date-time range is a JSON object with a start and an end.");
        }

        Instant? start = null;
        Instant? end = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) { continue; }

            var name = reader.GetString();
            reader.Read();

            if (name == "start") { start = ReadInstant(ref reader); }
            else if (name == "end") { end = ReadInstant(ref reader); }
            else { reader.Skip(); }
        }

        if (start is null || end is null)
        {
            throw new JsonException("A date-time range needs both a start and an end.");
        }

        try
        {
            return DateTimeRange.Between(start.Value, end.Value);
        }
        catch (ArgumentException e)
        {
            throw new JsonException($"The range {start} to {end} is not a usable interval.", e);
        }
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, DateTimeRange value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("start", NodaTime.Text.InstantPattern.ExtendedIso.Format(value.Start));
        writer.WriteString("end", NodaTime.Text.InstantPattern.ExtendedIso.Format(value.End));
        writer.WriteEndObject();
    }

    private static Instant ReadInstant(ref Utf8JsonReader reader)
    {
        var value = reader.GetString();
        return InstantJsonConverter.TryParse(value, out var instant)
            ? instant
            : throw new JsonException($"'{value}' is not a timestamp this library can read.");
    }
}
