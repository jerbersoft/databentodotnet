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

    /// <summary>The conventional root, which most of these tests resolve under.</summary>
    /// <remarks>
    /// Named rather than written out at every call, so that the tests below that pass something
    /// else read as the deliberate exception they are. Bound to
    /// <see cref="DatabentoOptions.DefaultSectionName"/> rather than to a second copy of
    /// <c>"Databento"</c>: a test asserting a literal path is only evidence about the message if
    /// the root it fed in came from the same place a host's would.
    /// </remarks>
    private const string Section = DatabentoOptions.DefaultSectionName;

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
    public void Resolve_UnderACustomSection_RootsEveryPathAtIt()
    {
        // #96. The section is threaded in rather than assumed, because AddDatabento("MyApp:Feeds")
        // is a supported registration and a message rooted at the literal "Databento" points such
        // a host at a key that does not exist in its file.
        var options = Valid();
        options.ApiKey = null;
        options.Dataset = null;
        options.Subscriptions[0].Schema = "mbp1";

        var result = LiveSessionResolver.Resolve("MyApp:Feeds", "equities", options, new DatabentoOptions(), null);

        Assert.False(result.Succeeded);
        Assert.All(result.Failures, failure => Assert.StartsWith("MyApp:Feeds:", failure));

        // The API-key failure names the root a second time — "checked ...:ApiKey" — by a
        // different line than the one that builds the prefix, so it gets its own assertion.
        Assert.Contains(result.Failures, failure => failure.Contains("MyApp:Feeds:ApiKey,", StringComparison.Ordinal));
    }

    [Fact]
    public void PathFor_ComposesTheSectionAndTheName()
    {
        Assert.Equal("Databento:Live:equities", LiveSessionResolver.PathFor(Section, "equities"));
        Assert.Equal("MyApp:Feeds:Live:equities", LiveSessionResolver.PathFor("MyApp:Feeds", "equities"));
    }

    [Fact]
    public void Resolve_WithNoSection_Throws()
    {
        // Not defaulted to the conventional name, and that is the decision rather than an
        // omission: a default here is exactly the bug #96 reported, silently restored for every
        // caller who forgot the argument.
        Assert.Throws<ArgumentException>(
            () => LiveSessionResolver.Resolve(" ", "equities", Valid(), new DatabentoOptions(), null));
        Assert.Throws<ArgumentException>(() => LiveSessionResolver.PathFor(" ", "equities"));
    }

    [Fact]
    public void Resolve_OverAValidSession_ProducesTheRealTypes()
    {
        var result = LiveSessionResolver.Resolve(Section, "equities", Valid(), new DatabentoOptions(), null);

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

        var result = LiveSessionResolver.Resolve(Section, "equities", options, new DatabentoOptions(), null);

        Assert.True(result.Succeeded);
        Assert.Equal(SType.RawSymbol, Assert.Single(result.Session.Subscriptions).StypeIn);
    }

    [Fact]
    public void Resolve_WithTheAllSymbolsWireValue_ProducesSymbolsAll()
    {
        var options = Valid();
        options.Subscriptions[0].Symbols = [Symbols.AllWireValue];

        var result = LiveSessionResolver.Resolve(Section, "equities", options, new DatabentoOptions(), null);

        Assert.True(result.Succeeded);
        Assert.Equal(SymbolsKind.All, Assert.Single(result.Session.Subscriptions).Symbols.Kind);
    }

    [Fact]
    public void Resolve_WithAReplayStart_ParsesItAsAnInstant()
    {
        var options = Valid();
        options.Subscriptions[0].Start = "2026-08-31T14:30:00Z";

        var result = LiveSessionResolver.Resolve(Section, "equities", options, new DatabentoOptions(), null);

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

        var result = LiveSessionResolver.Resolve(Section, "equities", options, new DatabentoOptions(), null);

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

        var result = LiveSessionResolver.Resolve(Section, "equities", options, new DatabentoOptions(), null);

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

        var result = LiveSessionResolver.Resolve(Section, "equities", options, new DatabentoOptions(), null);

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

        var result = LiveSessionResolver.Resolve(Section, "equities", options, new DatabentoOptions(), null);

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

        var result = LiveSessionResolver.Resolve(Section, "equities", options, new DatabentoOptions(), null);

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

        var result = LiveSessionResolver.Resolve(Section, "equities", options, new DatabentoOptions(), null);

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

        var result = LiveSessionResolver.Resolve(Section, "equities", options, new DatabentoOptions(), null);

        Assert.False(result.Succeeded);
        var failure = Assert.Single(result.Failures);
        Assert.StartsWith("Databento:Live:equities:Reconnect:InitialDelay — ", failure);
    }

    [Fact]
    public void Resolve_TakesTheApiKeyFromTheSessionFirst()
    {
        var result = LiveSessionResolver.Resolve(
            Section, "equities", Valid(), new DatabentoOptions { ApiKey = OtherKey },
            "ignored-env-key-32-chars-long!!");

        Assert.True(result.Succeeded);
        Assert.Equal(Key, result.Session.ApiKey.Value);
    }

    [Fact]
    public void Resolve_FallsBackToTheRootApiKey()
    {
        var options = Valid();
        options.ApiKey = null;

        var result = LiveSessionResolver.Resolve(
            Section, "equities", options, new DatabentoOptions { ApiKey = OtherKey }, null);

        Assert.True(result.Succeeded);
        Assert.Equal(OtherKey, result.Session.ApiKey.Value);
    }

    [Fact]
    public void Resolve_FallsBackToTheEnvironmentVariableLast()
    {
        var options = Valid();
        options.ApiKey = null;

        var result = LiveSessionResolver.Resolve(Section, "equities", options, new DatabentoOptions(), OtherKey);

        Assert.True(result.Succeeded);
        Assert.Equal(OtherKey, result.Session.ApiKey.Value);
    }

    [Fact]
    public void Resolve_WithNoKeyAnywhere_NamesAllThreePlacesItLooked()
    {
        var options = Valid();
        options.ApiKey = null;

        var result = LiveSessionResolver.Resolve(Section, "equities", options, new DatabentoOptions(), null);

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

        var result = LiveSessionResolver.Resolve(Section, "equities", options, new DatabentoOptions(), null);

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

        var result = LiveSessionResolver.Resolve(Section, "equities", options, new DatabentoOptions(), null);

        Assert.True(result.Succeeded);
        Assert.Equal("127.0.0.1:13000", result.Session.Gateway!.ToString());
    }

    [Fact]
    public void Resolve_ParsesAHostnameGatewayAsADnsEndPoint()
    {
        var options = Valid();
        options.Gateway = "lsg.databento.com:13000";

        var result = LiveSessionResolver.Resolve(Section, "equities", options, new DatabentoOptions(), null);

        Assert.True(result.Succeeded);
        var endpoint = Assert.IsType<System.Net.DnsEndPoint>(result.Session.Gateway);
        Assert.Equal("lsg.databento.com", endpoint.Host);
        Assert.Equal(13000, endpoint.Port);
    }

    [Theory]
    [InlineData("lsg.databento.com:99999")]
    [InlineData("lsg.databento.com:65536")]
    [InlineData("lsg.databento.com:-1")]
    public void Resolve_WithAGatewayPortOutOfRange_FailsAndNamesThePath(string gateway)
    {
        // int.TryParse accepts any int and DnsEndPoint's constructor accepts only 0..65535, so
        // this escaped Resolve as an ArgumentOutOfRangeException naming `port` — past the failure
        // list this type exists to collect, past ValidateOnStart, and into a consumer's face
        // naming neither the session nor the configuration key. A bad configuration value is
        // expected, not exceptional, so it is now one more entry in the list like every other.
        var options = Valid();
        options.Gateway = gateway;

        var result = LiveSessionResolver.Resolve(Section, "equities", options, new DatabentoOptions(), null);

        Assert.False(result.Succeeded);
        Assert.Null(result.Session);
        var failure = Assert.Single(result.Failures);
        Assert.StartsWith("Databento:Live:equities:Gateway — ", failure);
        Assert.Contains($"'{gateway}'", failure);
    }

    [Fact]
    public void Resolve_WithTheHighestValidGatewayPort_StillResolves()
    {
        // The other side of the bound, so the range check cannot quietly become off-by-one.
        var options = Valid();
        options.Gateway = "lsg.databento.com:65535";

        var result = LiveSessionResolver.Resolve(Section, "equities", options, new DatabentoOptions(), null);

        Assert.True(result.Succeeded);
        Assert.Equal(65535, Assert.IsType<System.Net.DnsEndPoint>(result.Session.Gateway).Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_WithEmptyOptionalWireStrings_UsesTheDefaults(string empty)
    {
        // An environment-variable provider yields "" for a key set to nothing, which is how an
        // operator spells "leave it alone". These three read IsNullOrWhiteSpace like every other
        // optional field; when they read `is null` instead, an empty override was a startup
        // failure rather than the default.
        var options = Valid();
        options.Subscriptions[0].StypeIn = empty;
        options.Compression = empty;
        options.SlowReaderBehavior = empty;

        var result = LiveSessionResolver.Resolve(Section, "equities", options, new DatabentoOptions(), null);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Failures);
        Assert.Equal(SType.RawSymbol, Assert.Single(result.Session.Subscriptions).StypeIn);
        Assert.Equal(Compression.None, result.Session.Compression);
        Assert.Null(result.Session.SlowReaderBehavior);
    }

    [Fact]
    public void Resolve_WithNoGateway_LeavesItNullForLiveClientToDerive()
    {
        // LiveClient.Gateway null means "derive it from the dataset" via LiveGateway.For. The
        // resolver must not helpfully fill that in: deriving it twice is how the two would drift.
        var result = LiveSessionResolver.Resolve(Section, "equities", Valid(), new DatabentoOptions(), null);

        Assert.True(result.Succeeded);
        Assert.Null(result.Session.Gateway);
    }

    [Fact]
    public void Resolve_ParsesCompressionAndSlowReaderBehaviourByTheirWireStrings()
    {
        var options = Valid();
        options.Compression = "zstd";
        options.SlowReaderBehavior = "skip";

        var result = LiveSessionResolver.Resolve(Section, "equities", options, new DatabentoOptions(), null);

        Assert.True(result.Succeeded);
        Assert.Equal(Compression.Zstd, result.Session.Compression);
        Assert.Equal(SlowReaderBehavior.Skip, result.Session.SlowReaderBehavior);
    }
    // ------------------------------------------------------------------------------------------
    // CloseTimeout — #98. No configuration key reached LiveSessionRunner.CloseTimeout before this,
    // so for every hosted consumer the courteous-close ceiling was a fixed five seconds.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Resolve_WithACloseTimeout_ParsesIt()
    {
        var options = Valid();
        options.CloseTimeout = "PT2S";

        var result = LiveSessionResolver.Resolve(Section, "equities", options, new DatabentoOptions(), null);

        Assert.True(result.Succeeded);
        Assert.Equal(Duration.FromSeconds(2), result.Session.CloseTimeout);
    }

    [Fact]
    public void Resolve_WithNoCloseTimeout_LeavesItNull()
    {
        // Null means "nobody configured one", and AddDatabentoLive supplies the runner's default.
        // Substituting five seconds here would put that number in two places.
        var result = LiveSessionResolver.Resolve(Section, "equities", Valid(), new DatabentoOptions(), null);

        Assert.True(result.Succeeded);
        Assert.Null(result.Session.CloseTimeout);
    }

    /// <summary>
    /// Zero is refused, unlike the other optional durations.
    /// </summary>
    /// <remarks>
    /// A zero ceiling loses the race against the close on every shutdown, so the session would drop
    /// the socket immediately and log <c>CloseTimedOut</c> each time — a warning describing a
    /// timeout that did not happen. <c>HeartbeatInterval</c> and <c>ReadTimeout</c> admit zero
    /// because for them it means something; here it is only ever a mistake.
    /// </remarks>
    [Fact]
    public void Resolve_WithAZeroCloseTimeout_FailsAndNamesThePath()
    {
        var options = Valid();
        options.CloseTimeout = "PT0S";

        var result = LiveSessionResolver.Resolve(Section, "equities", options, new DatabentoOptions(), null);

        Assert.False(result.Succeeded);
        var failure = Assert.Single(result.Failures);
        Assert.StartsWith($"{Section}:Live:equities:CloseTimeout", failure, StringComparison.Ordinal);
        Assert.Contains("is zero", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_WithACloseTimeoutThatIsNotADuration_FailsAndNamesThePath()
    {
        var options = Valid();
        options.CloseTimeout = "5 seconds";

        var result = LiveSessionResolver.Resolve(Section, "equities", options, new DatabentoOptions(), null);

        Assert.False(result.Succeeded);
        Assert.StartsWith(
            $"{Section}:Live:equities:CloseTimeout",
            Assert.Single(result.Failures),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_WithACloseTimeoutUnderACustomSection_RootsThePathAtIt()
    {
        var options = Valid();
        options.CloseTimeout = "PT0S";

        var result = LiveSessionResolver.Resolve("MyApp:Feeds", "equities", options, new DatabentoOptions(), null);

        Assert.False(result.Succeeded);
        Assert.StartsWith(
            "MyApp:Feeds:Live:equities:CloseTimeout",
            Assert.Single(result.Failures),
            StringComparison.Ordinal);
    }

}
