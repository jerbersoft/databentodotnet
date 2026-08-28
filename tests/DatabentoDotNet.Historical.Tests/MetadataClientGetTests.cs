using System.Text.Json;
using DatabentoDotNet.Dbn;
using NodaTime;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// Tests for <see cref="MetadataClient"/>'s seven <c>GET</c> endpoints, reached through
/// <see cref="HistoricalClient.Metadata"/>.
/// </summary>
/// <remarks>
/// Every test here asserts the same three things for its endpoint: the verb and slug the request
/// actually carried, every query parameter it sent, and the decoded result. The bodies are
/// hand-written the way <c>MetadataResponseTests</c>' are, from the same reading of the API this
/// client was written from — <c>ListSchemas_ThrowsRatherThanYieldingAnUnnamedSchema</c> and the
/// composition test below are what this file adds on top of that: routing and slug-prefixing, not
/// wire-format decoding, which the response tests already cover.
/// </remarks>
public sealed class MetadataClientGetTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListPublishers_SendsGetWithNoParameters_AndDecodesEveryField()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(
            "metadata.list_publishers",
            MockHistoricalResponse.Json(
                """[{"publisher_id":1,"dataset":"GLBX.MDP3","venue":"GLBX","description":"CME Globex MDP 3.0"}]"""));
        await using var client = ClientFor(gateway);

        var actual = await client.Metadata.ListPublishersAsync(Cancel);

        gateway.ThrowIfRejected();
        var only = Assert.Single(actual);
        Assert.Equal((ushort)1, only.PublisherId);
        Assert.Equal("GLBX.MDP3", only.Dataset);
        Assert.Equal("GLBX", only.Venue);
        Assert.Equal("CME Globex MDP 3.0", only.Description);
        Assert.Equal("GET", gateway.Requests[0].Method);
        Assert.Equal(MockHistoricalGateway.PathFor("metadata.list_publishers"), gateway.Requests[0].Path);
        Assert.Empty(gateway.Requests[0].Query);
    }

    [Fact]
    public async Task ListDatasets_SendsNoQueryAtAllWhenNoRangeIsGiven()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(
            "metadata.list_datasets",
            MockHistoricalResponse.Json("""["GLBX.MDP3","XNAS.ITCH"]"""));
        await using var client = ClientFor(gateway);

        var actual = await client.Metadata.ListDatasetsAsync(cancellationToken: Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(["GLBX.MDP3", "XNAS.ITCH"], actual);
        Assert.Equal("GET", gateway.Requests[0].Method);
        Assert.Equal(MockHistoricalGateway.PathFor("metadata.list_datasets"), gateway.Requests[0].Path);
        // Not merely absent values -- no query string at all, which is what upstream's
        // list_datasets(None) actually sends (metadata.rs:45-49).
        Assert.Equal(string.Empty, gateway.Requests[0].RawQuery);
    }

    /// <summary>
    /// The other half of the same behaviour, and the only <c>GET</c> in this group carrying a
    /// <see cref="DateRange"/> besides <c>get_dataset_condition</c>. <c>add_to_query</c> has
    /// exactly two call sites upstream and both carry a <see cref="DateRange"/>; every
    /// <see cref="DateTimeRange"/> travels in a form instead.
    /// </summary>
    [Fact]
    public async Task ListDatasets_SendsStartDateAndEndDateAsIsoDates()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(
            "metadata.list_datasets",
            MockHistoricalResponse.Json("""["GLBX.MDP3"]"""));
        await using var client = ClientFor(gateway);

        var range = DateRange.Between(new LocalDate(2024, 3, 15), new LocalDate(2024, 3, 20));
        var actual = await client.Metadata.ListDatasetsAsync(range, Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(["GLBX.MDP3"], actual);
        Assert.Equal("2024-03-15", gateway.Requests[0].Query["start_date"]);
        Assert.Equal("2024-03-20", gateway.Requests[0].Query["end_date"]);
    }

    [Fact]
    public async Task ListSchemas_ParsesThroughTheCodecsWireStrings()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get("metadata.list_schemas", MockHistoricalResponse.Json("""["mbo","ohlcv-1s","cmbp-1"]"""));
        await using var client = ClientFor(gateway);

        var actual = await client.Metadata.ListSchemasAsync("XNAS.ITCH", Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal([Schema.Mbo, Schema.Ohlcv1S, Schema.Cmbp1], actual);
        Assert.Equal("XNAS.ITCH", gateway.Requests[0].Query["dataset"]);
    }

    /// <summary>
    /// "A schema the codec cannot name must be an error at the boundary, not an unmapped enum
    /// value that reaches a caller as <c>0</c>" — and <c>0</c> is <see cref="Schema.Mbo"/>, an
    /// ordinary schema, so a silent fallback would be indistinguishable from real data.
    /// </summary>
    [Fact]
    public async Task ListSchemas_ThrowsRatherThanYieldingAnUnnamedSchema()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get("metadata.list_schemas", MockHistoricalResponse.Json("""["mbo","schema-from-the-future"]"""));
        await using var client = ClientFor(gateway);

        await Assert.ThrowsAsync<JsonException>(
            () => client.Metadata.ListSchemasAsync("XNAS.ITCH", Cancel));

        gateway.ThrowIfRejected();
    }

    [Fact]
    public async Task ListFields_SendsEncodingAndSchemaAndDataset_AndDecodesEveryField()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(
            "metadata.list_fields",
            MockHistoricalResponse.Json("""[{"name":"ts_recv","type":"uint64_t"}]"""));
        await using var client = ClientFor(gateway);

        var parameters = new ListFieldsParams { Encoding = Encoding.Dbn, Schema = Schema.Trades, Dataset = "XNAS.ITCH" };
        var actual = await client.Metadata.ListFieldsAsync(parameters, Cancel);

        gateway.ThrowIfRejected();
        var only = Assert.Single(actual);
        Assert.Equal("ts_recv", only.Name);
        Assert.Equal("uint64_t", only.TypeName);
        Assert.Equal("GET", gateway.Requests[0].Method);
        Assert.Equal(MockHistoricalGateway.PathFor("metadata.list_fields"), gateway.Requests[0].Path);
        Assert.Equal("dbn", gateway.Requests[0].Query["encoding"]);
        Assert.Equal("trades", gateway.Requests[0].Query["schema"]);
        Assert.Equal("XNAS.ITCH", gateway.Requests[0].Query["dataset"]);
    }

    [Fact]
    public async Task ListUnitPrices_SendsDataset_AndDecodesModeAndPricesKeyedByWireString()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(
            "metadata.list_unit_prices",
            MockHistoricalResponse.Json("""[{"mode":"historical","unit_prices":{"trades":0.75,"mbo":1.5}}]"""));
        await using var client = ClientFor(gateway);

        var actual = await client.Metadata.ListUnitPricesAsync("XNAS.ITCH", Cancel);

        gateway.ThrowIfRejected();
        var only = Assert.Single(actual);
        Assert.Equal(FeedMode.Historical, only.Mode);
        Assert.Equal(0.75m, only.UnitPrices[Schema.Trades]);
        Assert.Equal(1.5m, only.UnitPrices[Schema.Mbo]);
        Assert.Equal("GET", gateway.Requests[0].Method);
        Assert.Equal(MockHistoricalGateway.PathFor("metadata.list_unit_prices"), gateway.Requests[0].Path);
        Assert.Equal("XNAS.ITCH", gateway.Requests[0].Query["dataset"]);
    }

    [Fact]
    public async Task GetDatasetCondition_SendsDatasetAndRange_AndDecodesEveryDayIncludingMissing()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(
            "metadata.get_dataset_condition",
            MockHistoricalResponse.Json(
                """
                [{"date":"2024-03-15","condition":"available","last_modified_date":"2024-03-16"},
                 {"date":"2024-03-16","condition":"missing","last_modified_date":null}]
                """));
        await using var client = ClientFor(gateway);

        var parameters = new GetDatasetConditionParams
        {
            Dataset = "XNAS.ITCH",
            DateRange = DateRange.Between(new LocalDate(2024, 3, 15), new LocalDate(2024, 3, 17)),
        };
        var actual = await client.Metadata.GetDatasetConditionAsync(parameters, Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(2, actual.Count);
        Assert.Equal(DatasetCondition.Available, actual[0].Condition);
        Assert.Equal(new LocalDate(2024, 3, 16), actual[0].LastModifiedDate);
        Assert.Equal(DatasetCondition.Missing, actual[1].Condition);
        Assert.Null(actual[1].LastModifiedDate);
        Assert.Equal("GET", gateway.Requests[0].Method);
        Assert.Equal(MockHistoricalGateway.PathFor("metadata.get_dataset_condition"), gateway.Requests[0].Path);
        Assert.Equal("XNAS.ITCH", gateway.Requests[0].Query["dataset"]);
        Assert.Equal("2024-03-15", gateway.Requests[0].Query["start_date"]);
        Assert.Equal("2024-03-17", gateway.Requests[0].Query["end_date"]);
    }

    [Fact]
    public async Task GetDatasetRange_SendsDataset_AndDecodesTheSingleObject()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(
            "metadata.get_dataset_range",
            MockHistoricalResponse.Json(
                """
                {"start":"2020-01-01T00:00:00.000000000Z",
                 "end":"2024-03-16T00:00:00.000000000Z",
                 "schema":{"trades":{"start":"2020-01-01T00:00:00Z","end":"2024-03-16T00:00:00Z"}}}
                """));
        await using var client = ClientFor(gateway);

        var actual = await client.Metadata.GetDatasetRangeAsync("XNAS.ITCH", Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(Instant.FromUtc(2020, 1, 1, 0, 0), actual.Start);
        Assert.Equal(Instant.FromUtc(2024, 3, 16, 0, 0), actual.End);
        var only = Assert.Single(actual.RangeBySchema);
        Assert.Equal(Schema.Trades, only.Key);
        Assert.Equal("GET", gateway.Requests[0].Method);
        Assert.Equal(MockHistoricalGateway.PathFor("metadata.get_dataset_range"), gateway.Requests[0].Path);
        Assert.Equal("XNAS.ITCH", gateway.Requests[0].Query["dataset"]);
    }

    /// <summary>
    /// The slug carries the group prefix — upstream builds <c>metadata.{slug}</c> before the
    /// transport prepends <c>v0/</c> (<c>metadata.rs:196-202</c>). A facade that sent
    /// <c>v0/list_publishers</c> would get a 404 from the real API and, here, a 501 from the mock,
    /// which answers an unrouted path deliberately rather than 404 so a typo cannot pass for the
    /// wrong reason.
    /// </summary>
    [Fact]
    public async Task Every_Endpoint_SendsTheGroupPrefixedSlug()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get("metadata.list_publishers", MockHistoricalResponse.Json("[]"));
        gateway.Get("metadata.list_datasets", MockHistoricalResponse.Json("[]"));
        gateway.Get("metadata.list_schemas", MockHistoricalResponse.Json("[]"));
        gateway.Get("metadata.list_fields", MockHistoricalResponse.Json("[]"));
        gateway.Get("metadata.list_unit_prices", MockHistoricalResponse.Json("[]"));
        gateway.Get("metadata.get_dataset_condition", MockHistoricalResponse.Json("[]"));
        gateway.Get(
            "metadata.get_dataset_range",
            MockHistoricalResponse.Json("""{"start":"2020-01-01T00:00:00Z","end":"2020-01-02T00:00:00Z","schema":{}}"""));
        await using var client = ClientFor(gateway);

        await client.Metadata.ListPublishersAsync(Cancel);
        await client.Metadata.ListDatasetsAsync(cancellationToken: Cancel);
        await client.Metadata.ListSchemasAsync("XNAS.ITCH", Cancel);
        await client.Metadata.ListFieldsAsync(
            new ListFieldsParams { Encoding = Encoding.Dbn, Schema = Schema.Trades }, Cancel);
        await client.Metadata.ListUnitPricesAsync("XNAS.ITCH", Cancel);
        await client.Metadata.GetDatasetConditionAsync(new GetDatasetConditionParams { Dataset = "XNAS.ITCH" }, Cancel);
        await client.Metadata.GetDatasetRangeAsync("XNAS.ITCH", Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(7, gateway.Requests.Count);
        Assert.All(
            gateway.Requests,
            r => Assert.StartsWith(MockHistoricalGateway.PathFor("metadata."), r.Path, StringComparison.Ordinal));
    }

    /// <summary>
    /// The facade is reached through the client and is the same instance every time, so a caller
    /// holding <c>client.Metadata</c> across calls is holding what they think they are.
    /// </summary>
    [Fact]
    public async Task Metadata_IsTheSameFacadeOnEveryAccess()
    {
        await using var client = new HistoricalClient { ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey) };

        Assert.Same(client.Metadata, client.Metadata);
    }

    private static HistoricalClient ClientFor(MockHistoricalGateway gateway) => new()
    {
        ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey),
        BaseUrl = gateway.BaseUrl,
    };
}
