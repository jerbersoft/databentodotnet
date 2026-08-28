using System.Text.Json.Serialization.Metadata;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Historical.Internal;

namespace DatabentoDotNet.Historical;

/// <summary>
/// The <c>metadata.*</c> endpoints: what a dataset holds, and what a request for it would cost.
/// </summary>
/// <remarks>
/// <para>
/// Reached through <see cref="HistoricalClient.Metadata"/> rather than constructed. Port of
/// upstream's <c>MetadataClient</c> (<c>databento-rs/src/historical/metadata.rs:20-23</c>), which
/// borrows the outer client; here it holds a reference, since there is no borrow checker to
/// satisfy and the lifetime is the outer client's either way.
/// </para>
/// <para>
/// <b>Call <c>GetCostAsync</c> before spending anything.</b> It is the endpoint most callers want
/// first: it answers, in dollars and before any data moves, what a <c>timeseries.get_range</c>
/// for the same parameters would cost — and <see cref="MetadataQueryParams"/> is deliberately the
/// same type both take, so the request you priced is the request you send. <c>GetCostAsync</c>
/// stays plain markup here rather than a real <c>&lt;see cref&gt;</c>, the same reason
/// <see cref="MetadataQueryParams"/>'s own doc comment names it the same way: the three
/// <c>POST</c> billing endpoints, <c>GetCostAsync</c> included, are a later task's, so the method
/// does not exist in this file yet and a real cref to it would be CS1574 today. Promote it once
/// that task lands.
/// </para>
/// </remarks>
public sealed class MetadataClient
{
    private readonly HistoricalClient _client;

    internal MetadataClient(HistoricalClient client) => _client = client;

    /// <summary>Lists every publisher, with its dataset, venue and description.</summary>
    /// <remarks>Port of upstream's <c>list_publishers</c> (<c>metadata.rs:30-33</c>). Takes no parameters.</remarks>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every publisher Databento currently defines.</returns>
    public Task<IReadOnlyList<PublisherDetail>> ListPublishersAsync(
        CancellationToken cancellationToken = default) =>
        GetAsync("list_publishers", parameters: null, MetadataJson.Default.ListPublisherDetail, cancellationToken);

    /// <summary>Lists every dataset code Databento currently defines.</summary>
    /// <remarks>
    /// Port of upstream's <c>list_datasets</c> (<c>metadata.rs:41-51</c>). When
    /// <paramref name="dateRange"/> is <see langword="null"/>, this sends no query string at all —
    /// not an empty <c>start_date</c> — matching upstream's own branch, which only calls
    /// <c>add_to_query</c> when a range was actually given (<c>metadata.rs:45-48</c>).
    /// </remarks>
    /// <param name="dateRange">
    /// The UTC date range to list datasets available within, or <see langword="null"/> to list
    /// every dataset regardless of when it became available.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every matching dataset code, for example <c>GLBX.MDP3</c>.</returns>
    public Task<IReadOnlyList<string>> ListDatasetsAsync(
        DateRange? dateRange = null,
        CancellationToken cancellationToken = default) =>
        GetAsync("list_datasets", ToQueryParameters(dateRange), MetadataJson.Default.ListString, cancellationToken);

    /// <summary>Lists every schema available for a dataset.</summary>
    /// <remarks>Port of upstream's <c>list_schemas</c> (<c>metadata.rs:59-66</c>).</remarks>
    /// <param name="dataset">The dataset code, for example <c>XNAS.ITCH</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every schema the dataset carries.</returns>
    public Task<IReadOnlyList<Schema>> ListSchemasAsync(
        string dataset,
        CancellationToken cancellationToken = default) =>
        GetAsync(
            "list_schemas",
            [new KeyValuePair<string, string>("dataset", dataset)],
            MetadataJson.Default.ListSchema,
            cancellationToken);

