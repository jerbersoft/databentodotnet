using System.Text.Json;
using System.Text.Json.Serialization;

namespace DatabentoDotNet.Reference.Json;

/// <summary>
/// Reads and writes a <see cref="PaymentType"/> whose wire code may be blank, mapping the blank to
/// <see langword="null"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>PaymentType and <see cref="Fraction"/> are the only two of the nine char-coded reference
/// enums with a second converter, and the dictionary is why.</b> The <c>PAYTYPE</c> group of
/// <c>corporate_actions.list_enums</c> carries an entry with a null code and the description "A
/// Blank value is possible"; the other six groups do not. Upstream agrees by declaring the field
/// <c>Option&lt;PaymentType&gt;</c> (<c>corporate.rs:358</c>). So a blank is a value here rather
/// than a malformed response, and it maps to <see langword="null"/>.
/// </para>
/// <para>
/// <b>The empty string is the case that needs this converter; JSON <c>null</c> is not.</b>
/// <see cref="System.Text.Json"/> answers a null token for a <see cref="Nullable{T}"/> itself,
/// without reaching the underlying converter — so a <c>PaymentType?</c> field carrying only
/// <see cref="PaymentTypeJsonConverter"/> would already read <c>null</c> as no value, and would
/// then throw on <c>""</c>, which is the same absence spelled differently. Verified against
/// <see cref="System.Text.Json"/> rather than assumed.
/// </para>
/// <para>
/// <b><see cref="HandleNull"/> is <see langword="true"/> and is not optional.</b> Without it
/// <see cref="System.Text.Json"/> is free to answer a null token before this converter sees it,
/// which happens to give the same answer today and would stop being this file's decision. #51's
/// <see cref="ReferenceCodeJsonConverter{T}"/> sets it for the same reason.
/// </para>
/// <para>
/// <b>This one has to be named on the property, not on the type.</b> A <c>[JsonConverter]</c>
/// attribute on <see cref="PaymentType"/> can only name one converter, and that is the non-nullable
/// <see cref="PaymentTypeJsonConverter"/>. The model fields #53–#55 add for the blank-legal columns
/// are therefore <c>PaymentType?</c> with
/// <c>[JsonConverter(typeof(NullablePaymentTypeJsonConverter))]</c> on the property — and if that
/// issue's <c>Internal/ReferenceJson.cs</c> context does not pick this converter up through the
/// property attribute, it registers it in the context's options instead.
/// </para>
/// </remarks>
public sealed class NullablePaymentTypeJsonConverter : JsonConverter<PaymentType?>
{
    /// <summary>
    /// <see langword="true"/>: a JSON <c>null</c> is a value this converter answers, not one
    /// <see cref="System.Text.Json"/> answers for it.
    /// </summary>
    public override bool HandleNull => true;

    /// <inheritdoc/>
    public override PaymentType? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (ReferenceEnumCode.Read(ref reader, nameof(PaymentType)) is not { } code)
        {
            return null;
        }

        return ReferenceWireStrings.TryParsePaymentType(code, out var value)
            ? value
            : throw ReferenceEnumCode.Unrecognised(nameof(PaymentType), code);
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, PaymentType? value, JsonSerializerOptions options)
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
