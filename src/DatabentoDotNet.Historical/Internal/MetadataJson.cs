using System.Text.Json.Serialization;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Historical.Json;

namespace DatabentoDotNet.Historical.Internal;

/// <summary>
/// The source-generated serializer context for every <c>metadata.*</c> response.
/// </summary>
/// <remarks>
/// <para>
/// One context for the whole endpoint group, per the transport's own design note: "each endpoint
/// group supplies its own <c>[JsonSerializable]</c> context and passes the resulting
/// <c>JsonTypeInfo</c>" (<c>HistoricalClient.cs:409</c>). Source-generated rather than
/// reflection-based because the reflection overloads do not merely allocate at run time here —
/// they fail this assembly's build (IL2026/IL3050).
/// </para>
/// <para>
/// <b>The naming policy is <c>SnakeCaseLower</c>, and that is not a style choice.</b> Every
/// <c>metadata.*</c> key is snake_case — <c>publisher_id</c>, <c>unit_prices</c>,
/// <c>last_modified_date</c>, <c>record_count</c>. The nearby context in
/// <c>HistoricalClientTests.cs:921</c> uses <c>CamelCase</c> for its own one-property fixture;
/// copying that attribute here would map <c>PublisherId</c> to <c>publisherId</c> and every value
/// would come back null, with assertions failing for a reason that has nothing to do with the
/// client.
/// </para>
/// <para>
/// <b>Internal, and reachable only from <see cref="DatabentoDotNet.Historical.MetadataClient"/>'s
/// endpoint methods.</b> This repo declares no <c>InternalsVisibleTo</c>, so the test
/// project cannot name this type; <c>MetadataResponseTests</c> declares its own private nested
/// context over these same public DTOs and the public converters in
/// <see cref="DatabentoDotNet.Historical.Json"/> instead. That is the better split regardless of
/// visibility — those tests are about the DTOs and the converters, and this context's own
/// configuration is exercised where it is actually used, by the endpoint tests that call through
/// it.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    Converters = [
        typeof(SchemaJsonConverter),
        typeof(FeedModeJsonConverter),
        typeof(DatasetConditionJsonConverter),
        typeof(InstantJsonConverter),
        typeof(LocalDateJsonConverter),
        typeof(DateTimeRangeJsonConverter),
    ])]
[JsonSerializable(typeof(List<PublisherDetail>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<Schema>))]
[JsonSerializable(typeof(List<FieldDetail>))]
[JsonSerializable(typeof(List<UnitPricesForMode>))]
[JsonSerializable(typeof(List<DatasetConditionDetail>))]
[JsonSerializable(typeof(DatasetRange))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(decimal))]
internal sealed partial class MetadataJson : JsonSerializerContext
{
}
