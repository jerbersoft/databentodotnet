using System.Text.Json.Serialization;

namespace DatabentoDotNet.Reference.Internal;

/// <summary>
/// The source-generated serializer context for the two <c>corporate_actions.list_*</c> responses.
/// </summary>
/// <remarks>
/// <para>
/// One context per endpoint group, per the transport's design note
/// (<see cref="DatabentoDotNet.Historical.HistoricalClient.ReadJsonAsync{T}"/>).
/// Source-generated rather than reflection-based because the reflection overloads do not merely
/// allocate at run time in this assembly — they fail its build (IL2026/IL3050).
/// </para>
/// <para>
/// <b>This context registers no converters at all, and that is the whole list rather than an
/// omission.</b> <see cref="AdjustmentFactorsJson"/> and <see cref="SecurityMasterJson"/> each name
/// two, for the NodaTime types this library cannot attribute. Neither of these two responses
/// carries a date or a timestamp — they are documentation, not data — and the code carriers they do
/// carry (<see cref="Event"/>, <see cref="EventCategory"/>, <see cref="EventLevel"/>,
/// <see cref="EventSubType"/>, <see cref="FieldGroup"/>) each declare their own converter through a
/// <c>[JsonConverter]</c> attribute the generator reads directly.
/// </para>
/// <para>
/// <b>The registered types are the concrete dictionaries, not the interfaces the client
/// returns.</b>
/// <c>Dictionary&lt;string, T&gt;</c> already implements
/// <see cref="IReadOnlyDictionary{TKey, TValue}"/>, so the client's return type costs no copy;
/// registering the interface instead would ask the generator to pick a constructor it has no reason
/// to know. The value type of the second is <see cref="IReadOnlyList{T}"/> for the same reason in
/// the other direction — it is what the model exposes, and the generator materialises a list into
/// it directly.
/// </para>
/// <para>
/// <see cref="JsonKnownNamingPolicy.SnakeCaseLower"/> because the reference API spells its
/// properties <c>calendar_dates</c> and <c>event_info</c>, so neither model carries a
/// <c>[JsonPropertyName]</c>. It governs the models' <em>property</em> names and not the dictionary
/// <em>keys</em>: those arrive as the server wrote them — <c>AGM</c>, <c>MANDVOLU</c> — and are
/// never transformed. <b>No naming policy could transform them.</b> Setting a
/// <c>DictionaryKeyPolicy</c> here changes nothing that either endpoint reads, because that option
/// is consulted when writing a dictionary and never when reading one; adding one and watching all
/// 273 tests stay green is how that was established rather than assumed. What does govern the keys
/// is the comparer of the dictionary the client hands back, which is why a test pins it to ordinal.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(Dictionary<string, EventDoc>))]
[JsonSerializable(typeof(Dictionary<string, IReadOnlyList<EventEnumVariant>>))]
internal sealed partial class CorporateActionsJson : JsonSerializerContext
{
}
