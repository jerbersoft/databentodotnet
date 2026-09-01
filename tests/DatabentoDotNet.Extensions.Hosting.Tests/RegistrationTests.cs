using DatabentoDotNet;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Historical;
using DatabentoDotNet.Reference;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using NodaTime;

namespace DatabentoDotNet.Extensions.Hosting.Tests;

/// <summary>
/// A real <see cref="ServiceProvider"/> resolves what was registered, and two named sessions stay
/// independent.
/// </summary>
/// <remarks>
/// A real container rather than assertions about <see cref="ServiceDescriptor"/>s: what a consumer
/// experiences is <c>GetRequiredService</c> returning something, and a descriptor list can be
/// right while the graph it describes fails to build.
/// </remarks>
public class RegistrationTests
{
    private const string Key = "32-character-with-lots-of-filler";

    private static ServiceProvider Provider(Action<IServiceCollection> register)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Databento:ApiKey"] = Key,
                ["Databento:Historical:UserAgentExtension"] = "bound-value",
                ["Databento:Live:equities:Dataset"] = "EQUS.MINI",
                ["Databento:Live:equities:Subscriptions:0:Schema"] = "trades",
                ["Databento:Live:equities:Subscriptions:0:Symbols:0"] = "AAPL",
                ["Databento:Live:futures:Dataset"] = "GLBX.MDP3",
                ["Databento:Live:futures:Subscriptions:0:Schema"] = "mbp-1",
                ["Databento:Live:futures:Subscriptions:0:Symbols:0"] = "ESH6",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddDatabento();
        register(services);
        return services.BuildServiceProvider();
    }

    // The next four tests register a HistoricalClient, a ReferenceClient, or both — and both are
    // IAsyncDisposable only (Task 2's own design: DisposeAsync is what lets a shared transport be
    // released without blocking). ServiceProvider.Dispose() refuses to dispose a container holding
    // an IAsyncDisposable-only singleton rather than silently skipping it, so these four use
    // await using and an async test method; every other test below registers nothing disposable
    // and keeps the brief's plain using.

    [Fact]
    public async Task AddDatabentoHistorical_RegistersASingletonCarryingTheKey()
    {
        await using var provider = Provider(services => services.AddDatabentoHistorical());

        var client = provider.GetRequiredService<HistoricalClient>();

        Assert.Equal(Key, client.ApiKey.Value);
        Assert.NotNull(client.Handler);
        // The factory owns the handler's lifetime and rotates it on its own schedule.
        Assert.False(client.DisposesHandler);
        Assert.Same(client, provider.GetRequiredService<HistoricalClient>());
    }

    [Fact]
    public async Task AddDatabentoReference_ReusesTheHistoricalTransport()
    {
        await using var provider = Provider(services =>
        {
            services.AddDatabentoHistorical();
            services.AddDatabentoReference();
        });

        Assert.Same(
            provider.GetRequiredService<HistoricalClient>(),
            provider.GetRequiredService<ReferenceClient>().Transport);
    }

    [Fact]
    public async Task AddDatabentoReference_Alone_RegistersTheTransportItself()
    {
        // Neither call is a prerequisite of the other. This is the half that would break if
        // AddDatabentoReference assumed AddDatabentoHistorical had already run.
        await using var provider = Provider(services => services.AddDatabentoReference());

        Assert.NotNull(provider.GetRequiredService<ReferenceClient>().Transport);
    }

    [Fact]
    public async Task AddDatabentoReferenceThenHistorical_StillYieldsOneTransport()
    {
        // The other order, because TryAddSingleton is what makes both orders equivalent and
        // nothing else in the registration does.
        await using var provider = Provider(services =>
        {
            services.AddDatabentoReference();
            services.AddDatabentoHistorical();
        });

        Assert.Same(
            provider.GetRequiredService<HistoricalClient>(),
            provider.GetRequiredService<ReferenceClient>().Transport);
    }

    [Fact]
    public async Task AddDatabentoHistoricalThenReference_ConfiguresThePrimaryHandlerOnce()
    {
        // ConfigurePrimaryHttpMessageHandler has no TryAdd form: it appends to a list of builder
        // actions and the last one wins, so the second call built a SocketsHttpHandler on every
        // rotation and discarded it. Harmless, and on the documented both-orders path — which is
        // the wrong place for a reader to find something that looks like a leak.
        //
        // Compared against the single-call container rather than asserted as a literal count, so
        // this states the property — the second call adds nothing — without depending on how many
        // actions the HTTP factory itself installs.
        await using var once = Provider(services => services.AddDatabentoHistorical());
        await using var twice = Provider(services =>
        {
            services.AddDatabentoHistorical();
            services.AddDatabentoReference();
        });

        Assert.Equal(HandlerBuilderActions(once), HandlerBuilderActions(twice));

        static int HandlerBuilderActions(ServiceProvider provider) =>
            provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
                    .Get(DatabentoServiceCollectionExtensions.HttpClientName)
                    .HttpMessageHandlerBuilderActions.Count;
    }

    [Fact]
    public void AddDatabento_NamingASecondRootAfterOneIsPinned_Throws()
    {
        // This test used to assert the opposite, and its comment is why #101 exists: it said the
        // split root was "not a defect to fix here" because "a package cannot tell the two apart".
        // The two it could not tell apart are "the consumer wants the default root" and "the
        // consumer has not called AddDatabento yet" — which is true, and beside the point. Neither
        // has to be identified, because both end in the same place: the first Add* to need a root
        // pins one, and a later call naming a different root is contradicting a decision the
        // collection has already recorded.
        //
        // What it produced before was worse than an exception and quieter: HistoricalOptions bound
        // to Databento:Historical while DatabentoOptions bound to MyApp:Feeds, in a container that
        // starts and resolves. Half the settings come from a key the consumer never wrote.
        var services = Collection();
        services.AddDatabentoHistorical();

        var thrown = Assert.Throws<InvalidOperationException>(() => services.AddDatabento("MyApp:Feeds"));

        // Both roots named, because the reader has to see which one is already in force to know
        // whether the fix is to move the call or to change its argument.
        Assert.Contains("MyApp:Feeds", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("\"Databento\"", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddDatabento_NamingTheRootThatIsAlreadyPinned_IsANoOp()
    {
        // The benign half of the reorder, and it has to stay benign: calling AddDatabento() after
        // a registration has pinned the default root is naming the same root, not a second one.
        // A guard that threw here would fail the commonest ordering mistake there is, which is
        // the one that was never broken.
        var services = Collection();
        services.AddDatabentoHistorical();
        services.AddDatabento();
        services.AddDatabento(DatabentoOptions.DefaultSectionName);

        using var provider = services.BuildServiceProvider();

        // Both halves bound, and both from the same root — which is the property the throw above
        // exists to protect. ApiKey is checked for presence rather than value: ApiKey.ToString()
        // is redacted by design, so a test that compared it would be asserting the redaction.
        Assert.NotNull(provider.GetRequiredService<IOptions<DatabentoOptions>>().Value.ApiKey);
        Assert.Equal("conventional-root", provider.GetRequiredService<IOptions<HistoricalOptions>>().Value.UserAgentExtension);
    }

    [Fact]
    public void AddDatabento_NamingThePinnedRootInADifferentCase_IsAccepted()
    {
        // IConfiguration resolves keys case-insensitively, so these name one section. A guard that
        // compared them ordinally would report a conflict the configuration system does not have,
        // and the consumer would be told to fix a spelling that already works.
        var services = Collection();
        services.AddDatabentoHistorical();

        services.AddDatabento("databento");

        using var provider = services.BuildServiceProvider();
        Assert.Equal(
            "conventional-root",
            provider.GetRequiredService<IOptions<HistoricalOptions>>().Value.UserAgentExtension);
    }

    [Fact]
    public void AddDatabento_TwiceNamingTwoNonDefaultRoots_Throws()
    {
        // The same rule without AddDatabentoHistorical in it, so the failure cannot be read as
        // something about the historical client.
        var services = Collection();
        services.AddDatabento("MyApp:Feeds");

        Assert.Throws<InvalidOperationException>(() => services.AddDatabento("MyApp:Other"));
    }

    [Fact]
    public void AddDatabentoHistorical_WithNoAddDatabentoAnywhere_ReadsTheRootApiKey()
    {
        // A defect #101 did not know about, found by mutation-testing the pin and fixed by the
        // same change. AddDatabento was the only call that bound DatabentoOptions, and no other
        // Add* called it — so a consumer whose Program.cs is just AddDatabentoHistorical() got a
        // DatabentoOptions that no configuration had ever touched, and the root key was invisible.
        //
        // What made it worth a test of its own is the failure it produced:
        //
        //   Databento:Historical:ApiKey — no API key found. Checked Databento:Historical:ApiKey,
        //   Databento:ApiKey, and the DATABENTO_API_KEY environment variable.
        //
        // Databento:ApiKey is present in the configuration below. The message names a key it did
        // not read, so the one place a consumer would look to fix this told them it was already
        // checked. Worse on a developer machine with DATABENTO_API_KEY exported, where the third
        // fallback covers for the second and the whole thing only fails in deployment.
        //
        // Resolving HistoricalOptions is what runs the validator, so this assertion is the check:
        // reaching a value at all means the key was found, and the two assertions together mean it
        // was found at the root rather than by an environment variable the harness does not set.
        var services = Collection();
        services.AddDatabentoHistorical();

        using var provider = services.BuildServiceProvider();

        Assert.Equal(
            "conventional-root",
            provider.GetRequiredService<IOptions<HistoricalOptions>>().Value.UserAgentExtension);
        Assert.NotNull(provider.GetRequiredService<IOptions<DatabentoOptions>>().Value.ApiKey);
    }

    [Fact]
    public void AddDatabentoLive_WithNoAddDatabentoAnywhere_ReadsTheRootApiKey()
    {
        // The same gap on the priority-1 path, asserted separately because it fails separately:
        // AddDatabentoLive reaches the root through LiveSessionResolver rather than through
        // HistoricalResolver, so a fix that only reached one of them would leave this green by
        // reading nothing.
        var services = Collection();
        services.AddDatabentoLive("equities");

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IOptions<DatabentoOptions>>().Value.ApiKey);
        Assert.Equal(
            "EQUS.MINI",
            provider.GetRequiredService<IOptionsMonitor<LiveSessionOptions>>().Get("equities").Dataset);
    }

    [Fact]
    public void AddDatabentoLive_WhenSomethingElseRegisteredTheRunnerUnderThatName_Throws()
    {
        // #101 item 3. The idempotence guard read the keyed LiveSessionRunner descriptor, so a
        // consumer's own registration under the same name looked exactly like a second
        // AddDatabentoLive call — and the session came back with no bound options, no validator
        // and no hosted service, silently.
        //
        // A factory descriptor that would throw if resolved, because the guard reads descriptors
        // and never builds one. Constructing a real LiveSessionRunner needs a ResolvedLiveSession,
        // a handler and a supervisor — a socket's worth of fixture for a question asked entirely
        // at registration time.
        var services = Collection();
        services.AddKeyedSingleton<LiveSessionRunner>(
            "equities",
            (_, _) => throw new InvalidOperationException("the consumer's own runner, never resolved here"));

        var thrown = Assert.Throws<InvalidOperationException>(() => services.AddDatabentoLive("equities"));

        Assert.Contains("equities", thrown.Message, StringComparison.Ordinal);
        // The message has to say what would have gone missing, not merely that something is wrong:
        // the container it prevents is one that resolves a runner and never reads a record, which
        // is indistinguishable from a quiet market until someone reads this sentence.
        Assert.Contains("hosted service", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddDatabentoLive_WhenTheRunnerIsOverriddenAfterwards_StaysIdempotentRatherThanThrowing()
    {
        // The asymmetry is deliberate, and this is where it is stated. Registering your own runner
        // *after* AddDatabentoLive overrides ours on purpose — it is how a test double gets in —
        // and that container is fully configured rather than half. Only the pre-registration case
        // is the silent one, so only it throws.
        var services = Collection();
        services.AddDatabentoLive("equities");
        services.AddKeyedSingleton<LiveSessionRunner>("equities", (_, _) => throw new InvalidOperationException("not resolved"));

        services.AddDatabentoLive("equities");

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    /// <summary>
    /// A service collection with configuration and logging and no Databento registration at all —
    /// unlike <see cref="Provider"/>, which calls <c>AddDatabento()</c> for its callers. The tests
    /// above are about which call pins the root, so none of them can have it pinned for them.
    /// </summary>
    private static ServiceCollection Collection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Databento:ApiKey"] = Key,
                ["Databento:Historical:UserAgentExtension"] = "conventional-root",
                ["Databento:Live:equities:Dataset"] = "EQUS.MINI",
                ["Databento:Live:equities:Subscriptions:0:Schema"] = "trades",
                ["Databento:Live:equities:Subscriptions:0:Symbols:0"] = "AAPL",
                ["MyApp:Feeds:ApiKey"] = Key,
                ["MyApp:Feeds:Historical:UserAgentExtension"] = "custom-root",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        return services;
    }

    [Fact]
    public async Task AddDatabentoLive_TwiceForOneName_RegistersOneRunnerAndOneHostedService()
    {
        // Not a duplicate that does nothing. The host starts both hosted services against the one
        // keyed runner, so the second StartAsync throws "This session is Running" — after the
        // first has already opened a billable session, with a message that reads like a bug in
        // this package rather than a registration called twice. Every other Add* in
        // ServiceCollectionExtensions is idempotent; these now are too.
        await using var provider = Provider(services =>
        {
            services.AddDatabentoLive("equities").AddRecordHandler<EquitiesHandler>();
            services.AddDatabentoLive("equities");
        });

        var hosted = Assert.Single(provider.GetServices<IHostedService>());

        Assert.Same(
            provider.GetRequiredKeyedService<LiveSessionRunner>("equities"),
            Assert.IsType<LiveSessionService>(hosted).Runner);
    }

    [Fact]
    public void AddDatabentoLive_RegistersTheHandlerUnderTheSessionName()
    {
        using var provider = Provider(services =>
        {
            services.AddDatabentoLive("equities").AddRecordHandler<EquitiesHandler>();
            services.AddDatabentoLive("futures").AddRecordHandler<FuturesHandler>();
        });

        Assert.IsType<EquitiesHandler>(provider.GetRequiredKeyedService<ILiveRecordHandler>("equities"));
        Assert.IsType<FuturesHandler>(provider.GetRequiredKeyedService<ILiveRecordHandler>("futures"));
    }

    [Fact]
    public void AddDatabentoLive_BindsEachSessionFromItsOwnConfigurationKey()
    {
        using var provider = Provider(services =>
        {
            services.AddDatabentoLive("equities").AddRecordHandler<EquitiesHandler>();
            services.AddDatabentoLive("futures").AddRecordHandler<FuturesHandler>();
        });

        var monitor = provider.GetRequiredService<IOptionsMonitor<LiveSessionOptions>>();
        Assert.Equal("EQUS.MINI", monitor.Get("equities").Dataset);
        Assert.Equal("GLBX.MDP3", monitor.Get("futures").Dataset);
    }

    [Fact]
    public void AddDatabentoLive_WithNoName_UsesTheLiteralDefaultName()
    {
        // Databento:Live:{name} in every case, so Databento:Live:Dataset and
        // Databento:Live:equities are never siblings of different kinds.
        var services = new ServiceCollection();
        var builder = services.AddDatabentoLive();

        Assert.Equal(DatabentoLiveBuilder.DefaultSessionName, builder.Name);
        Assert.Equal(
            "Databento:Live:Default",
            LiveSessionResolver.PathFor(DatabentoOptions.DefaultSectionName, builder.Name));
    }

    [Fact]
    public void AddDatabentoLive_WithALambda_OverridesTheBoundValue()
    {
        using var provider = Provider(services =>
            services.AddDatabentoLive("equities", options => options.Dataset = "XNAS.ITCH")
                    .AddRecordHandler<EquitiesHandler>());

        Assert.Equal(
            "XNAS.ITCH",
            provider.GetRequiredService<IOptionsMonitor<LiveSessionOptions>>().Get("equities").Dataset);
    }

    [Fact]
    public void AddDatabentoHistorical_WithALambda_OverridesTheBoundValue()
    {
        // The Historical analogue of the Live test above: UserAgentExtension is bound from
        // configuration ("bound-value", set in Provider()'s dictionary) and the lambda overrides
        // it. If AddDatabentoHistorical(configure) ever registered Configure before
        // BindConfiguration, the bound value would win instead and this would fail.
        using var provider = Provider(services =>
            services.AddDatabentoHistorical(options => options.UserAgentExtension = "lambda-value"));

        Assert.Equal(
            "lambda-value",
            provider.GetRequiredService<IOptions<HistoricalOptions>>().Value.UserAgentExtension);
    }

    private sealed class EquitiesHandler : ILiveRecordHandler
    {
        public void OnRecord(scoped RecordRef record)
        {
        }

        public ValueTask OnFlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class FuturesHandler : ILiveRecordHandler
    {
        public void OnRecord(scoped RecordRef record)
        {
        }

        public ValueTask OnFlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
    // ------------------------------------------------------------------------------------------
    // CloseTimeout reaches the runner — #98. It previously did not: the key did not exist, and
    // CreateRunner never set the property, so every hosted session got a fixed five seconds.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task AddDatabentoLive_PassesTheConfiguredCloseTimeoutToTheRunner()
    {
        // await using, not using: LiveSessionRunner is IAsyncDisposable only, so a synchronous
        // container dispose throws. Same reason the historical/reference tests above do it.
        await using var provider = ProviderWith(
            new Dictionary<string, string?> { ["Databento:Live:equities:CloseTimeout"] = "PT2S" },
            services => services.AddDatabentoLive("equities").AddRecordHandler<EquitiesHandler>());

        Assert.Equal(
            Duration.FromSeconds(2),
            provider.GetRequiredKeyedService<LiveSessionRunner>("equities").CloseTimeout);
    }

    [Fact]
    public async Task AddDatabentoLive_WithNoCloseTimeoutConfigured_LeavesTheRunnersDefault()
    {
        await using var provider = ProviderWith(
            [],
            services => services.AddDatabentoLive("equities").AddRecordHandler<EquitiesHandler>());

        Assert.Equal(
            Duration.FromSeconds(5),
            provider.GetRequiredKeyedService<LiveSessionRunner>("equities").CloseTimeout);
    }

    // ------------------------------------------------------------------------------------------
    // The default session is configurable in code without naming it — #99. Before it, a lambda
    // for the default session meant spelling DatabentoLiveBuilder.DefaultSessionName out, which
    // is the one name in the family a caller should never have to know.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void AddDatabentoLive_WithALambdaAndNoName_ConfiguresTheDefaultSession()
    {
        // Bound value in configuration, lambda over the top. Two ways this can be wrong and one
        // assertion catches both, because both leave EQUS.MINI standing: registering Configure
        // before BindConfiguration, so the bound value wins; and applying the lambda to any name
        // other than Default, so nothing touches the options Get() returns here.
        using var provider = ProviderWith(
            new Dictionary<string, string?>
            {
                ["Databento:Live:Default:Dataset"] = "EQUS.MINI",
                ["Databento:Live:Default:Subscriptions:0:Schema"] = "trades",
                ["Databento:Live:Default:Subscriptions:0:Symbols:0"] = "AAPL",
            },
            services => services.AddDatabentoLive(options => options.Dataset = "XNAS.ITCH")
                                .AddRecordHandler<EquitiesHandler>());

        Assert.Equal(
            "XNAS.ITCH",
            provider.GetRequiredService<IOptionsMonitor<LiveSessionOptions>>()
                    .Get(DatabentoLiveBuilder.DefaultSessionName)
                    .Dataset);
    }

    [Fact]
    public async Task AddDatabentoLive_WithALambdaAndNoName_ReachesTheRunnerWithNoConfigurationAtAll()
    {
        // The case the overload exists for: a host that configures its one session entirely in
        // Program.cs. Nothing under Databento:Live:Default is in the configuration dictionary, so
        // every value the resolver needs comes from the lambda — and the builder it returns is the
        // default session's, which is what lets AddRecordHandler chain off it.
        await using var provider = ProviderWith(
            [],
            services => services.AddDatabentoLive(options =>
                                {
                                    options.Dataset = "XNAS.ITCH";
                                    options.Subscriptions.Add(new SubscriptionOptions
                                    {
                                        Schema = "trades",
                                        Symbols = { "AAPL" },
                                    });
                                })
                                .AddRecordHandler<EquitiesHandler>());

        var runner = provider.GetRequiredKeyedService<LiveSessionRunner>(DatabentoLiveBuilder.DefaultSessionName);
        Assert.Equal(DatabentoLiveBuilder.DefaultSessionName, runner.Session.Name);
        Assert.Equal("XNAS.ITCH", runner.Session.Dataset);
        Assert.Equal(Symbols.From("AAPL"), Assert.Single(runner.Session.Subscriptions).Symbols);
    }

    [Fact]
    public void AddDatabentoLive_WithALambdaAndNoName_ReturnsTheDefaultSessionsBuilder()
    {
        var services = new ServiceCollection();

        var builder = services.AddDatabentoLive(options => options.Dataset = "XNAS.ITCH");

        Assert.Equal(DatabentoLiveBuilder.DefaultSessionName, builder.Name);
        Assert.Same(services, builder.Services);
    }

    /// <summary>
    /// A container like <see cref="Provider"/>'s, plus <paramref name="extra"/> configuration keys.
    /// </summary>
    private static ServiceProvider ProviderWith(
        Dictionary<string, string?> extra,
        Action<IServiceCollection> register)
    {
        var keys = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Databento:ApiKey"] = Key,
            ["Databento:Live:equities:Dataset"] = "EQUS.MINI",
            ["Databento:Live:equities:Subscriptions:0:Schema"] = "trades",
            ["Databento:Live:equities:Subscriptions:0:Symbols:0"] = "AAPL",
        };

        foreach (var (key, value) in extra)
        {
            keys[key] = value;
        }

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(keys).Build());
        services.AddLogging();
        services.AddDatabento();
        register(services);
        return services.BuildServiceProvider();
    }
}
