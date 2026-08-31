using DatabentoDotNet.Dbn;
using DatabentoDotNet.Live;
using NodaTime;

namespace DatabentoDotNet.Extensions.Hosting.Tests;

/// <summary>
/// <see cref="LiveSessionResolver"/> — the one crossing from bindable primitives to the library's
/// real types.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every failure names its configuration path</b>, because the reader of the message is looking
/// at an <c>appsettings.json</c> and not at this assembly. A message that says only
/// <c>'mbp1' is not a Databento schema</c> leaves them searching a file for which of four
/// subscriptions it meant.
/// </para>
/// <para>
/// <b>The environment variable is a parameter, not an ambient read.</b>
/// <see cref="LiveSessionResolver.Resolve"/> takes the value rather than calling
/// <see cref="Environment.GetEnvironmentVariable(string)"/> itself, so these tests are
/// order-independent and do not mutate the process they run in — and so the precedence chain is
/// something a test can state rather than something it has to arrange.
/// </para>
/// </remarks>
public class LiveSessionResolverTests
{
    private const string Key = "32-character-with-lots-of-filler";
    private const string OtherKey = "another-32-character-api-key-abc";

    private static LiveSessionOptions Valid() => new()
    {
        ApiKey = Key,
        Dataset = "EQUS.MINI",
        Subscriptions =
        [
            new SubscriptionOptions { Schema = "mbp-1", StypeIn = "raw_symbol", Symbols = ["AAPL", "MSFT"] },
        ],
    };

    [Fact]
    public void Resolve_OverAValidSession_ProducesTheRealTypes()
    {
        var result = LiveSessionResolver.Resolve("equities", Valid(), new DatabentoOptions(), null);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Failures);

        var session = result.Session;
        Assert.Equal("equities", session.Name);
        Assert.Equal(Key, session.ApiKey.Value);
        Assert.Equal("EQUS.MINI", session.Dataset);

