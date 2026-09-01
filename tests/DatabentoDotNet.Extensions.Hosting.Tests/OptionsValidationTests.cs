using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Live.Tests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DatabentoDotNet.Extensions.Hosting.Tests;

/// <summary>
/// Startup validation: a configuration that is wrong stops the host, and the message says where
/// in the configuration file to look.
/// </summary>
/// <remarks>
/// <b>The validator and the runner share one conversion path</b>, so these tests are also what
/// establishes that a session which validates is a session which resolves.
/// <c>LiveSessionValidator</c> — internal, so named in prose rather than by cref — holds no
/// rules of its own: it calls
/// <see cref="LiveSessionResolver.Resolve"/> and turns the failure list into a
/// <see cref="ValidateOptionsResult"/>.
/// </remarks>
public class OptionsValidationTests
{
    // Every test below needs a key that is syntactically valid; StartAsync_WithAValidSession_Boots
    // additionally needs one MockLiveGateway will accept, since it completes a real CRAM handshake.
    // One constant rather than two identical literals, so the second requirement cannot silently
    // stop being met.
    private const string Key = MockLiveGateway.TestApiKey;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static IHost Host(string json) =>
        Host(json, DatabentoOptions.DefaultSectionName);

    private static IHost Host(string json, string sectionPath)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(Flatten(json));
        builder.Services.AddDatabento(sectionPath);
        builder.Services.AddDatabentoLive("equities").AddRecordHandler<NullHandler>();
        return builder.Build();
    }

    // A helper that turns the JSON in each test into the flat key/value pairs an in-memory
    // provider takes. Written out in the test project rather than reaching for a JSON file, so a
    // test's configuration is visible in the test.
    [SuppressMessage(
        "Performance",
        "CA1859:Use concrete types when possible for improved performance",
        Justification =
            "IEnumerable<KeyValuePair<string, string?>> is AddInMemoryCollection's own parameter "
            + "type, and this method's whole job is to produce something that fits it. The List "
            + "materialization below is not a performance choice available to relax: it forces "
            + "the lazily generated pairs to be read out before the using block disposes the "
            + "JsonDocument they are views over.")]
    private static IEnumerable<KeyValuePair<string, string?>> Flatten(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Flatten(document.RootElement, prefix: null).ToList();
    }

    private static IEnumerable<KeyValuePair<string, string?>> Flatten(JsonElement element, string? prefix)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var key = prefix is null ? property.Name : $"{prefix}:{property.Name}";
                    foreach (var pair in Flatten(property.Value, key))
                    {
                        yield return pair;
                    }
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var pair in Flatten(item, $"{prefix}:{index}"))
                    {
                        yield return pair;
                    }

                    index++;
                }

                break;

            case JsonValueKind.Null:
                yield return new KeyValuePair<string, string?>(prefix!, null);
                break;

            default:
                yield return new KeyValuePair<string, string?>(prefix!, element.ToString());
                break;
        }
    }

    [Fact]
    public void Get_WithAValidSession_ResolvesRatherThanThrowing()
    {
        // Named for what it does, after #100 found it named for what the test below does. There is
        // no host started here and nothing connects: IOptionsMonitor.Get runs the same
        // IValidateOptions chain ValidateOnStart runs, so this settles the validator without the
        // socket — which is the cheap half of the positive control and worth keeping as its own
        // test. It carried a "Gateway": "127.0.0.1:1" key until #100 removed it; nothing read it,
        // and a port nothing dials is an invitation to believe something did.
        using var host = Host($$"""
            { "Databento": { "ApiKey": "{{Key}}", "Live": { "equities": {
                "Dataset": "EQUS.MINI",
                "Subscriptions": [ { "Schema": "trades", "Symbols": ["AAPL"] } ],
                "Reconnect": { "Enabled": false } } } } }
            """);

        var options = host.Services.GetRequiredService<IOptionsMonitor<LiveSessionOptions>>();
        Assert.Equal("EQUS.MINI", options.Get("equities").Dataset);
    }

    [Fact]
    public async Task StartAsync_WithAValidSession_Boots()
    {
        // The file's positive control, missing until #100 and the reason the rest of it means
        // anything: every other test here asserts that a wrong configuration stops the boot, and a
        // validator that rejected everything would satisfy all of them. This is the one that says a
        // right configuration does not.
        //
        // It needs a gateway socket, and that is a property of what is being asserted rather than
        // an inconvenience. ValidateOnStart runs inside host.StartAsync, and so does
        // LiveSessionService.StartAsync — there is no reaching the first without the second, so a
        // test that boots for real has to have something to connect to. LiveSessionServiceTests
        // owns whether the connection is established at the right moment; what is asserted here is
        // only that validation let the boot happen at all.
        await using var gateway = new MockLiveGateway("EQUS.MINI");

        using var host = Host($$"""
            { "Databento": { "ApiKey": "{{Key}}", "Live": { "equities": {
                "Dataset": "EQUS.MINI",
                "Gateway": "{{gateway.Address}}",
                "Subscriptions": [ { "Schema": "trades", "Symbols": ["AAPL"] } ],
                "Reconnect": { "Enabled": false } } } } }
            """);

        // trades, not MockGatewayHandshake.MboAapl()'s mbo: the subscription above is this file's,
        // and #97 made the expectation a parameter precisely so a caller wanting a different schema
        // states it rather than forking the handshake.
        var serving = MockGatewayHandshake.ServeAsync(
            gateway,
            Cancel,
            new ExpectedSubscription
            {
                Schema = Schema.Trades,
                StypeIn = SType.RawSymbol,
                Symbols = ["AAPL"],
            });

        await host.StartAsync(Cancel);
        await serving;

        // "It did not throw" is the claim, and an assertion is written anyway: the state the host
        // reached is what distinguishes a boot that completed from one that returned early. A
        // Running session is a session the validator passed, the resolver converted and the
        // hosted service started.
        var runner = host.Services.GetRequiredKeyedService<LiveSessionRunner>("equities");
        Assert.Equal(LiveSessionState.Running, runner.State);

        await gateway.CloseAsync();
        await host.StopAsync(Cancel);
    }

    [Fact]
    public async Task StartAsync_WithAnUnknownSchema_FailsTheBootAndNamesThePath()
    {
        using var host = Host($$"""
            { "Databento": { "ApiKey": "{{Key}}", "Live": { "equities": {
                "Dataset": "EQUS.MINI",
                "Subscriptions": [ { "Schema": "mbp1", "Symbols": ["AAPL"] } ] } } } }
            """);

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(Cancel));

        var failure = Assert.Single(exception.Failures);
        Assert.StartsWith("Databento:Live:equities:Subscriptions:0:Schema — ", failure);
    }

    [Fact]
    public async Task StartAsync_WithNoApiKeyAnywhere_FailsTheBoot()
    {
        using var host = Host("""
            { "Databento": { "Live": { "equities": {
                "Dataset": "EQUS.MINI",
                "Subscriptions": [ { "Schema": "trades", "Symbols": ["AAPL"] } ] } } } }
            """);

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(Cancel));

        Assert.Contains(exception.Failures, f => f.Contains("ApiKey"));
    }

    [Fact]
    public async Task StartAsync_ReportsEveryFailureAtOnce()
    {
        // One restart to see four mistakes, not four restarts. The reason the resolver collects
        // rather than throwing on the first.
        using var host = Host("""
            { "Databento": { "Live": { "equities": {
                "Subscriptions": [ { "Schema": "nope", "StypeIn": "also-nope", "Symbols": ["AAPL"] } ] } } } }
            """);

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(Cancel));

        Assert.Equal(4, exception.Failures.Count());
    }

    [Fact]
    public async Task StartAsync_WithTwoSessions_ValidatesEachAgainstItsOwnPath()
    {
        // Each session registers its own IValidateOptions<LiveSessionOptions>, and each skips a
        // name that is not its own. Getting that wrong makes one session's mistake stop the other.
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(Flatten($$"""
            { "Databento": { "ApiKey": "{{Key}}", "Live": {
                "equities": { "Dataset": "EQUS.MINI",  "Subscriptions": [ { "Schema": "trades", "Symbols": ["AAPL"] } ] },
                "futures":  { "Dataset": "GLBX.MDP3", "Subscriptions": [ { "Schema": "nope",   "Symbols": ["ESH6"] } ] } } } }
            """));
        builder.Services.AddDatabento();
        builder.Services.AddDatabentoLive("equities").AddRecordHandler<NullHandler>();
        builder.Services.AddDatabentoLive("futures").AddRecordHandler<NullHandler>();

        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(Cancel));

        var failure = Assert.Single(exception.Failures);
        Assert.StartsWith("Databento:Live:futures:", failure);
    }

    [Fact]
    public async Task StartAsync_UnderACustomSection_RootsTheMessageAtIt()
    {
        // #96. AddDatabento("MyApp:Feeds") is a supported registration — the design spec's §4
        // example — and every message used to be rooted at the literal "Databento" regardless, so
        // a host that named its own section was sent to a key absent from its own file. Worse
        // than naming no path: it sends the reader looking.
        using var host = Host(
            $$"""
            { "MyApp": { "Feeds": { "ApiKey": "{{Key}}", "Live": { "equities": {
                "Dataset": "EQUS.MINI",
                "Subscriptions": [ { "Schema": "mbp1", "Symbols": ["AAPL"] } ] } } } } }
            """,
            "MyApp:Feeds");

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(Cancel));

        // Single, not Contains, and that is the second half of the assertion: had the options
        // bound from anywhere but MyApp:Feeds, the key and the dataset would be missing too and
        // this would be three failures. One failure is what says the message and the binding
        // agree — which is the property, not the string.
        var failure = Assert.Single(exception.Failures);
        Assert.StartsWith("MyApp:Feeds:Live:equities:Subscriptions:0:Schema — ", failure);
    }

    [Fact]
    public async Task StartAsync_UnderACustomSection_RootsTheApiKeyFallbackAtItToo()
    {
        // The root this message names is a second one, reached by a different line, and it is the
        // message a reader is likeliest to act on: it tells them where to put the key. Asserted
        // whole rather than by prefix for that reason.
        using var host = Host(
            """
            { "MyApp": { "Feeds": { "Live": { "equities": {
                "Dataset": "EQUS.MINI",
                "Subscriptions": [ { "Schema": "trades", "Symbols": ["AAPL"] } ] } } } } }
            """,
            "MyApp:Feeds");

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(Cancel));

        var failure = Assert.Single(exception.Failures);
        Assert.Equal(
            "MyApp:Feeds:Live:equities:ApiKey — no API key found. Checked "
            + "MyApp:Feeds:Live:equities:ApiKey, MyApp:Feeds:ApiKey, and the "
            + "DATABENTO_API_KEY environment variable.",
            failure);
    }

    [Fact]
    public async Task StartAsync_UnderACustomSection_RootsTheHistoricalMessageAtItToo()
    {
        // The historical resolver had the same literal root, in its own copy, and is reached
        // through a different validator — so the live tests above are not evidence about it.
        // Driven through the container because HistoricalResolver is internal and this repository
        // declares no InternalsVisibleTo.
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(Flatten($$"""
            { "MyApp": { "Feeds": {
                "ApiKey": "{{Key}}",
                "Historical": { "BaseUrl": "not-a-url" } } } }
            """));
        builder.Services.AddDatabento("MyApp:Feeds");
        builder.Services.AddDatabentoHistorical();

        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(Cancel));

        // One failure again, for the reason above: the key bound from MyApp:Feeds, so only the
        // URL is wrong.
        var failure = Assert.Single(exception.Failures);
        Assert.StartsWith("MyApp:Feeds:Historical:BaseUrl — ", failure);
    }

    private sealed class NullHandler : ILiveRecordHandler
    {
        public void OnRecord(scoped RecordRef record)
        {
        }

        public ValueTask OnFlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
