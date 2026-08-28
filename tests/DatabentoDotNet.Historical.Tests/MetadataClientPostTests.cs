using System.Globalization;
using DatabentoDotNet.Dbn;
using NodaTime;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// Tests for <see cref="MetadataClient"/>'s three <c>POST</c> billing endpoints, reached through
/// <see cref="HistoricalClient.Metadata"/>.
/// </summary>
/// <remarks>
/// All three share <see cref="MetadataQueryParams"/>, so <see cref="Params"/> is the one request
/// every test here sends; what varies is the slug, the response body's shape (a bare <c>u64</c> or
/// a bare <c>f64</c>), and the type each decodes into.
/// </remarks>
public sealed class MetadataClientPostTests
{
    private static readonly DateTimeRange Range = DateTimeRange.Between(
        Instant.FromUtc(2023, 7, 4, 0, 0, 0), Instant.FromUtc(2023, 7, 5, 0, 0, 0));

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// The whole rendering, through the transport and Kestrel, in upstream's field order — which
    /// <c>MetadataParamsTests</c> asserts in isolation. This is the pair that catches an encoding
    /// applied twice or not at all.
    /// </summary>
    [Fact]
    public async Task GetCost_PostsEveryParameterInTheFormAndLeavesTheQueryEmpty()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post("metadata.get_cost", MockHistoricalResponse.Json("0.65"));
        await using var client = ClientFor(gateway);

        var actual = await client.Metadata.GetCostAsync(Params(), Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(0.65m, actual);

        var request = gateway.Requests[0];
        Assert.Equal("POST", request.Method);
        Assert.Equal(MockHistoricalGateway.PathFor("metadata.get_cost"), request.Path);
        Assert.Empty(request.Query);
        Assert.Equal("XNAS.ITCH", request.Form["dataset"]);
        Assert.Equal("trades", request.Form["schema"]);
        Assert.Equal("raw_symbol", request.Form["stype_in"]);
        Assert.Equal("AAPL,MSFT", request.Form["symbols"]);
        Assert.Equal("1688428800000000000", request.Form["start"]);
        Assert.Equal("1688515200000000000", request.Form["end"]);
        Assert.False(request.Form.ContainsKey("limit"));
    }

    /// <summary>
    /// A cost is money, and money is <see langword="decimal"/>. Upstream returns <c>f64</c>
    /// (<c>metadata.rs:190</c>) only because Rust's std has no decimal type. The value here is one
    /// that binary floating point cannot hold exactly.
    /// </summary>
    [Fact]
    public async Task GetCost_ReadsThePriceAsDecimal_NotAsBinaryFloatingPoint()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post("metadata.get_cost", MockHistoricalResponse.Json("10.10"));
        await using var client = ClientFor(gateway);

        var actual = await client.Metadata.GetCostAsync(Params(), Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(10.10m, actual);
        Assert.Equal("10.10", actual.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The other half of <c>MetadataQueryParams_OmitsLimitWhenItIsNotSet_AndAppendsItLastWhenItIs</c>
    /// (<c>MetadataParamsTests</c>) after it has actually crossed the wire: a set
    /// <see cref="MetadataQueryParams.Limit"/> has to survive the transport and Kestrel's own form
    /// decoding, not just <see cref="MetadataQueryParams.ToFormParameters"/>.
    /// </summary>
    [Fact]
    public async Task GetRecordCount_SendsASetLimitInTheForm()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post("metadata.get_record_count", MockHistoricalResponse.Json("42"));
        await using var client = ClientFor(gateway);

        var actual = await client.Metadata.GetRecordCountAsync(Params() with { Limit = 1_000UL }, Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(42UL, actual);
        Assert.Equal("1000", gateway.Requests[0].Form["limit"]);
    }

    [Fact]
    public async Task GetRecordCount_PostsToItsOwnSlug_AndDecodesTheBareUInt64()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post("metadata.get_record_count", MockHistoricalResponse.Json("128500"));
        await using var client = ClientFor(gateway);

        var actual = await client.Metadata.GetRecordCountAsync(Params(), Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(128_500UL, actual);
        Assert.Equal("POST", gateway.Requests[0].Method);
        Assert.Equal(MockHistoricalGateway.PathFor("metadata.get_record_count"), gateway.Requests[0].Path);
    }

    [Fact]
    public async Task GetBillableSize_PostsToItsOwnSlug_AndDecodesTheBareUInt64()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post("metadata.get_billable_size", MockHistoricalResponse.Json("4294967296"));
        await using var client = ClientFor(gateway);

        var actual = await client.Metadata.GetBillableSizeAsync(Params(), Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(4_294_967_296UL, actual);
        Assert.Equal("POST", gateway.Requests[0].Method);
        Assert.Equal(MockHistoricalGateway.PathFor("metadata.get_billable_size"), gateway.Requests[0].Path);
    }

    private static MetadataQueryParams Params() => new()
    {
        Dataset = "XNAS.ITCH",
        Symbols = Symbols.From(["AAPL", "MSFT"]),
        Schema = Schema.Trades,
        DateTimeRange = Range,
    };

    private static HistoricalClient ClientFor(MockHistoricalGateway gateway) => new()
    {
        ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey),
        BaseUrl = gateway.BaseUrl,
    };
}