        var subscription = Assert.Single(session.Subscriptions);
        Assert.Equal(Schema.Mbp1, subscription.Schema);
        Assert.Equal(SType.RawSymbol, subscription.StypeIn);
        Assert.Equal(["AAPL", "MSFT"], subscription.Symbols.ToArray());
        Assert.Null(subscription.Start);
    }

    [Fact]
    public void Resolve_WithNoStypeIn_DefaultsToRawSymbol()
    {
        // LiveClient's own default, restated here rather than left to chance: the wire default and
        // the configuration default must agree or a session behaves differently depending on
        // whether a key was written down.
        var options = Valid();
        options.Subscriptions[0].StypeIn = null;

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), null);

        Assert.True(result.Succeeded);
        Assert.Equal(SType.RawSymbol, Assert.Single(result.Session.Subscriptions).StypeIn);
    }

    [Fact]
    public void Resolve_WithTheAllSymbolsWireValue_ProducesSymbolsAll()
    {
        var options = Valid();
        options.Subscriptions[0].Symbols = [Symbols.AllWireValue];

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), null);

        Assert.True(result.Succeeded);
        Assert.Equal(SymbolsKind.All, Assert.Single(result.Session.Subscriptions).Symbols.Kind);
    }

    [Fact]
    public void Resolve_WithAReplayStart_ParsesItAsAnInstant()
    {
        var options = Valid();
        options.Subscriptions[0].Start = "2026-08-31T14:30:00Z";

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), null);

        Assert.True(result.Succeeded);
        Assert.Equal(
            Instant.FromUtc(2026, 8, 31, 14, 30),
            Assert.Single(result.Session.Subscriptions).Start);
    }

    [Fact]
    public void Resolve_WithAnUnknownSchema_FailsAndNamesTheExactPath()
    {
        var options = Valid();
        options.Subscriptions[0].Schema = "mbp1";

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), null);

        Assert.False(result.Succeeded);
        Assert.Null(result.Session);
        var failure = Assert.Single(result.Failures);
        Assert.StartsWith("Databento:Live:equities:Subscriptions:0:Schema — ", failure);
        Assert.Contains("'mbp1'", failure);
    }

    [Fact]
    public void Resolve_ReportsEveryFailure_NotJustTheFirst()
    {
        // A configuration with four mistakes should take one edit-and-restart cycle, not four.
        var options = new LiveSessionOptions
        {
            Dataset = null,
            Subscriptions =
            [
                new SubscriptionOptions { Schema = "nope", StypeIn = "also-nope", Symbols = ["AAPL"] },
            ],
            Reconnect = new ReconnectOptions { InitialDelay = "one second" },
        };

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), null);

        Assert.False(result.Succeeded);
        Assert.Equal(5, result.Failures.Length);
        Assert.All(result.Failures, f => Assert.StartsWith("Databento:", f));
        Assert.Contains(result.Failures, f => f.Contains(":ApiKey — "));
        Assert.Contains(result.Failures, f => f.Contains(":Dataset — "));
        Assert.Contains(result.Failures, f => f.Contains(":Subscriptions:0:Schema — "));
        Assert.Contains(result.Failures, f => f.Contains(":Subscriptions:0:StypeIn — "));
        Assert.Contains(result.Failures, f => f.Contains(":Reconnect:InitialDelay — "));
    }

    [Fact]
    public void Resolve_WithNoSubscriptions_Fails()
    {
        var options = Valid();
        options.Subscriptions.Clear();

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), null);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures, f => f.StartsWith("Databento:Live:equities:Subscriptions — "));
    }

    [Fact]
    public void Resolve_WithNoSymbols_CarriesTheLibrarysOwnMessage()
    {
        // Symbols.From's message, with the path prefixed. Not a second copy of the rule: this
        // resolver never decides for itself what a valid symbol set is.
        var options = Valid();
        options.Subscriptions[0].Symbols = [];

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), null);

        Assert.False(result.Succeeded);
        var failure = Assert.Single(result.Failures);
        Assert.StartsWith("Databento:Live:equities:Subscriptions:0:Symbols — ", failure);
        Assert.Contains("at least one symbol", failure);
    }

    [Theory]
    [InlineData("PT1S", 1)]
    [InlineData("PT30S", 30)]
    [InlineData("PT1M", 60)]
    [InlineData("PT1H30M", 5400)]
    public void Resolve_ParsesIso8601Durations(string text, int expectedSeconds)
    {
        var options = Valid();
        options.Reconnect.InitialDelay = text;
        // Raised so every InlineData case, including PT1H30M, sits inside it: this theory is
        // about ISO-8601 parsing, not about the InitialDelay/MaxDelay relationship, and the
        // default MaxDelay of PT30S would otherwise make the largest case fail that unrelated
        // check instead.
        options.Reconnect.MaxDelay = "PT24H";

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), null);

        Assert.True(result.Succeeded);
        Assert.Equal(Duration.FromSeconds(expectedSeconds), result.Session.Reconnect.InitialDelay);
    }

    [Theory]
    // Parses as a Period and then cannot become a Duration: a month is not a fixed length.
    [InlineData("P1M")]
    [InlineData("P1Y")]
    // Parses to a negative duration. A backoff that runs backwards is not a preference.
    [InlineData("PT-5S")]
    // Not ISO-8601 at all. The third is NodaTime's own DurationPattern.Roundtrip form, which is a
    // plausible mistake precisely because it is what this repo uses everywhere else.
    [InlineData("30")]
    [InlineData("30s")]
    [InlineData("0:00:00:30")]
    public void Resolve_WithANonDuration_FailsAndNamesThePath(string text)
    {
        var options = Valid();
        options.Reconnect.MaxDelay = text;

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), null);

        Assert.False(result.Succeeded);
        var failure = Assert.Single(result.Failures);
        Assert.StartsWith("Databento:Live:equities:Reconnect:MaxDelay — ", failure);
    }

    [Fact]
    public void Resolve_WithInitialDelayAboveMaxDelay_FailsAndNamesThePath()
    {
        // An initial backoff cannot start above the ceiling it backs off toward: the two
        // durations parse individually with no complaint, so this is a cross-field check rather
        // than something ResolveDuration alone could catch.
        var options = Valid();
        options.Reconnect.InitialDelay = "PT1M";
        options.Reconnect.MaxDelay = "PT30S";

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), null);

        Assert.False(result.Succeeded);
        var failure = Assert.Single(result.Failures);
        Assert.StartsWith("Databento:Live:equities:Reconnect:InitialDelay — ", failure);
    }

    [Fact]
    public void Resolve_TakesTheApiKeyFromTheSessionFirst()
    {
        var result = LiveSessionResolver.Resolve(
            "equities", Valid(), new DatabentoOptions { ApiKey = OtherKey }, "ignored-env-key-32-chars-long!!");

        Assert.True(result.Succeeded);
        Assert.Equal(Key, result.Session.ApiKey.Value);
    }

    [Fact]
    public void Resolve_FallsBackToTheRootApiKey()
    {
        var options = Valid();
        options.ApiKey = null;

        var result = LiveSessionResolver.Resolve(
            "equities", options, new DatabentoOptions { ApiKey = OtherKey }, null);

        Assert.True(result.Succeeded);
        Assert.Equal(OtherKey, result.Session.ApiKey.Value);
    }

    [Fact]
    public void Resolve_FallsBackToTheEnvironmentVariableLast()
    {
        var options = Valid();
        options.ApiKey = null;

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), OtherKey);

        Assert.True(result.Succeeded);
        Assert.Equal(OtherKey, result.Session.ApiKey.Value);
    }

    [Fact]
    public void Resolve_WithNoKeyAnywhere_NamesAllThreePlacesItLooked()
    {
        var options = Valid();
        options.ApiKey = null;

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), null);

        Assert.False(result.Succeeded);
        var failure = Assert.Single(result.Failures);
        Assert.Contains("Databento:Live:equities:ApiKey", failure);
        Assert.Contains("Databento:ApiKey", failure);
        Assert.Contains(LiveSessionResolver.ApiKeyEnvironmentVariable, failure);
    }

    [Fact]
    public void Resolve_WithAMalformedKey_CarriesTheLibrarysMessageAndNotTheKey()
    {
        var options = Valid();
        options.ApiKey = "too-short";

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), null);

        Assert.False(result.Succeeded);
        var failure = Assert.Single(result.Failures);
        Assert.StartsWith("Databento:Live:equities:ApiKey — ", failure);
        Assert.Contains("exactly 32 characters", failure);
        // ApiKey's own constructor never puts the key in the message, and neither does this. A
        // validation failure is logged, and a logged credential is the failure this library's
        // redacted ToString exists to prevent.
        Assert.DoesNotContain("too-short", failure);
    }

    [Fact]
    public void Resolve_ParsesTheGatewayEndpoint()
    {
        var options = Valid();
        options.Gateway = "127.0.0.1:13000";

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), null);

        Assert.True(result.Succeeded);
        Assert.Equal("127.0.0.1:13000", result.Session.Gateway!.ToString());
    }

    [Fact]
    public void Resolve_ParsesAHostnameGatewayAsADnsEndPoint()
    {
        var options = Valid();
        options.Gateway = "lsg.databento.com:13000";

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), null);

        Assert.True(result.Succeeded);
        var endpoint = Assert.IsType<System.Net.DnsEndPoint>(result.Session.Gateway);
        Assert.Equal("lsg.databento.com", endpoint.Host);
        Assert.Equal(13000, endpoint.Port);
    }

    [Fact]
    public void Resolve_WithNoGateway_LeavesItNullForLiveClientToDerive()
    {
        // LiveClient.Gateway null means "derive it from the dataset" via LiveGateway.For. The
        // resolver must not helpfully fill that in: deriving it twice is how the two would drift.
        var result = LiveSessionResolver.Resolve("equities", Valid(), new DatabentoOptions(), null);

        Assert.True(result.Succeeded);
        Assert.Null(result.Session.Gateway);
    }

    [Fact]
    public void Resolve_ParsesCompressionAndSlowReaderBehaviourByTheirWireStrings()
    {
        var options = Valid();
        options.Compression = "zstd";
        options.SlowReaderBehavior = "skip";

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), null);

        Assert.True(result.Succeeded);
        Assert.Equal(Compression.Zstd, result.Session.Compression);
        Assert.Equal(SlowReaderBehavior.Skip, result.Session.SlowReaderBehavior);
    }
}
