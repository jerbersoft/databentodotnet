using System.Text.Json;
using System.Text.Json.Serialization;

namespace DatabentoDotNet.Reference.Json;

/// <summary>
/// Reads and writes any <see cref="IReferenceCode{TSelf}"/> as its bare wire string.
/// </summary>
/// <typeparam name="T">The reference code type.</typeparam>
/// <remarks>
/// <para>
/// One converter for all ten types, closed over each of them by the
/// <c>[JsonConverter(typeof(ReferenceCodeJsonConverter&lt;Country&gt;))]</c> attribute the types
/// carry. That attribute is what the <c>System.Text.Json</c> source generator reads, so the ten
/// types are AOT-safe wherever a generated context includes a model that holds one — no reflection
/// fallback and no converter to register at a call site.
/// </para>
/// <para>
/// <b><see cref="HandleNull"/> is <see langword="true"/>, and it restates the framework default
/// rather than changing it.</b> <see cref="System.Text.Json"/> derives the property from whether
/// <c>default(T)</c> is null, so a converter over a non-nullable struct — which is every one of
/// these — is handed the <see langword="null"/> token already, while a <see cref="Nullable{T}"/>
/// converter is not. Deleting the override leaves the whole suite green, the row-level tests that
/// read an explicit <c>null</c> into a <see cref="Currency"/> and a <see cref="Frequency"/>
/// included; that is how this was settled rather than reasoned about (#60).
/// </para>
/// <para>
/// The override is kept regardless, because reading a blank as no value is this file's decision
/// rather than the framework's. A blank is a value the reference API genuinely sends, so
/// <see langword="null"/> reads as <see langword="default"/> — no value, rather than an error — and
/// it should stay this file's decision if that default ever moves. The nine closed-enum converters
/// keep theirs for the same reason, and <see cref="NullableFractionJsonConverter"/> documents the
/// other side of the same rule.
/// </para>
/// <para>
/// There is deliberately no property-name support, unlike
/// <c>DatabentoDotNet.Historical.Json.SchemaJsonConverter</c>. No reference response is keyed by one
/// of these types — <c>CorporateAction</c>'s three open maps are keyed by field <em>name</em> — so
/// it would be untested surface. Add it with the first response that needs it.
/// </para>
/// </remarks>
public sealed class ReferenceCodeJsonConverter<T> : JsonConverter<T>
    where T : struct, IReferenceCode<T>
{
    /// <inheritdoc/>
    public override bool HandleNull => true;

    /// <inheritdoc/>
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => default,
            JsonTokenType.String => T.From(reader.GetString()),
            var other => throw new JsonException(
                $"A {typeof(T).Name} is a string or null on the wire, and this one is {other}."),
        };

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value.Code is { } code)
        {
            writer.WriteStringValue(code);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
