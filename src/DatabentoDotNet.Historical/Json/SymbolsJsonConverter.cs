using System.Text.Json;
using System.Text.Json.Serialization;
using DatabentoDotNet.Dbn;

namespace DatabentoDotNet.Historical.Json;

/// <summary>
/// Reads a <see cref="Symbols"/> from any of the four shapes the API sends one in, and writes it as
/// the comma-joined string the API takes.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's hand-written <c>Deserialize for Symbols</c> (<c>databento-rs/src/lib.rs:189-211</c>),
/// which tries an untagged helper in the order <c>u32</c>, <c>Vec&lt;u32&gt;</c>, <c>String</c>,
/// <c>Vec&lt;String&gt;</c>. The shapes are the same here; the dispatch is on the JSON token rather
/// than on trial deserialization, which is the same decision made without backtracking.
/// </para>
/// <para>
/// <b>A string is split on commas, and upstream's does not split.</b> That is a deliberate
/// departure, and it is forced rather than chosen: a comma is one of the four characters
/// <see cref="Symbols"/> forbids inside a symbol — the live gateway's subscription line separates
/// symbols with it — so <c>Symbols.From("AAPL,MSFT")</c> throws. Upstream's <c>String</c> branch
/// builds a one-element list holding the whole comma-joined value, a "symbol" that cannot be sent
/// back and does not compare equal to the set it came from; the same input here has to either split
/// or fail. Splitting also makes the round trip true, which is the property that matters: this
/// library <em>sends</em> <c>symbols</c> comma-joined (<see cref="Symbols.ToApiString"/>), so
/// comma-joined is exactly the spelling a job echoes back.
/// </para>
/// <para>
/// <b><see cref="Symbols.AllWireValue"/> is recognised in both positions.</b> #39's probe found the
/// API sends it as a bare string — <c>"symbols":"ALL_SYMBOLS"</c> — which is the case upstream
/// handles. A one-element array holding it is treated the same way here, where upstream would
/// produce an ordinary symbol set naming a symbol called <c>ALL_SYMBOLS</c>. Nothing is lost by
/// being right in a case that may never arrive: the sentinel is not a symbol any exchange lists.
/// </para>
/// <para>
/// <b>An empty array throws rather than yielding an empty set.</b> There is no such thing as a
/// <see cref="Symbols"/> naming nothing — <see cref="Symbols.From(IEnumerable{string})"/> refuses
/// it, and a defaulted value throws when rendered — so the alternative to an exception here is a
/// value that fails later, somewhere with less context.
/// </para>
/// </remarks>
public sealed class SymbolsJsonConverter : JsonConverter<Symbols>
{
    /// <inheritdoc/>
    public override Symbols Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return FromApiString(reader.GetString());

            case JsonTokenType.Number:
                return Symbols.FromIds(reader.GetUInt32());

            case JsonTokenType.StartArray:
                return ReadArray(ref reader);

            default:
                throw new JsonException(
                    $"A symbol set is a string, a number, or an array of either; the value was a "
                    + $"{reader.TokenType}.");
        }
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, Symbols value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringValue(value.ToApiString());
    }

    private static Symbols FromApiString(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new JsonException("A symbol set cannot be an empty string.");
        }

        return value == Symbols.AllWireValue
            ? Symbols.All
            : Symbols.From(value.Split(','));
    }

    private static Symbols ReadArray(ref Utf8JsonReader reader)
    {
        var symbols = new List<string>();
        var ids = new List<uint>();

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    symbols.Add(reader.GetString()!);
                    break;

                case JsonTokenType.Number:
                    ids.Add(reader.GetUInt32());
                    break;

                default:
                    throw new JsonException(
                        $"A symbol set's elements are strings or numbers; one was a {reader.TokenType}.");
            }
        }

        if (symbols.Count > 0 && ids.Count > 0)
        {
            throw new JsonException(
                "A symbol set names either raw symbols or instrument ids, and this one mixed both.");
        }

        if (ids.Count > 0)
        {
            return Symbols.FromIds(ids);
        }

        if (symbols.Count == 0)
        {
            throw new JsonException("A symbol set cannot be empty.");
        }

        // One element, and it is the sentinel: the whole-dataset set rather than a symbol whose
        // name happens to be ALL_SYMBOLS. See this converter's remarks.
        return symbols is [Symbols.AllWireValue] ? Symbols.All : Symbols.From(symbols);
    }
}
