using DatabentoDotNet.Dbn;
using NodaTime;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// Tests for <see cref="SymbologyClient.ResolveAsync"/> against the mock gateway.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every response body here is a transcription of one <c>hist.databento.com</c> actually
/// returned</b>, captured while #37 was probing the endpoint's date semantics, rather than a body
/// invented from reading upstream's deserializer. That matters for the reason CLAUDE.md gives
/// about mocks that share an author with the code they check: a body written from the same reading
/// as the converter would agree with the converter about a misreading. These agree with the
/// server.
/// </para>
/// <para>
/// The one exception is <see cref="PartialBody"/>, which no request to <c>GLBX.MDP3</c> would
/// produce, and which says so at its declaration.
/// </para>
/// </remarks>
public sealed class SymbologyResolveTests
{
    /// <summary>
    /// <c>ESH4</c> over one day. Captured verbatim from <c>hist.databento.com</c>, echoed fields
    /// and all — those extra keys being present is itself part of what this fixture pins.
    /// </summary>
    private const string OneDayBody =
        """
        {"result":{"ESH4":[{"d0":"2024-01-02","d1":"2024-01-03","s":"17077"}]},"symbols":["ESH4"],
        "stype_in":"raw_symbol","stype_out":"instrument_id","start_date":"2024-01-02",
        "end_date":"2024-01-03","partial":[],"not_found":[],"message":"OK","status":0}
        """;

    /// <summary>
    /// <c>ES.c.0</c> across its March 2024 roll: two intervals, meeting exactly where one ends and
    /// the next begins. Captured verbatim.
    /// </summary>
    private const string RollBody =
        """
        {"result":{"ES.c.0":[{"d0":"2024-01-02","d1":"2024-03-17","s":"17077"},
        {"d0":"2024-03-17","d1":"2024-04-01","s":"5602"}]},"symbols":["ES.c.0"],
        "stype_in":"continuous","stype_out":"instrument_id","start_date":"2024-01-02",
        "end_date":"2024-04-01","partial":[],"not_found":[],"message":"OK","status":0}
        """;

    /// <summary>
    /// Two symbols that resolved and one that did not. Captured verbatim — including the
    /// <c>"NOTAREALSYMBOL":[]</c> entry in <c>result</c>, which is the shape this class exists to
    /// pin, and the <c>"status":2</c> that arrived on an HTTP 200.
    /// </summary>
    private const string NotFoundBody =
        """
        {"result":{"ESH4":[{"d0":"2024-03-01","d1":"2024-06-01","s":"17077"}],
        "ESM4":[{"d0":"2024-03-01","d1":"2024-06-01","s":"5602"}],"NOTAREALSYMBOL":[]},
        "symbols":["ESH4","ESM4","NOTAREALSYMBOL"],"stype_in":"raw_symbol",
        "stype_out":"instrument_id","start_date":"2024-03-01","end_date":"2024-06-01",
        "partial":[],"not_found":["NOTAREALSYMBOL"],"message":"Not found","status":2}
        """;

    /// <summary>
    /// A partial resolution — the one bucket that could not be captured.
    /// </summary>
    /// <remarks>
    /// <b>Synthetic, and marked as such rather than passed off as a capture.</b> No request to
    /// <c>GLBX.MDP3</c> produces a partial: raw symbols resolve across the whole requested window
    /// even outside a contract's listed life, and a range starting before the dataset's first day
    /// is rejected with HTTP 422 rather than partially resolved. So this body is
    /// <see cref="NotFoundBody"/>'s shape with <c>ESM4</c> moved into <c>partial</c> and its
    /// interval shortened to cover part of the range — which is what the field is documented to
    /// mean. <c>RealHistoricalApiTests</c> asserts the two buckets that can be reached for real.
    /// </remarks>
    private const string PartialBody =
        """
        {"result":{"ESH4":[{"d0":"2024-03-01","d1":"2024-06-01","s":"17077"}],
        "ESM4":[{"d0":"2024-03-01","d1":"2024-04-01","s":"5602"}],"NOTAREALSYMBOL":[]},
        "symbols":["ESH4","ESM4","NOTAREALSYMBOL"],"stype_in":"raw_symbol",
        "stype_out":"instrument_id","start_date":"2024-03-01","end_date":"2024-06-01",
        "partial":["ESM4"],"not_found":["NOTAREALSYMBOL"],"message":"OK","status":0}
        """;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// The whole rendering through the transport and Kestrel, which <see cref="ResolveParamsTests"/>
    /// asserts in isolation. Everything goes in the form; nothing goes in the query string.
    /// </summary>
    [Fact]
    public async Task Resolve_PostsEveryParameterInTheFormAndLeavesTheQueryEmpty()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post("symbology.resolve", MockHistoricalResponse.Json(RollBody));
        await using var client = ClientFor(gateway);

