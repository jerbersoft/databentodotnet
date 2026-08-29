using System.Text.Json.Serialization;
using DatabentoDotNet.Historical.Json;

namespace DatabentoDotNet.Reference.Internal;

/// <summary>The source-generated serializer context for the <c>security_master.*</c> response.</summary>
/// <remarks>
/// <para>
/// One context per endpoint group, per the transport's design note
/// (<see cref="DatabentoDotNet.Historical.HistoricalClient.ReadJsonAsync{T}"/>).
/// Source-generated rather than reflection-based because the reflection overloads do not merely
/// allocate at run time in this assembly — they fail its build (IL2026/IL3050).
/// </para>
/// <para>
/// <b>The converter list is the same two entries <see cref="AdjustmentFactorsJson"/> carries, and
/// for the same reason.</b> The closed reference enums and the open carriers each declare their own
/// converter through a <c>[JsonConverter]</c> attribute on the type, which the source generator
/// reads directly — so <see cref="ListingStatus"/>, <see cref="ListingSource"/>,
/// <see cref="Voting"/>, <see cref="SecurityType"/>, <see cref="Country"/> and
/// <see cref="Currency"/> all need no entry here. What is left is the two NodaTime types, which are
/// third-party structs this library cannot attribute: <see cref="LocalDateJsonConverter"/> for the
/// four dates and <see cref="InstantJsonConverter"/> for the three timestamps. Both are reused from
/// <c>DatabentoDotNet.Historical</c> rather than reimplemented — the timestamps' spellings are
/// upstream's <c>deserialize_date_time</c>, which is the same function the historical models read
/// their timestamps through.
/// </para>
/// <para>
/// <b><see cref="SecurityMaster.Voting"/> is a <c>Voting?</c>, and it needs no entry either.</b>
/// <see cref="System.Text.Json"/> wraps the type's own converter for a <see cref="Nullable{T}"/>
/// and answers the <c>null</c> token itself. That is asserted rather than assumed — see the
/// security master tests' absent-field and explicit-null rows.
/// </para>
/// <para>
/// <see cref="JsonKnownNamingPolicy.SnakeCaseLower"/> because the reference API spells its
/// properties <c>listing_group_id</c>, <c>bbg_comp_ticker</c>, <c>shares_outstanding_date</c> — so
/// <see cref="SecurityMaster"/> carries no <c>[JsonPropertyName]</c> at all, and a property renamed
/// in C# fails a test rather than silently stopping matching a wire field.
/// </para>
/// <para>
/// <b>Only the row type is registered, not a list of it.</b> This response is read a line at a time
/// by <see cref="DatabentoDotNet.Historical.HistoricalClient.SendZstdJsonLinesStreamAsync"/>, so a
/// <c>List&lt;SecurityMaster&gt;</c> entry would be surface nothing constructs.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    Converters = [
        typeof(LocalDateJsonConverter),
        typeof(InstantJsonConverter),
    ])]
[JsonSerializable(typeof(SecurityMaster))]
internal sealed partial class SecurityMasterJson : JsonSerializerContext
{
}
