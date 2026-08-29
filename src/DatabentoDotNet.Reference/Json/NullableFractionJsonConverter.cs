using System.Text.Json;
using System.Text.Json.Serialization;

namespace DatabentoDotNet.Reference.Json;

/// <summary>
/// Reads and writes a <see cref="Fraction"/> whose wire code may be blank, mapping the blank to
/// <see langword="null"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fraction and <see cref="PaymentType"/> are the only two of the nine char-coded reference
/// enums with a second converter, and the dictionary is why.</b> The <c>FRACCD</c> and
/// <c>FRACTIONS</c> groups of <c>corporate_actions.list_enums</c> each carry an entry with a null
/// code and the description "A Blank value is possible"; the other six groups do not. Upstream
/// agrees by declaring the field <c>Option&lt;Fraction&gt;</c> (<c>corporate.rs:373</c>). So a
/// blank is a value here rather than a malformed response, and it maps to <see langword="null"/>.
/// </para>
/// <para>
/// <b>The empty string is the case that needs this converter; JSON <c>null</c> is not.</b>
/// <see cref="System.Text.Json"/> answers a null token for a <see cref="Nullable{T}"/> itself,
/// without reaching the underlying converter — so a <c>Fraction?</c> field carrying only
/// <see cref="FractionJsonConverter"/> would already read <c>null</c> as no value, and would then
/// throw on <c>""</c>, which is the same absence spelled differently. Verified against
/// <see cref="System.Text.Json"/> rather than assumed.
/// </para>
/// <para>
/// <b><see cref="HandleNull"/> is <see langword="true"/>, and it is stated rather than
/// required.</b> <see cref="System.Text.Json"/> derives the property from whether
/// <c>default(T)</c> is null, so it comes out <see langword="false"/> here — a
/// <see cref="Nullable{T}"/> converter is <em>not</em> handed the null token by default, and the
/// framework answers <see langword="null"/> for it. That is the same answer this converter gives,
/// so the shipped behaviour is identical with the override and without it; probed rather than
/// reasoned about, and note that the nine non-nullable converters get the opposite default for the
/// same rule. It is written down anyway because reading a blank as no value is this file's
/// decision rather than the framework's, and it should stay this file's decision if that default
/// ever moves.
/// </para>
/// <para>
/// <b>This one has to be named on the property, not on the type.</b> A <c>[JsonConverter]</c>
/// attribute on <see cref="Fraction"/> can only name one converter, and that is the non-nullable
/// <see cref="FractionJsonConverter"/>. The model fields #53–#55 add for the blank-legal columns
/// are therefore <c>Fraction?</c> with
/// <c>[JsonConverter(typeof(NullableFractionJsonConverter))]</c> on the property — and if that
/// issue's <c>Internal/ReferenceJson.cs</c> context does not pick this converter up through the
/// property attribute, it registers it in the context's options instead.
/// </para>
/// </remarks>
public sealed class NullableFractionJsonConverter : JsonConverter<Fraction?>
{
    /// <summary>
    /// <see langword="true"/>, so a JSON <c>null</c> is a value this converter answers rather than
    /// one <see cref="System.Text.Json"/> answers on its own. Both give <see langword="null"/>; see
    /// the type's remarks for why the override is kept regardless.
    /// </summary>
    public override bool HandleNull => true;

    /// <inheritdoc/>
    public override Fraction? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (ReferenceEnumCode.Read(ref reader, nameof(Fraction)) is not { } code)
        {
            return null;
        }

        return ReferenceWireStrings.TryParseFraction(code, out var value)
            ? value
            : throw ReferenceEnumCode.Unrecognised(nameof(Fraction), code);
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, Fraction? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        // Written as JSON null rather than as the empty string. The API sends both and means the
        // same thing by them; null is the one a reader that is not this converter also understands.
        if (value is { } present)
        {
            ReferenceEnumCode.Write(writer, present.ToChar());
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
