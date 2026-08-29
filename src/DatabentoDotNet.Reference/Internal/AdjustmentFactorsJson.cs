using System.Text.Json.Serialization;
using DatabentoDotNet.Historical.Json;

namespace DatabentoDotNet.Reference.Internal;

/// <summary>The source-generated serializer context for the <c>adjustment_factors.*</c> response.</summary>
/// <remarks>
/// <para>
/// One context per endpoint group, per the transport's design note
/// (<see cref="DatabentoDotNet.Historical.HistoricalClient.ReadJsonAsync{T}"/>).
/// Source-generated rather than reflection-based because the reflection overloads do not merely
/// allocate at run time in this assembly — they fail its build (IL2026/IL3050).
/// </para>
/// <para>
/// <b>The converter list is two entries long, and the shortness is the point.</b> The nine closed
/// reference enums and the ten open carriers each declare their own converter through a
/// <c>[JsonConverter]</c> attribute on the type, which the source generator reads directly — so
/// <see cref="AdjustmentStatus"/>, <see cref="Event"/>, <see cref="SecurityType"/>,
/// <see cref="Currency"/> and <see cref="Frequency"/> all need no entry here. What is left is the
/// two NodaTime types, which are third-party structs this library cannot attribute:
/// <see cref="LocalDateJsonConverter"/> for <c>ex_date</c> and
/// <see cref="InstantJsonConverter"/> for <c>ts_created</c>. Both are reused from
/// <c>DatabentoDotNet.Historical</c> rather than reimplemented — <c>ts_created</c>'s spellings are
/// upstream's <c>deserialize_date_time</c>, which is the same function the historical models read
/// their timestamps through.
/// </para>
/// <para>
/// <see cref="JsonKnownNamingPolicy.SnakeCaseLower"/> because the reference API spells its
/// properties <c>security_id</c>, <c>ex_date</c>, <c>gross_dividend</c> — so
/// <see cref="AdjustmentFactor"/> carries no <c>[JsonPropertyName]</c> at all, and a property
/// renamed in C# fails a test rather than silently stopping matching a wire field.
/// </para>
/// <para>
/// <b>Only the row type is registered, not a list of it.</b> This response is read a line at a
/// time by <see cref="DatabentoDotNet.Historical.HistoricalClient.SendZstdJsonLinesStreamAsync"/>,
/// so a <c>List&lt;AdjustmentFactor&gt;</c> entry would be surface nothing constructs.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    Converters = [
        typeof(LocalDateJsonConverter),
        typeof(InstantJsonConverter),
    ])]
[JsonSerializable(typeof(AdjustmentFactor))]
internal sealed partial class AdjustmentFactorsJson : JsonSerializerContext
{
}
