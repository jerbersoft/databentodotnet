using System.Text.Json.Serialization;
using DatabentoDotNet.Historical.Json;

namespace DatabentoDotNet.Historical.Internal;

/// <summary>The source-generated serializer context for every <c>batch.*</c> response.</summary>
/// <remarks>
/// <para>
/// One context for the whole endpoint group, per the transport's design note that each endpoint
/// group supplies its own (<see cref="HistoricalClient.ReadJsonAsync{T}"/>). Source-generated
/// rather than reflection-based because the reflection overloads do not merely allocate at run
/// time here — they fail this assembly's build (IL2026/IL3050).
/// </para>
/// <para>
/// <b>The longest converter list in the library, and every entry earns its place.</b>
/// <c>batch.*</c> is the first endpoint group whose responses echo the whole request back, so it is
/// the first to need <see cref="SymbolsJsonConverter"/>, <see cref="STypeJsonConverter"/>,
/// <see cref="EncodingJsonConverter"/> and <see cref="CompressionJsonConverter"/> — four types this
/// library had until now only ever <em>sent</em>. The other three are this group's own enums.
/// </para>
/// <para>
/// <b>Two of them read JSON <c>null</c> as a value rather than as an absence</b>, which is a shape
/// no other endpoint group has: <c>compression</c> and <c>split_duration</c> both spell their
/// "none" that way. See those converters, and note that both properties are
/// <see langword="required"/> on <see cref="BatchJob"/> — a <c>null</c> satisfies
/// <see langword="required"/> here precisely because the converter turns it into a value.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    Converters = [
        typeof(SchemaJsonConverter),
        typeof(STypeJsonConverter),
        typeof(EncodingJsonConverter),
        typeof(CompressionJsonConverter),
        typeof(SymbolsJsonConverter),
        typeof(InstantJsonConverter),
        typeof(JobStateJsonConverter),
        typeof(SplitDurationJsonConverter),
        typeof(DeliveryJsonConverter),
    ])]
[JsonSerializable(typeof(BatchJob))]
[JsonSerializable(typeof(List<BatchJob>))]
[JsonSerializable(typeof(List<BatchJobSummary>))]
[JsonSerializable(typeof(List<BatchFileDescription>))]
internal sealed partial class BatchJson : JsonSerializerContext
{
}
