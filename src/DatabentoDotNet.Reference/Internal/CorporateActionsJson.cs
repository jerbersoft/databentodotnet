using System.Text.Json.Serialization;
using DatabentoDotNet.Historical.Json;

namespace DatabentoDotNet.Reference.Internal;

/// <summary>
/// The source-generated serializer context for the three <c>corporate_actions.*</c> responses.
/// </summary>
/// <remarks>
/// <para>
/// One context per endpoint group, per the transport's design note
/// (<see cref="DatabentoDotNet.Historical.HistoricalClient.ReadJsonAsync{T}"/>).
/// Source-generated rather than reflection-based because the reflection overloads do not merely
/// allocate at run time in this assembly — they fail its build (IL2026/IL3050).
/// </para>
/// <para>
/// <b>Three registered types for one endpoint group, because the group answers in three shapes.</b>
/// The two <c>list_*</c> endpoints are read whole as one JSON object, so their registrations are
/// the dictionaries themselves; <c>get_range</c> is read a line at a time by
/// <see cref="DatabentoDotNet.Historical.HistoricalClient.SendZstdJsonLinesStreamAsync"/>, so only
/// the row type is registered and a <c>List&lt;CorporateAction&gt;</c> entry would be surface
/// nothing constructs.
/// </para>
/// <para>
/// <b>The converter list exists for <see cref="CorporateAction"/> alone.</b> Until #55 this context
/// registered none at all: neither <c>list_*</c> response carries a date or a timestamp — they are
/// documentation, not data — and the code carriers they do carry (<see cref="Event"/>,
/// <see cref="EventCategory"/>, <see cref="EventLevel"/>, <see cref="EventSubType"/>,
/// <see cref="FieldGroup"/>) each declare their own converter through a <c>[JsonConverter]</c>
/// attribute the generator reads directly. <see cref="CorporateAction"/> changes that, and by
/// exactly the two entries <see cref="AdjustmentFactorsJson"/> and <see cref="SecurityMasterJson"/>
/// carry, for the same reason: the NodaTime types are third-party structs this library cannot
/// attribute. <see cref="LocalDateJsonConverter"/> serves its twenty-four dates and
/// <see cref="InstantJsonConverter"/> its two timestamps.
/// </para>
/// <para>
/// <b><see cref="InstantJsonConverter"/> also serves <see cref="CorporateAction.DateInfo"/>'s
/// values, which is where this library reads more spellings than upstream does.</b> Upstream parses
/// that map with a stricter, ISO-8601-only deserializer than the one it uses for the two fixed
/// timestamps; one converter serves both here. <see cref="CorporateAction.DateInfo"/> records why
/// that is a safe divergence rather than a leniency bug.
/// </para>
/// <para>
/// <b>The two nullable closed enums are named on their properties, not here.</b>
/// <see cref="CorporateAction.PaymentType"/> and <see cref="CorporateAction.Fraction"/> carry
/// <c>[JsonConverter]</c> attributes for <see cref="Json.NullablePaymentTypeJsonConverter"/> and
/// <see cref="Json.NullableFractionJsonConverter"/>, because a type-level attribute can only name
/// one converter and that one is the non-nullable variant. The generator reads a property attribute
/// directly, so registering them in these options as well would be duplication rather than
/// belt-and-braces.
/// </para>
/// <para>
/// <b>The <c>list_*</c> registrations are the concrete dictionaries, not the interfaces the client
/// returns.</b>
/// <c>Dictionary&lt;string, T&gt;</c> already implements
/// <see cref="IReadOnlyDictionary{TKey, TValue}"/>, so the client's return type costs no copy;
/// registering the interface instead would ask the generator to pick a constructor it has no reason
/// to know. The value type of the second is <see cref="IReadOnlyList{T}"/> for the same reason in
/// the other direction — it is what the model exposes, and the generator materialises a list into
/// it directly. <see cref="CorporateAction"/>'s three open maps go the other way still: they are
/// <see cref="IReadOnlyDictionary{TKey, TValue}"/> <em>properties</em>, which the generator
/// materialises as <see cref="Dictionary{TKey, TValue}"/> with its default ordinal comparer.
/// </para>
/// <para>
/// <see cref="JsonKnownNamingPolicy.SnakeCaseLower"/> because the reference API spells its
/// properties <c>calendar_dates</c>, <c>event_info</c> and <c>duebills_redemption_date</c>, so no
/// model in this group carries a <c>[JsonPropertyName]</c> and a property renamed in C# fails a test
/// rather than silently stopping matching a wire field. It governs the models'
/// <em>property</em> names and not the dictionary <em>keys</em>: those arrive as the server wrote
/// them — <c>AGM</c>, <c>MANDVOLU</c>, <c>meeting_number</c> — and are never transformed. <b>No
/// naming policy could transform them.</b> Setting a <c>DictionaryKeyPolicy</c> here changes nothing
/// that any of the three endpoints reads, because that option is consulted when writing a dictionary
/// and never when reading one; adding one and watching all 273 tests stay green is how that was
/// established rather than assumed. What does govern the keys is the comparer of the dictionary
/// handed back, which is why a test pins it to ordinal on both sides.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    Converters = [
        typeof(LocalDateJsonConverter),
        typeof(InstantJsonConverter),
    ])]
[JsonSerializable(typeof(Dictionary<string, EventDoc>))]
[JsonSerializable(typeof(Dictionary<string, IReadOnlyList<EventEnumVariant>>))]
[JsonSerializable(typeof(CorporateAction))]
internal sealed partial class CorporateActionsJson : JsonSerializerContext
{
}
