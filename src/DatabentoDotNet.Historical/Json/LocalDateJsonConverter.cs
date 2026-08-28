using System.Text.Json;
using System.Text.Json.Serialization;
using NodaTime;
using NodaTime.Text;

namespace DatabentoDotNet.Historical.Json;

/// <summary>
/// Reads a <see cref="LocalDate"/> from the <c>yyyy-MM-dd</c> spelling the historical API uses for
/// calendar dates.
/// </summary>
/// <remarks>
/// Upstream's <c>DATE_FORMAT</c> is <c>"[year]-[month]-[day]"</c>
/// (<c>databento-rs/src/historical.rs:184-185</c>), which is exactly
/// <see cref="LocalDatePattern.Iso"/>. Registering this converter for <see cref="LocalDate"/> also
/// covers <c>LocalDate?</c>: <see cref="System.Text.Json"/> unwraps the nullable and hands the
/// non-null case here, answering <see langword="null"/> itself for a JSON <c>null</c> — which is
/// what <c>last_modified_date</c> carries when a day's condition is
/// <see cref="DatasetCondition.Missing"/> (<c>metadata.rs:300-301</c>).
/// </remarks>
public sealed class LocalDateJsonConverter : JsonConverter<LocalDate>
{
    /// <inheritdoc/>
    public override LocalDate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        var parsed = LocalDatePattern.Iso.Parse(value ?? string.Empty);
        return parsed.Success
            ? parsed.Value
            : throw new JsonException($"'{value}' is not a yyyy-MM-dd date.");
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, LocalDate value, JsonSerializerOptions options) =>
        writer.WriteStringValue(LocalDatePattern.Iso.Format(value));
}
