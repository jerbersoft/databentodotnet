using System.Text.Json;
using System.Text.Json.Serialization;
using NodaTime;
using NodaTime.Text;

namespace DatabentoDotNet.Historical.Json;

/// <summary>
/// Reads an <see cref="Instant"/> from every timestamp spelling the historical API is known to
/// send.
/// </summary>
/// <remarks>
/// <para>
/// <b>Six patterns, because no fewer will do.</b> Upstream parses these in two branches: ISO 8601
/// as a zone-less <c>PrimitiveDateTime</c> that is then assumed UTC, falling back to a legacy
/// space-separated format with an optional <c>.ffffff</c> and an optional <c>+HH:mm</c>
/// (<c>databento-rs/src/deserialize.rs:7-19</c>). NodaTime splits that across more patterns than
/// the <c>time</c> crate needs, for two measured reasons:
/// </para>
/// <para>
/// <see cref="InstantPattern.ExtendedIso"/> parses <c>2023-06-14T10:00:00Z</c> but throws on
/// <c>2023-06-14T10:00:00</c>; <see cref="LocalDateTimePattern.ExtendedIso"/> does exactly the
/// reverse. So the ISO branch alone needs both. And NodaTime patterns have no optional-section
/// syntax, so each of the legacy branch's four combinations of "subsecond or not" and "offset or
/// not" needs its own pattern.
/// </para>
/// <para>
/// The order is upstream's — ISO first, legacy second — though it does not affect the result:
/// every pattern here is exact, and a value that matches one matches no other.
/// </para>
/// </remarks>
public sealed class InstantJsonConverter : JsonConverter<Instant>
{
    private static readonly LocalDateTimePattern LegacyWithSubsecond =
        LocalDateTimePattern.CreateWithInvariantCulture("uuuu-MM-dd HH:mm:ss.ffffff");

    private static readonly LocalDateTimePattern Legacy =
        LocalDateTimePattern.CreateWithInvariantCulture("uuuu-MM-dd HH:mm:ss");

    private static readonly OffsetDateTimePattern LegacyWithSubsecondAndOffset =
        OffsetDateTimePattern.CreateWithInvariantCulture("uuuu-MM-dd HH:mm:ss.ffffffo<G>");

    private static readonly OffsetDateTimePattern LegacyWithOffset =
        OffsetDateTimePattern.CreateWithInvariantCulture("uuuu-MM-dd HH:mm:sso<G>");

    /// <inheritdoc/>
    public override Instant Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return TryParse(value, out var instant)
            ? instant
            : throw new JsonException($"'{value}' is not a timestamp this library can read.");
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, Instant value, JsonSerializerOptions options) =>
        writer.WriteStringValue(InstantPattern.ExtendedIso.Format(value));

    internal static bool TryParse(string? value, out Instant result)
    {
        if (value is not null)
        {
            var iso = InstantPattern.ExtendedIso.Parse(value);
            if (iso.Success) { result = iso.Value; return true; }

            var isoZoneless = LocalDateTimePattern.ExtendedIso.Parse(value);
            if (isoZoneless.Success) { result = isoZoneless.Value.InUtc().ToInstant(); return true; }

            var subsecondOffset = LegacyWithSubsecondAndOffset.Parse(value);
            if (subsecondOffset.Success) { result = subsecondOffset.Value.ToInstant(); return true; }

            var offset = LegacyWithOffset.Parse(value);
            if (offset.Success) { result = offset.Value.ToInstant(); return true; }

            var subsecond = LegacyWithSubsecond.Parse(value);
            if (subsecond.Success) { result = subsecond.Value.InUtc().ToInstant(); return true; }

            var plain = Legacy.Parse(value);
            if (plain.Success) { result = plain.Value.InUtc().ToInstant(); return true; }
        }

        result = default;
        return false;
    }
}
