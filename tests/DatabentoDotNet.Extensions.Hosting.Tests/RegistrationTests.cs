using DatabentoDotNet.Dbn;
using DatabentoDotNet.Historical;
using DatabentoDotNet.Reference;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
        Assert.Equal("Databento:Live:Default", LiveSessionResolver.PathFor(builder.Name));
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
}
