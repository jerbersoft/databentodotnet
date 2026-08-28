using System.Text.Json;
using System.Text.Json.Serialization;
using DatabentoDotNet.Dbn;
using NodaTime;

namespace DatabentoDotNet.Historical.Json;

/// <summary>
/// Reads a <see cref="MappingInterval"/> from the three-key object the symbology API spells it
/// with: <c>d0</c>, <c>d1</c> and <c>s</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A converter here rather than attributes on <see cref="MappingInterval"/> itself.</b>
/// Upstream can put <c>#[serde(rename = "d0")]</c> straight on the field
/// (<c>dbn/src/metadata.rs:452-467</c>) because serde is one crate-level feature flag away. The
/// equivalent — <c>[JsonPropertyName]</c> — would make <c>DatabentoDotNet.Dbn</c> reference
/// <see cref="System.Text.Json"/>, which today it does not, anywhere: that assembly is a binary
/// codec and these three keys are the JSON spelling of <em>one HTTP endpoint's</em> response. The
/// dependency belongs on the side that already has it.
/// </para>
/// <para>
/// <b>The keys are unreachable by naming policy, which is why this is a converter and not a
/// rename.</b> <c>MetadataJson</c> and <c>SymbologyJson</c> both apply
/// <c>JsonKnownNamingPolicy.SnakeCaseLower</c>, and no policy turns <c>StartDate</c> into
/// <c>d0</c>. Without this, every interval would deserialize with all three members at their
/// defaults — two <c>0001-01-01</c> dates and a null symbol — and no exception anywhere, because
/// unmatched JSON properties are skipped by default. That is the failure this library exists to
/// turn back into something visible, so the reader below rejects an interval missing any of the
/// three rather than filling in a default.
/// </para>
/// </remarks>
public sealed class MappingIntervalJsonConverter : JsonConverter<MappingInterval>
{
    /// <summary>The wire key for the interval's inclusive start date.</summary>
    public const string StartDateKey = "d0";

    /// <summary>The wire key for the interval's exclusive end date.</summary>
    public const string EndDateKey = "d1";

    /// <summary>The wire key for the resolved symbol.</summary>
    public const string SymbolKey = "s";

    /// <summary>
    /// The date spelling, shared with <see cref="LocalDateJsonConverter"/> rather than reimplemented.
    /// </summary>
    /// <remarks>
    /// Both keys carry the same <c>yyyy-MM-dd</c> that every other date in this API uses —
    /// upstream's <c>DATE_FORMAT</c> — so there is one implementation of "how this API spells a
    /// date" and one error message for a value that is not one.
    /// </remarks>
    private static readonly LocalDateJsonConverter Date = new();

    /// <inheritdoc/>
    public override MappingInterval Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"A mapping interval is a JSON object, not {reader.TokenType}.");
        }

        LocalDate? startDate = null;
        LocalDate? endDate = null;
        string? symbol = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"Expected a property name in a mapping interval, not {reader.TokenType}.");
            }

            var name = reader.GetString();
            reader.Read();

            switch (name)
            {
                case StartDateKey:
                    startDate = Date.Read(ref reader, typeof(LocalDate), options);
                    break;
                case EndDateKey:
                    endDate = Date.Read(ref reader, typeof(LocalDate), options);
                    break;
                case SymbolKey:
                    symbol = reader.GetString();
                    break;
                default:
                    // Forward compatibility: a key added upstream is not a reason to fail a
                    // response whose three known keys are all present and well-formed.
                    reader.Skip();
                    break;
            }
        }

        return new MappingInterval(
            startDate ?? throw Missing(StartDateKey),
            endDate ?? throw Missing(EndDateKey),
            symbol ?? throw Missing(SymbolKey));
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, MappingInterval value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WritePropertyName(StartDateKey);
        Date.Write(writer, value.StartDate, options);
        writer.WritePropertyName(EndDateKey);
        Date.Write(writer, value.EndDate, options);
        writer.WriteString(SymbolKey, value.Symbol);
        writer.WriteEndObject();
    }

    private static JsonException Missing(string key) =>
        new($"A mapping interval is missing its '{key}' key.");
}
