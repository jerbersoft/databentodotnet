using DatabentoDotNet.Dbn;
using DatabentoDotNet.Dbn.Publishers;
using DatabentoDotNet.Live;
using DatabentoDotNet.Live.Tests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DatabentoDotNet.Extensions.Hosting.Tests;

/// <summary>
/// The wiring: does the container build a runner from the resolved options, does startup happen
/// during the host's own start, and does a wrong key stop the boot.
/// </summary>
/// <remarks>
/// <b><see cref="LiveSessionService"/> itself is thin by construction, and there is nothing in it
/// worth a test of its own.</b> Everything about the session loop — draining, flushing, ordering,
/// faulting, reconnecting — is already covered with no host and no container by
/// <c>LiveSessionRunnerTests</c> and <c>LiveSessionReconnectTests</c>. What is left here is
/// whether a real <see cref="IHost"/> resolves the right thing at the right time, following the
/// same gateway-sequencing rules those two files established: gateway-side handshake calls are
/// awaited in order inside one task, started before the client side runs.
/// </remarks>
public class LiveSessionServiceTests
{
    private static readonly string DatasetName = Dataset.XnasItch.ToWireString();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// Builds a host with <c>Databento:ApiKey</c> set to <see cref="MockLiveGateway.TestApiKey"/>
    /// and every <paramref name="sessionConfigurations"/> merged in, then hands the collection to
    /// <paramref name="register"/> for the <c>AddDatabentoLive</c>/<c>AddRecordHandler</c> calls a
    /// test needs. The gateway address itself never goes through configuration here — see the
    /// per-test calls below, which set it through the lambda overload instead, the honest way to
    /// say "a test needs a gateway a configuration file would never name".
    /// </summary>
    private static IHost BuildHost(
        Action<IServiceCollection> register,
        params IReadOnlyDictionary<string, string?>[] sessionConfigurations)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["Databento:ApiKey"] = MockLiveGateway.TestApiKey,
        };

        foreach (var sessionConfiguration in sessionConfigurations)
        {
            foreach (var pair in sessionConfiguration)
            {
                configuration[pair.Key] = pair.Value;
            }
        }

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Services.AddDatabento();
        register(builder.Services);
        return builder.Build();
    }

    /// <summary>One session's worth of configuration: a dataset and a single <c>mbo</c>/<c>AAPL</c>
    /// subscription, reconnection turned off so a test's failures are the wiring's rather than the
    /// backoff's — the same reason <c>LiveSessionRunnerTests.Session</c> disables it.</summary>
    private static Dictionary<string, string?> SessionConfiguration(string name) => new()
    {
        [$"Databento:Live:{name}:Dataset"] = DatasetName,
        [$"Databento:Live:{name}:Subscriptions:0:Schema"] = "mbo",
        [$"Databento:Live:{name}:Subscriptions:0:Symbols:0"] = "AAPL",
        [$"Databento:Live:{name}:Reconnect:Enabled"] = "false",
    };

    /// <summary>Runs the gateway's side of connect, authenticate, subscribe and start. Ported
    /// verbatim from <c>LiveSessionRunnerTests.ServeStartupAsync</c>: the three steps are awaited
    /// in order inside one task, started before the client side runs.</summary>
    private static async Task ServeStartupAsync(MockLiveGateway gateway)
    {
        await gateway.AuthenticateAsync(cancellationToken: Cancel);
        await gateway.ExpectSubscribeAsync(
            new ExpectedSubscription { Schema = Schema.Mbo, StypeIn = SType.RawSymbol, Symbols = ["AAPL"] },
            isLast: true,
            Cancel);
        await gateway.StartAsync(Cancel);
    }

    /// <summary>
    /// Runs the handshake up to the client's authentication request, then answers with
    /// <paramref name="response"/> instead of a success line — <c>LiveSessionRunnerTests.RejectAsync</c>'s
    /// own pattern, ultimately from <c>LiveClientAuthenticationTests.RejectAsync</c>. A wrong
    /// <c>ExpectedApiKey</c> would make the mock's own CRAM check throw before it ever answers the
    /// client (a fixture bug, not a rejection); accepting the handshake normally and answering
    /// with <c>success=0</c> is what a real gateway does when it declines a key it understood.
    /// </summary>
    private static async Task RejectAsync(MockLiveGateway gateway, string response)
    {
        await gateway.ExpectAuthenticationAsync(cancellationToken: Cancel);
        await gateway.SendAsync(response, Cancel);
    }

    /// <summary>
    /// Wraps a <see cref="RecordingHandler"/> with a data-driven synchronization point: <see cref="AllReceived"/>
    /// completes once <see cref="OnRecord"/> has been called <c>expected</c> times.
    /// </summary>
    /// <remarks>
    /// A session run by <see cref="LiveSessionService"/> drains on a background task the test does
    /// not control the scheduling of. Bytes already being ahead of a close on the wire is not
    /// enough to guarantee the drain has happened by the time this test thread reaches
    /// <c>CloseAsync</c>/<c>StopAsync</c> — under load, that background continuation can still lose
    /// the race. Awaiting <see cref="AllReceived"/> instead of a fixed span of wall-clock time is
    /// what makes the ordering in <c>ExecuteAsync_DeliversRecordsToTheRegisteredHandler</c> and
    /// <c>TwoSessions_RunIndependently</c> deterministic rather than merely usually true.
    /// </remarks>
    private sealed class CountingHandler(RecordingHandler inner, int expected) : ILiveRecordHandler
    {
        private readonly TaskCompletionSource _allReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _seen;

        public Task AllReceived => _allReceived.Task;

        public void OnRecord(scoped RecordRef record)
        {
            inner.OnRecord(record);

            if (Interlocked.Increment(ref _seen) == expected)
            {
                _allReceived.TrySetResult();
            }
        }

        public ValueTask OnFlushAsync(CancellationToken cancellationToken) => inner.OnFlushAsync(cancellationToken);
    }

    [Fact]
    public async Task StartAsync_ConnectsDuringHostStartup()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        var handler = new RecordingHandler();

        var host = BuildHost(
            services => services
                .AddDatabentoLive("equities", options => options.Gateway = gateway.Address.ToString())
                .AddRecordHandler(_ => handler),
            SessionConfiguration("equities"));
        await using var disposable = (IAsyncDisposable)host;

        var serving = ServeStartupAsync(gateway);

        // The load-bearing assertion: State is already Running by the time host.StartAsync()
        // returns, i.e. the session was established during the host's own start rather than in a
        // background task the host has already stopped watching.
        await host.StartAsync(Cancel);
        await serving;

        var runner = host.Services.GetRequiredKeyedService<LiveSessionRunner>("equities");
        Assert.Equal(LiveSessionState.Running, runner.State);

        await host.StopAsync(Cancel);
    }

    [Fact]
    public async Task ExecuteAsync_DeliversRecordsToTheRegisteredHandler()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        var handler = new RecordingHandler();
        var counting = new CountingHandler(handler, expected: 2);

        var host = BuildHost(
            services => services
                .AddDatabentoLive("equities", options => options.Gateway = gateway.Address.ToString())
                .AddRecordHandler(_ => counting),
            SessionConfiguration("equities"));
        await using var disposable = (IAsyncDisposable)host;

        var serving = ServeStartupAsync(gateway);
        await host.StartAsync(Cancel);
        await serving;

        await gateway.SendRecordAsync(SyntheticMbo.Record(1), Cancel);
        await gateway.SendRecordAsync(SyntheticMbo.Record(2), Cancel);
        await counting.AllReceived;

        await gateway.CloseAsync();
        await host.StopAsync(Cancel);

        Assert.Equal([1u, 2u], handler.Sequences);
    }

    [Fact]
    public async Task StartAsync_WithAKeyTheGatewayRejects_FailsTheBoot()
    {
        await using var gateway = new MockLiveGateway(DatasetName);

        var host = BuildHost(
            services => services
                .AddDatabentoLive("equities", options => options.Gateway = gateway.Address.ToString())
                .AddRecordHandler(_ => new RecordingHandler()),
            SessionConfiguration("equities"));
        await using var disposable = (IAsyncDisposable)host;

        var serving = RejectAsync(gateway, "success=0|error=invalid API key");

        await Assert.ThrowsAsync<DatabentoAuthenticationException>(() => host.StartAsync(Cancel));
        await serving;

        // The runner was already resolved from the container (that is how LiveSessionService got
        // it), so it is a second, independent witness that the failure is real rather than an
        // artifact of Assert.ThrowsAsync alone.
        var runner = host.Services.GetRequiredKeyedService<LiveSessionRunner>("equities");
        Assert.Equal(LiveSessionState.Faulted, runner.State);
        Assert.IsType<DatabentoAuthenticationException>(runner.Fault);
    }

    [Fact]
    public async Task StopAsync_ClosesTheSession()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        var handler = new RecordingHandler();

        var host = BuildHost(
            services => services
                .AddDatabentoLive("equities", options => options.Gateway = gateway.Address.ToString())
                .AddRecordHandler(_ => handler),
            SessionConfiguration("equities"));
        await using var disposable = (IAsyncDisposable)host;

        var serving = ServeStartupAsync(gateway);
        await host.StartAsync(Cancel);
        await serving;

        // No close from the gateway side: the session is still open, and host.StopAsync() has to
        // cancel it out from under a pending read on its own — the same shutdown
        // BackgroundService.StopAsync always does, and the one this override exists not to bypass.
        await host.StopAsync(Cancel);

        var runner = host.Services.GetRequiredKeyedService<LiveSessionRunner>("equities");
        Assert.Equal(LiveSessionState.Stopped, runner.State);
        Assert.Null(runner.Fault);
    }

    [Fact]
    public async Task StopAsync_CalledASecondTime_DoesNotThrowOrRetransition()
    {
        // This is NOT a deterministic test of LiveSessionService.StopAsync's dispatch-race
        // fallback (see its remarks) — closing cleanly from the gateway side first, below, gives
        // the thread pool materially more time to dispatch the queued ExecuteAsync work item than
        // the already-rare race needs, so the ordinary outcome here is ExecuteTask.IsCanceled
        // staying false and the fallback never running at all. What this test actually pins is
        // narrower and still real: the pump reaches Stopped on its own, and a second
        // host.StopAsync() call afterwards must not throw or re-transition an already-Stopped
        // runner — which is true with or without the fallback, since base.StopAsync's own
        // cancel-an-already-cancelled-source and wait-on-an-already-completed-task are no-ops.
        //
        // The fallback branch itself is deliberately not forced here. Three ways to force it were
        // considered and rejected: reflecting onto BackgroundService's private _executeTask field
        // (brittle against a BCL field name, and this repository has no reflection of that kind
        // anywhere else); throttling the global ThreadPool to win the race (process-wide state,
        // unsafe under a parallel test suite); and adding production surface purely so a test
        // could observe dispatch (the minimal-public-surface bar this fix was built under). What
        // covers the fallback instead: LiveSessionRunnerTests's
        // RunAsync_GivenAnAlreadyCancelledToken_StopsWithoutEverPumping deterministically pins the
        // runner-side behaviour the fallback depends on; the guard itself
        // (ExecuteTask.IsCanceled && Runner.State == Running) is two conditions readable at the
        // call site; and #95's 50-consecutive-run verification covers the integration. A reader
        // should not infer a deterministic test of the fallback exists — it does not, and the gap
        // is accepted rather than hidden.
        await using var gateway = new MockLiveGateway(DatasetName);
        var handler = new RecordingHandler();

        var host = BuildHost(
            services => services
                .AddDatabentoLive("equities", options => options.Gateway = gateway.Address.ToString())
                .AddRecordHandler(_ => handler),
            SessionConfiguration("equities"));
        await using var disposable = (IAsyncDisposable)host;

        var serving = ServeStartupAsync(gateway);
        await host.StartAsync(Cancel);
        await serving;

        await gateway.CloseAsync();
        await host.StopAsync(Cancel);

        var runner = host.Services.GetRequiredKeyedService<LiveSessionRunner>("equities");
        Assert.Equal(LiveSessionState.Stopped, runner.State);
        Assert.Null(runner.Fault);

        await host.StopAsync(Cancel);

        Assert.Equal(LiveSessionState.Stopped, runner.State);
        Assert.Null(runner.Fault);
    }

    [Fact]
    public async Task TwoSessions_RunIndependently()
    {
        await using var equitiesGateway = new MockLiveGateway(DatasetName);
        await using var futuresGateway = new MockLiveGateway(DatasetName);
        var equitiesHandler = new RecordingHandler();
        var futuresHandler = new RecordingHandler();
        var equitiesCounting = new CountingHandler(equitiesHandler, expected: 1);
        var futuresCounting = new CountingHandler(futuresHandler, expected: 1);

        var host = BuildHost(
            services =>
            {
                services.AddDatabentoLive("equities", options => options.Gateway = equitiesGateway.Address.ToString())
                        .AddRecordHandler(_ => equitiesCounting);
                services.AddDatabentoLive("futures", options => options.Gateway = futuresGateway.Address.ToString())
                        .AddRecordHandler(_ => futuresCounting);
            },
            SessionConfiguration("equities"),
            SessionConfiguration("futures"));
        await using var disposable = (IAsyncDisposable)host;

        // The generic host starts hosted services one at a time, in registration order, and awaits
        // each one's StartAsync before moving to the next — so both gateway-side handshakes must
        // already be running before host.StartAsync() is called, exactly as every other test here
        // starts its one gateway-side task before the client-side call that needs it.
        var servingEquities = ServeStartupAsync(equitiesGateway);
        var servingFutures = ServeStartupAsync(futuresGateway);

        await host.StartAsync(Cancel);
        await Task.WhenAll(servingEquities, servingFutures);

        await equitiesGateway.SendRecordAsync(SyntheticMbo.Record(1), Cancel);
        await futuresGateway.SendRecordAsync(SyntheticMbo.Record(2), Cancel);
        await Task.WhenAll(equitiesCounting.AllReceived, futuresCounting.AllReceived);

        await equitiesGateway.CloseAsync();
        await futuresGateway.CloseAsync();

        await host.StopAsync(Cancel);

        Assert.Equal([1u], equitiesHandler.Sequences);
        Assert.Equal([2u], futuresHandler.Sequences);

        // The decisive check for AddSingleton<IHostedService> over AddHostedService<T>: the latter
        // deduplicates by implementation type, so two LiveSessionService registrations would
        // collapse into one and only one session would ever run. Assert the count rather than
        // trust the paragraph explaining why it matters.
        Assert.Equal(2, host.Services.GetServices<IHostedService>().Count());
    }
}
