using System.Text.Json.Serialization;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Historical.Json;

namespace DatabentoDotNet.Historical.Internal;

/// <summary>
/// The wire shape of a <c>symbology.resolve</c> response, before the request's own symbology types
/// are attached to it.
/// </summary>
/// <remarks>
/// <para>
/// Upstream's private <c>ResolutionResp</c> (<c>symbology.rs:189-195</c>), and internal here for
/// the same reason: <see cref="DatabentoDotNet.Historical.Resolution"/> is what a caller sees, and
/// it carries two fields this does not.
/// </para>
/// <para>
/// <b>All three members are <see langword="required"/>, so a response missing one is a
/// <see cref="System.Text.Json.JsonException"/> rather than an empty result.</b> Upstream's serde
/// derive has the same effect — no <c>#[serde(default)]</c> on any of them — and the alternative is
/// worse than it looks: a body that omitted <c>result</c> would otherwise deserialize into a
/// perfectly well-formed resolution in which nothing resolved and nothing was reported missing,
/// which is indistinguishable from a legitimate answer.
/// </para>
/// <para>
/// The response also carries <c>symbols</c>, <c>stype_in</c>, <c>stype_out</c>, <c>start_date</c>,
/// <c>end_date</c>, <c>message</c> and <c>status</c>. None is declared here — unmatched properties
/// are skipped — matching upstream, and see
/// <see cref="DatabentoDotNet.Historical.Resolution.StypeIn"/> for why the two echoed symbology
/// types are taken from the request instead.
/// </para>
/// </remarks>
internal sealed class ResolutionResponse
{
    /// <summary>Every requested symbol, mapped to the intervals it resolved over.</summary>
    public required Dictionary<string, List<MappingInterval>> Result { get; init; }

    /// <summary>The symbols that resolved over part of the range only.</summary>
    public required List<string> Partial { get; init; }

    /// <summary>The symbols that did not resolve.</summary>
    public required List<string> NotFound { get; init; }
}

/// <summary>
/// The source-generated serializer context for <c>symbology.resolve</c>.
/// </summary>
/// <remarks>
/// <para>
/// A second context beside <see cref="MetadataJson"/> rather than an addition to it, per the
/// transport's design note that each endpoint group supplies its own. They share the
/// <c>SnakeCaseLower</c> policy because they share a wire convention — <c>not_found</c> here,
/// <c>last_modified_date</c> there — but not the converter list: this one needs exactly
/// <see cref="MappingIntervalJsonConverter"/>, which no <c>metadata.*</c> response contains.
/// </para>
/// <para>
/// <b>That converter is not optional decoration.</b> No naming policy maps
/// <see cref="MappingInterval.StartDate"/> to the <c>d0</c> the wire uses, so without it every
/// interval would deserialize to three default members and no exception at all. See the
/// converter's own remarks.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    Converters = [typeof(MappingIntervalJsonConverter)])]
[JsonSerializable(typeof(ResolutionResponse))]
internal sealed partial class SymbologyJson : JsonSerializerContext
{
}