    /// <summary>Lists the record fields for a schema and encoding.</summary>
    /// <remarks>Port of upstream's <c>list_fields</c> (<c>metadata.rs:79-94</c>).</remarks>
    /// <param name="parameters">The encoding and schema to list fields for, and an optional dataset.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The fields the schema and encoding carry.</returns>
    public Task<IReadOnlyList<FieldDetail>> ListFieldsAsync(
        ListFieldsParams parameters,
        CancellationToken cancellationToken = default) =>
        GetAsync(
            "list_fields",
            parameters.ToQueryParameters(),
            MetadataJson.Default.ListFieldDetail,
            cancellationToken);

    /// <summary>Lists unit prices for each data schema and feed mode, in US dollars per gigabyte.</summary>
    /// <remarks>Port of upstream's <c>list_unit_prices</c> (<c>metadata.rs:102-111</c>).</remarks>
    /// <param name="dataset">The dataset code, for example <c>XNAS.ITCH</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>One entry per feed mode Databento prices separately.</returns>
    public Task<IReadOnlyList<UnitPricesForMode>> ListUnitPricesAsync(
        string dataset,
        CancellationToken cancellationToken = default) =>
        GetAsync(
            "list_unit_prices",
            [new KeyValuePair<string, string>("dataset", dataset)],
            MetadataJson.Default.ListUnitPricesForMode,
            cancellationToken);

    /// <summary>Reports data availability and quality for a dataset.</summary>
    /// <remarks>Port of upstream's <c>get_dataset_condition</c> (<c>metadata.rs:121-133</c>).</remarks>
    /// <param name="parameters">The dataset, and an optional UTC date range to report on.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// One entry for every date in the requested range, including a date with no data at all.
    /// </returns>
    public Task<IReadOnlyList<DatasetConditionDetail>> GetDatasetConditionAsync(
        GetDatasetConditionParams parameters,
        CancellationToken cancellationToken = default) =>
        GetAsync(
            "get_dataset_condition",
            parameters.ToQueryParameters(),
            MetadataJson.Default.ListDatasetConditionDetail,
            cancellationToken);

    /// <summary>Gets the available range for a dataset, given the caller's entitlements.</summary>
    /// <remarks>
    /// Port of upstream's <c>get_dataset_range</c> (<c>metadata.rs:143-153</c>). Unlike the other
    /// six endpoints in this file, the response is a single object rather than a list, so this
    /// calls <see cref="HistoricalClient.SendJsonAsync{T}"/> directly instead of going through
    /// <see cref="GetAsync{T}"/>.
    /// </remarks>
    /// <param name="dataset">The dataset code, for example <c>XNAS.ITCH</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// The dataset's overall available range, and the narrower range each individual schema has
    /// data for.
    /// </returns>
    public async Task<DatasetRange> GetDatasetRangeAsync(
        string dataset,
        CancellationToken cancellationToken = default) =>
        await _client.SendJsonAsync(
            HttpMethod.Get,
            Slug("get_dataset_range"),
            [new KeyValuePair<string, string>("dataset", dataset)],
            MetadataJson.Default.DatasetRange,
            cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<T>> GetAsync<T>(
        string slug,
        IEnumerable<KeyValuePair<string, string>>? parameters,
        JsonTypeInfo<List<T>> typeInfo,
        CancellationToken cancellationToken) =>
        await _client.SendJsonAsync(
            HttpMethod.Get, Slug(slug), parameters, typeInfo, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The endpoint group's slug prefix. Upstream builds it the same way
    /// (<c>metadata.rs:196-202</c>) before the transport prepends the API version.
    /// </summary>
    internal static string Slug(string endpoint) => $"metadata.{endpoint}";

    /// <summary>
    /// Renders <paramref name="dateRange"/> as the query parameters <c>list_datasets</c> sends,
    /// or no parameters at all when it is <see langword="null"/> — upstream's <c>add_to_query</c>
    /// (<c>historical.rs:348-353</c>), called only when a range was actually given
    /// (<c>metadata.rs:45-48</c>).
    /// </summary>
    private static IReadOnlyList<KeyValuePair<string, string>>? ToQueryParameters(DateRange? dateRange) =>
        dateRange is { } range
            ? [new("start_date", range.StartIsoDate), new("end_date", range.EndIsoDate)]
            : null;
}
