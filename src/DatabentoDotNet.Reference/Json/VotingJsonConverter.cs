using System.Text.Json;
using System.Text.Json.Serialization;

namespace DatabentoDotNet.Reference.Json;

/// <summary>Reads and writes <see cref="Voting"/> as its one-character wire code.</summary>
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
/// <b>A blank is rejected, and the dictionary is the reason.</b> The <c>VOTING</c> group lists no
/// blank entry. Upstream does declare the field <c>Option&lt;Voting&gt;</c>
/// (<c>security.rs:283</c>), but a serde <c>Option</c> also covers an absent or JSON-null field,
/// which <see cref="System.Text.Json"/> answers for a <c>Voting?</c> without reaching this
/// converter — see <see cref="Voting"/>. The empty <em>string</em> is the case that would, and the
/// dictionary says it does not occur.
/// </para>
/// <para>
/// Attached to the type by <c>[JsonConverter]</c>, which is what the <see cref="System.Text.Json"/>
/// source generator reads — so <see cref="Voting"/> is AOT-safe wherever a generated context holds
/// a model carrying one, with no converter to register at a call site.
/// </para>
/// <para>
/// <see cref="Write"/> exists so the type is usable in a consumer's own serialization; nothing in
/// this library calls it, and upstream gives these enums a <c>Serialize</c> impl for the same
/// reason (<c>enums.rs:65-72</c>).
/// </para>
/// </remarks>
public sealed class VotingJsonConverter : JsonConverter<Voting>
{
    /// <summary>
    /// <see langword="true"/>, so a JSON <c>null</c> reaches <see cref="Read"/> and is rejected
    /// with a message rather than silently becoming <c>default(Voting)</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="System.Text.Json"/> already passes a null token to a value type's converter, so
    /// this restates the framework default rather than changing it. It is stated because the
    /// rejection below is the behaviour under test, and a default that quietly changed would turn
    /// it into an undefined enum value instead of an exception.
    /// </remarks>
    public override bool HandleNull => true;

    /// <inheritdoc/>
    public override Voting Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var code = ReferenceEnumCode.Read(ref reader, nameof(Voting))
            ?? throw ReferenceEnumCode.Blank(nameof(Voting), "VOTING");

        return ReferenceWireStrings.TryParseVoting(code, out var value)
            ? value
            : throw ReferenceEnumCode.Unrecognised(nameof(Voting), code);
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, Voting value, JsonSerializerOptions options) =>
        ReferenceEnumCode.Write(writer, value.ToChar());
}
