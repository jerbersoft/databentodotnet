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
    public async Task AddDatabentoHistorical_BeforeAddDatabento_TakesTheDefaultRoot()
    {
        // The guide tells consumers to call AddDatabento first, and this is the claim behind that
        // sentence: every Add* reads the root at the moment it is called, so the earlier call sees
        // no marker and falls back to the conventional section. Not a defect to fix here — the
        // fallback is what makes a standalone AddDatabentoHistorical() work at all, and a package
        // cannot tell the two apart — but it is documented, so it is pinned.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Databento:ApiKey"] = Key,
                ["Databento:Historical:UserAgentExtension"] = "conventional-root",
                ["MyApp:Feeds:ApiKey"] = Key,
                ["MyApp:Feeds:Historical:UserAgentExtension"] = "custom-root",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddDatabentoHistorical();
        services.AddDatabento("MyApp:Feeds");

        await using var provider = services.BuildServiceProvider();

        Assert.Equal(
            "conventional-root",
            provider.GetRequiredService<IOptions<HistoricalOptions>>().Value.UserAgentExtension);
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