        await client.Symbology.ResolveAsync(
            new ResolveParams
            {
                Dataset = "GLBX.MDP3",
                Symbols = Symbols.From(["ES.c.0"]),
                StypeIn = SType.Continuous,
                DateRange = DateRange.Between(new LocalDate(2024, 1, 2), new LocalDate(2024, 4, 1)),
            },
            Cancel);

        gateway.ThrowIfRejected();

        var request = gateway.Requests[0];
        Assert.Equal("POST", request.Method);
        Assert.Equal(MockHistoricalGateway.PathFor("symbology.resolve"), request.Path);
        Assert.Empty(request.Query);
        Assert.Equal("GLBX.MDP3", request.Form["dataset"]);
        Assert.Equal("continuous", request.Form["stype_in"]);
        Assert.Equal("instrument_id", request.Form["stype_out"]);
        Assert.Equal("ES.c.0", request.Form["symbols"]);
        Assert.Equal("2024-01-02", request.Form["start_date"]);
        Assert.Equal("2024-04-01", request.Form["end_date"]);
    }

    /// <summary>
    /// The three buckets, and the fact that <see cref="Resolution.Mappings"/> holds every symbol
    /// asked for regardless of which bucket it landed in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is #37's definition of done, generalised by what the real API turned out to do.</b>
    /// The issue asks that a partly-resolved symbol appear in <see cref="Resolution.Mappings"/> as
    /// well as <see cref="Resolution.Partial"/> — "the detail a caller reading only
    /// <c>mappings</c> gets wrong". True, and not the whole of it: a symbol that resolved to
    /// <em>nothing</em> is in <c>mappings</c> too, with an empty interval list, which the probe
    /// found and which makes <c>ContainsKey</c> useless as a "did this resolve" test for any
    /// bucket.
    /// </para>
    /// <para>
    /// So all three symbols are asserted present, and the two that did not fully resolve are
    /// asserted to carry exactly the intervals they did resolve over — an empty list for the
    /// not-found one, a shortened one for the partial.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Resolve_PutsEverySymbolInMappings_WhicheverBucketItLandedIn()
    {
        var resolution = await ResolveAsync(PartialBody, Symbols.From(["ESH4", "ESM4", "NOTAREALSYMBOL"]));

        Assert.Equal(["ESM4"], resolution.Partial);
        Assert.Equal(["NOTAREALSYMBOL"], resolution.NotFound);

        Assert.Equal(3, resolution.Mappings.Count);
        Assert.Contains("ESH4", resolution.Mappings.Keys);
        Assert.Contains("ESM4", resolution.Mappings.Keys);
        Assert.Contains("NOTAREALSYMBOL", resolution.Mappings.Keys);

        // The fully-resolved symbol covers the whole range; the partial one stops early; the
        // not-found one is present with nothing in it.
        Assert.Equal(new LocalDate(2024, 6, 1), Assert.Single(resolution.Mappings["ESH4"]).EndDate);
        Assert.Equal(new LocalDate(2024, 4, 1), Assert.Single(resolution.Mappings["ESM4"]).EndDate);
        Assert.Empty(resolution.Mappings["NOTAREALSYMBOL"]);
    }

    /// <summary>
    /// A symbol that resolves to nothing is an ordinary HTTP 200, not a
    /// <see cref="DatabentoApiException"/> — so <see cref="Resolution.NotFound"/> is the only
    /// signal a caller gets.
    /// </summary>
    /// <remarks>
    /// The body asserted here carries <c>"status":2,"message":"Not found"</c>, which the real API
    /// sent with a 200. Nothing reads those two fields, and this test is what says that is a
    /// decision rather than an oversight.
    /// </remarks>
    [Fact]
    public async Task Resolve_WithAnUnresolvableSymbol_ReturnsNormallyRatherThanThrowing()
    {
        var resolution = await ResolveAsync(NotFoundBody, Symbols.From(["ESH4", "ESM4", "NOTAREALSYMBOL"]));

        Assert.Equal(["NOTAREALSYMBOL"], resolution.NotFound);
        Assert.Empty(resolution.Partial);
        Assert.Empty(resolution.Mappings["NOTAREALSYMBOL"]);
    }

    /// <summary>
    /// The <c>d0</c>/<c>d1</c>/<c>s</c> keys become a <see cref="MappingInterval"/>, across a roll
    /// where one interval ends exactly where the next begins.
    /// </summary>
    /// <remarks>
    /// <b>The failure this catches is silent.</b> No naming policy maps
    /// <see cref="MappingInterval.StartDate"/> to <c>d0</c>, and unmatched JSON properties are
    /// skipped rather than reported — so without
    /// <see cref="Json.MappingIntervalJsonConverter"/> this response deserializes into two
    /// intervals whose dates are both <c>0001-01-01</c> and whose symbols are null, with no
    /// exception anywhere. Asserting the values, not merely the count, is what makes that
    /// impossible to pass.
    /// </remarks>
    [Fact]
    public async Task Resolve_ReadsIntervalsFromTheirWireKeys()
    {
        var resolution = await ResolveAsync(RollBody, Symbols.From(["ES.c.0"]), SType.Continuous);

        Assert.Equal(
            [
                new MappingInterval(new LocalDate(2024, 1, 2), new LocalDate(2024, 3, 17), "17077"),
                new MappingInterval(new LocalDate(2024, 3, 17), new LocalDate(2024, 4, 1), "5602"),
            ],
            resolution.Mappings["ES.c.0"]);
    }

    /// <summary>
    /// The two symbology types come from the request, even when the response says otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The response really does carry them</b> — every captured body in this class has
    /// <c>"stype_in"</c> and <c>"stype_out"</c> in it — so "echo the request" is a choice, not the
    /// only option available, and #37's porting note gave a reason for it that the probe showed to
    /// be false. This test pins the choice on its actual merit: the request is where the caller's
    /// intent lives.
    /// </para>
    /// <para>
    /// The body here is <see cref="OneDayBody"/>'s, whose echo reads
    /// <c>raw_symbol</c>/<c>instrument_id</c>, sent in answer to a request for
    /// <c>continuous</c>/<c>raw_symbol</c>. A reader that took the response's values would return
    /// the wrong pair, and <see cref="Resolution.ToSymbolMap"/> would then read the mapping in the
    /// wrong direction — which is exactly how this would be noticed in production, and much later.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Resolve_EchoesTheRequestsSymbologyTypes_NotTheResponses()
    {
        var resolution = await ResolveAsync(
            OneDayBody, Symbols.From(["ESH4"]), SType.Continuous, SType.RawSymbol);

        Assert.Equal(SType.Continuous, resolution.StypeIn);
        Assert.Equal(SType.RawSymbol, resolution.StypeOut);
    }

    /// <summary>
    /// A response missing one of the three keys is a <see cref="System.Text.Json.JsonException"/>,
    /// not a resolution in which nothing resolved.
    /// </summary>
    /// <remarks>
    /// The two are indistinguishable to a caller, which is why the members are
    /// <see langword="required"/>. <c>{"partial":[],"not_found":[]}</c> would otherwise be a
    /// perfectly well-formed answer meaning "none of your symbols exist, and none of them are
    /// missing either".
    /// </remarks>
    [Theory]
    [InlineData("""{"partial":[],"not_found":[]}""")]
    [InlineData("""{"result":{},"not_found":[]}""")]
    [InlineData("""{"result":{},"partial":[]}""")]
    public async Task Resolve_WithAResponseMissingAKey_Throws(string body)
    {
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(
            () => ResolveAsync(body, Symbols.From(["ESH4"])));
    }

    /// <summary>An interval missing one of its own three keys is reported, not defaulted.</summary>
    [Fact]
    public async Task Resolve_WithAnIntervalMissingAKey_Throws()
    {
        var thrown = await Assert.ThrowsAsync<System.Text.Json.JsonException>(
            () => ResolveAsync(
                """{"result":{"ESH4":[{"d0":"2024-01-02","s":"17077"}]},"partial":[],"not_found":[]}""",
                Symbols.From(["ESH4"])));

        Assert.Contains("d1", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>The endpoint guards its argument before building a form.</summary>
    [Fact]
    public async Task Resolve_RejectsNullParameters()
    {
        await using var client = new HistoricalClient
        {
            ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey),
            BaseUrl = new Uri("http://127.0.0.1:1/"),
        };

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.Symbology.ResolveAsync(null!, Cancel));
    }

    /// <summary>Serves <paramref name="body"/> and resolves against it.</summary>
    private static async Task<Resolution> ResolveAsync(
        string body,
        Symbols symbols,
        SType stypeIn = SType.RawSymbol,
        SType stypeOut = SType.InstrumentId)
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post("symbology.resolve", MockHistoricalResponse.Json(body));
        await using var client = ClientFor(gateway);

        var resolution = await client.Symbology.ResolveAsync(
            new ResolveParams
            {
                Dataset = "GLBX.MDP3",
                Symbols = symbols,
                StypeIn = stypeIn,
                StypeOut = stypeOut,
                DateRange = DateRange.Between(new LocalDate(2024, 3, 1), new LocalDate(2024, 6, 1)),
            },
            Cancel);

        gateway.ThrowIfRejected();
        return resolution;
    }

    private static HistoricalClient ClientFor(MockHistoricalGateway gateway) => new()
    {
        ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey),
        BaseUrl = gateway.BaseUrl,
    };
}
