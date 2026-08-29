using System.Text.Json;
using System.Text.Json.Serialization;

namespace DatabentoDotNet.Reference.Json;

/// <summary>
/// Reads and writes <see cref="AdjustmentStatus"/> as its one-character wire code.
/// </summary>
/// <remarks>
/// <para>
/// <b>An unrecognised code throws.</b> That is the deliberate difference from the ten open code
/// types <see cref="ReferenceCodeJsonConverter{T}"/> serves, and the argument for it is in
/// <see cref="ReferenceWireStrings"/>: these alphabets were probed against the live server, so a
/// code outside one means this library is stale rather than that the caller should be handed an
/// opaque string. The offending code is in the message. So is a string whose length is not one,
/// which is unrecognised for the same reason.
/// </para>
/// <para>
/// <b>A blank is rejected, and the dictionary is the reason.</b> <c>list_enums</c> documents
/// corporate actions and this is an <c>adjustment_factors</c> field, so it has no group there at
/// all — but upstream declares it <c>pub status: AdjustmentStatus</c> (<c>adjustment.rs:139</c>)
/// rather than an <c>Option</c>, which is the whole authority available until #57 sees real rows.
/// </para>
/// <para>
/// Attached to the type by <c>[JsonConverter]</c>, which is what the <see cref="System.Text.Json"/>
/// source generator reads — so <see cref="AdjustmentStatus"/> is AOT-safe wherever a generated
/// context holds a model carrying one, with no converter to register at a call site.
/// </para>
/// <para>
/// <see cref="Write"/> exists so the type is usable in a consumer's own serialization; nothing in
/// this library calls it, and upstream gives these enums a <c>Serialize</c> impl for the same
/// reason (<c>enums.rs:65-72</c>).
/// </para>
/// </remarks>
public sealed class AdjustmentStatusJsonConverter : JsonConverter<AdjustmentStatus>
{
    /// <summary>
    /// <see langword="true"/>, so a JSON <c>null</c> reaches <see cref="Read"/> and is rejected
    /// with a message rather than silently becoming <c>default(AdjustmentStatus)</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="System.Text.Json"/> already passes a null token to a value type's converter, so
    /// this restates the framework default rather than changing it. It is stated because the
    /// rejection below is the behaviour under test, and a default that quietly changed would turn
    /// it into an undefined enum value instead of an exception.
    /// </remarks>
    public override bool HandleNull => true;

    /// <inheritdoc/>
    public override AdjustmentStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var code = ReferenceEnumCode.Read(ref reader, nameof(AdjustmentStatus))
            ?? throw new JsonException(
                "AdjustmentStatus has no blank value. Upstream declares the adjustment_factors field "
                + "non-optional (adjustment.rs:139), so an absent value here is a malformed response "
                + "rather than 'no value'.");

        return ReferenceWireStrings.TryParseAdjustmentStatus(code, out var value)
            ? value
            : throw ReferenceEnumCode.Unrecognised(nameof(AdjustmentStatus), code);
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, AdjustmentStatus value, JsonSerializerOptions options) =>
        ReferenceEnumCode.Write(writer, value.ToChar());
}
