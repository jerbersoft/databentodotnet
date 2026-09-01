using System.Diagnostics.Metrics;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Dbn.Publishers;
using DatabentoDotNet.Live;
using DatabentoDotNet.Live.Tests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NodaTime;

namespace DatabentoDotNet.Extensions.Hosting.Tests;

/// <summary>
/// The two things an operator needs and nobody else should pay for: four metric instruments, and
/// a health check that only exists once somebody asks for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every metrics test runs with a <see cref="MeterListener"/> attached, and that is the point
/// rather than the instrumentation.</b> <c>Counter&lt;T&gt;.Add</c> short-circuits when nothing is
/// subscribed, so an implementation that allocated per call would measure as free in any test
/// without a listener and would start allocating the moment a consumer wired up OpenTelemetry.
/// Task 11's allocation test attaches one for the same reason; these tests attach one so that the
/// counts being asserted are counts that actually travelled through the listener plumbing.
/// </para>
/// <para>
/// <b>Each metrics test uses a session name of its own, and the recorder filters on it.</b> The
/// meter's name is a constant shared by every <see cref="LiveSessionMetrics"/> in the process, and
/// <c>AddDatabento</c> now registers one — so a session run by another test class, in parallel,
/// publishes to a meter this listener is also subscribed to. Filtering on the
/// <c>databento.session</c> tag is what makes a sum in here a sum of this test's own measurements.
/// The one assertion that deliberately looks at every measurement — that each carries exactly one
/// tag, keyed <c>databento.session</c> — is safe against that pollution because a measurement from
/// another session satisfies it too.
/// </para>
/// <para>
/// <b>The gateway sequencing follows <c>LiveSessionServiceTests</c> and
/// <c>LiveSessionReconnectTests</c> exactly:</b> the handshake's three steps are awaited in order
/// inside one task, started before the client side runs, and the one transient failure here is
/// provoked by silence — never by a graceful close, which the client treats as a clean stream end
/// and does not reconnect from.
/// </para>
/// </remarks>
public class ObservabilityTests
{
    private const string SessionTag = "databento.session";
    private const string RecordsReceived = "databento.live.records.received";
    private const string SessionsStarted = "databento.live.sessions.started";
    private const string ReconnectsAttempted = "databento.live.reconnects.attempted";
    private const string FlushDuration = "databento.live.flush.duration";

    /// <summary>The session id the mock reports on a reconnect's handshake, as a real gateway would.</summary>
    private const string SecondSessionId = "6";

    private const string RegistrationKey = "32-character-with-lots-of-filler";

    private static readonly string DatasetName = Dataset.XnasItch.ToWireString();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// One session against <paramref name="gateway"/>, named <paramref name="name"/> so the tag on
    /// every measurement it publishes is unique to the test that built it.
    /// </summary>
    /// <remarks>
    /// The read timeout is short enough that a silent gateway is a failure inside a test's
    /// patience, which is how the reconnect test below provokes one. The subscription carries no
    /// replay <c>Start</c>, so the first handshake and a resubscribe expect the same line.
    /// </remarks>
    private static ResolvedLiveSession Session(MockLiveGateway gateway, string name, ResolvedReconnect reconnect) => new()
    {
        Name = name,
        ApiKey = new ApiKey(MockLiveGateway.TestApiKey),
        Dataset = gateway.Dataset,
        Gateway = gateway.Address,
        ReadTimeout = Duration.FromMilliseconds(250),
        Subscriptions =
        [
            new Subscription { Schema = Schema.Mbo, Symbols = Symbols.From(["AAPL"]) },
        ],
        Reconnect = reconnect,
    };

    /// <summary>Reconnection off, so a test's failures are its own rather than the backoff's.</summary>
    private static ResolvedReconnect NoReconnect => ResolvedReconnect.Default with { Enabled = false };

    /// <summary>
    /// Runs the gateway's side of one handshake — authenticate, then subscribe, then start, each
    /// awaited before the next begins, all inside this one task.
    /// </summary>
    private static async Task ServeHandshakeAsync(
        MockLiveGateway gateway, string sessionId = MockLiveGateway.SessionId)
    {
        await gateway.AuthenticateAsync(sessionId, cancellationToken: Cancel);
        await gateway.ExpectSubscribeAsync(
            new ExpectedSubscription { Schema = Schema.Mbo, StypeIn = SType.RawSymbol, Symbols = ["AAPL"] },
            isLast: true,
            Cancel);
        await gateway.StartAsync(Cancel);
    }

    /// <summary>Asks <paramref name="runner"/>'s health check the question the framework would.</summary>
    /// <remarks>
    /// A real <see cref="HealthCheckRegistration"/> rather than a bare
    /// <see cref="HealthCheckContext"/>, because the check reads
    /// <see cref="HealthCheckRegistration.FailureStatus"/> to honour the status a caller passed to
    /// <c>AddHealthCheck</c> — and that property defaults to
    /// <see cref="HealthStatus.Unhealthy"/> when none was.
    /// </remarks>
    private static Task<HealthCheckResult> CheckAsync(LiveSessionRunner runner)
    {
        var check = new LiveSessionHealthCheck(runner);

        return check.CheckHealthAsync(
            new HealthCheckContext
            {
                Registration = new HealthCheckRegistration(
                    "databento-live-test", _ => check, failureStatus: null, tags: null),
            },
            Cancel);
    }

    /// <summary>A container carrying enough configuration for <c>AddDatabentoLive</c> to bind against.</summary>
    private static ServiceProvider Provider(Action<IServiceCollection> register)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Databento:ApiKey"] = RegistrationKey,
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
        services.AddDatabento();
        register(services);
        return services.BuildServiceProvider();
    }

    private static HealthCheckRegistration[] RegistrationsOf(ServiceProvider provider) =>
        provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations.ToArray();

    [Fact]
    public async Task Metrics_CountRecordsOncePerFlush_NotOncePerRecord()
    {
        const string SessionName = "metrics-flush";
        const int RecordCount = 8;

        using var recorder = new MeasurementRecorder();
        using var metrics = new LiveSessionMetrics();

        await using var gateway = new MockLiveGateway(DatasetName);
        var handler = new FlushCountingHandler(RecordCount);

        await using var runner = new LiveSessionRunner(
            Session(gateway, SessionName, NoReconnect),
            handler,
            new ReconnectSupervisor(NoReconnect),
            logger: null,
            metrics);

        var serving = ServeHandshakeAsync(gateway);
        await runner.StartSessionAsync(Cancel);
        await serving;

        var running = runner.RunAsync(Cancel);

        for (var sequence = 1u; sequence <= RecordCount; sequence++)
        {
            await gateway.SendRecordAsync(SyntheticMbo.Record(sequence), Cancel);
        }

        await handler.AllReceived;
        await gateway.CloseAsync();
        await running;

        var published = recorder.For(RecordsReceived, SessionName);

        // Nothing lost and nothing double-counted.
        Assert.Equal(RecordCount, published.Sum(measurement => (int)measurement.Value));

        // The load-bearing assertion, and it is stronger than a count: each measurement's value is
        // the number of records that particular flush carried, in order. A Counter<long>.Add inside
        // the drain loop would publish RecordCount measurements of 1 each, which matches this
        // sequence only if every fill happened to deliver exactly one record — and the eight are
        // written to the socket before the first of them is drained, so one does not.
        Assert.Equal(
            handler.RecordsPerFlush,
            published.Select(measurement => (long)measurement.Value).ToArray());
    }

    [Fact]
    public async Task Metrics_TagEveryMeasurementWithTheSessionName()
    {
        const string SessionName = "metrics-tags";
        const int RecordCount = 2;

        using var recorder = new MeasurementRecorder();
        using var metrics = new LiveSessionMetrics();

        await using var gateway = new MockLiveGateway(DatasetName);
        var handler = new FlushCountingHandler(RecordCount);

        await using var runner = new LiveSessionRunner(
            Session(gateway, SessionName, NoReconnect),
            handler,
            new ReconnectSupervisor(NoReconnect),
            logger: null,
            metrics);

        var serving = ServeHandshakeAsync(gateway);
        await runner.StartSessionAsync(Cancel);
        await serving;

        var running = runner.RunAsync(Cancel);

        for (var sequence = 1u; sequence <= RecordCount; sequence++)
        {
            await gateway.SendRecordAsync(SyntheticMbo.Record(sequence), Cancel);
        }

        await handler.AllReceived;
        await gateway.CloseAsync();
        await running;

        // Every measurement on this meter — including any a session in another test class
        // published while this one ran — carries exactly one tag, and it is the session tag.
        Assert.All(
            recorder.All,
            measurement => Assert.Equal(SessionTag, Assert.Single(measurement.Tags).Key));

        // And the ones this session published carry *this* session's name, which is the half the
        // assertion above cannot make on its own: the recorder finds these by that tag's value, so
        // an untagged or mis-tagged measurement would leave every one of these empty.
        Assert.Single(recorder.For(SessionsStarted, SessionName));
        Assert.Equal(
            RecordCount,
            recorder.For(RecordsReceived, SessionName).Sum(measurement => (int)measurement.Value));
        Assert.NotEmpty(recorder.For(FlushDuration, SessionName));
    }

    [Fact]
    public async Task Metrics_CountReconnectAttemptsAndSessionStarts()
    {
        const string SessionName = "metrics-reconnect";

        using var recorder = new MeasurementRecorder();
        using var metrics = new LiveSessionMetrics();

        await using var gateway = new MockLiveGateway(DatasetName);

        // Set from inside the first Delay call, which is proof the read budget has already elapsed
        // and torn the client's socket down — only then is releasing the gateway's half safe, and
        // not itself the failure. LiveSessionReconnectTests explains this at length.
        Task? reserving = null;
        var ready = new TaskCompletionSource();

        var supervisor = new ReconnectSupervisor(ResolvedReconnect.Default)
        {
            Delay = async (_, _) =>
            {
                await gateway.CloseAsync();
                reserving = ServeHandshakeAsync(gateway, SecondSessionId);
                ready.TrySetResult();
            },
        };

        await using var runner = new LiveSessionRunner(
            Session(gateway, SessionName, ResolvedReconnect.Default),
            new RecordingHandler(),
            supervisor,
            logger: null,
            metrics);

        var serving = ServeHandshakeAsync(gateway);
        await runner.StartSessionAsync(Cancel);
        await serving;

        var running = runner.RunAsync(Cancel);

        // No close from here: the gateway simply says nothing, the client's own 250 ms read budget
        // expires, and the runner enters the backoff.
        await ready.Task;
        await reserving!;

        await gateway.CloseAsync();
        await running;

        // One attempt, and it succeeded.
        Assert.Equal(
            1,
            recorder.For(ReconnectsAttempted, SessionName).Sum(measurement => (int)measurement.Value));

        // Two session starts, not one: StartSessionAsync opened the first and the successful
        // reconnect opened the second. A restart calls StartAsync and is a newly billed session, so
        // a sessions.started that counted only the first would under-report the sessions this
        // process actually opened — on precisely the path the runner documents as newly billed.
        // The counter above cannot stand in: it counts attempts, and a failed attempt bills
        // nothing.
        Assert.Equal(
            2,
            recorder.For(SessionsStarted, SessionName).Sum(measurement => (int)measurement.Value));
    }

    [Fact]
    public async Task AddDatabentoLive_UsedOnItsOwn_StillAttachesMetrics()
    {
        const string SessionName = "metrics-standalone";

        using var recorder = new MeasurementRecorder();
        await using var gateway = new MockLiveGateway(DatasetName);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Databento:Live:{SessionName}:Dataset"] = DatasetName,
                [$"Databento:Live:{SessionName}:Subscriptions:0:Schema"] = "mbo",
                [$"Databento:Live:{SessionName}:Subscriptions:0:Symbols:0"] = "AAPL",
                [$"Databento:Live:{SessionName}:Reconnect:Enabled"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);

        // No AddDatabento, deliberately. AddDatabentoLive works standalone — the section path falls
        // back to "Databento" when no marker was registered — and that is the arrangement in which
        // a metrics singleton registered only by AddDatabento would leave this session publishing
        // nothing, with no error, no warning and no missing service to notice.
        services.AddDatabentoLive(SessionName, options =>
                {
                    options.ApiKey = MockLiveGateway.TestApiKey;
                    options.Gateway = gateway.Address.ToString();
                })
                .AddRecordHandler(_ => new RecordingHandler());

        await using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<LiveSessionMetrics>());

        var runner = provider.GetRequiredKeyedService<LiveSessionRunner>(SessionName);
        var serving = ServeHandshakeAsync(gateway);
        await runner.StartSessionAsync(Cancel);
        await serving;

        // Resolvable is not the same as wired: this is the runner the container built, publishing.
        Assert.Single(recorder.For(SessionsStarted, SessionName));
    }

    [Fact]
    public async Task HealthCheck_BeforeStarting_IsDegraded()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var runner = new LiveSessionRunner(
            Session(gateway, "health-notstarted", NoReconnect),
            new RecordingHandler(),
            new ReconnectSupervisor(NoReconnect));

        Assert.Equal(LiveSessionState.NotStarted, runner.State);
        Assert.Equal(HealthStatus.Degraded, (await CheckAsync(runner)).Status);
    }

    [Fact]
    public async Task HealthCheck_WhileStarting_IsDegraded()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Cancel);

        await using var runner = new LiveSessionRunner(
            Session(gateway, "health-starting", NoReconnect),
            new RecordingHandler(),
            new ReconnectSupervisor(NoReconnect));

        // Nothing serves this gateway, so the handshake cannot finish — and StartSessionAsync sets
        // Starting synchronously, before its first await, so the state is already Starting by the
        // time the call hands a Task back. Nothing here waits on wall-clock time to observe it.
        var starting = runner.StartSessionAsync(stopping.Token);

        Assert.Equal(LiveSessionState.Starting, runner.State);
        Assert.Equal(HealthStatus.Degraded, (await CheckAsync(runner)).Status);

        await stopping.CancelAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => starting);
    }

    [Fact]
    public async Task HealthCheck_WhileRunning_IsHealthy()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var runner = new LiveSessionRunner(
            Session(gateway, "health-running", NoReconnect),
            new RecordingHandler(),
            new ReconnectSupervisor(NoReconnect));

        var serving = ServeHandshakeAsync(gateway);
        await runner.StartSessionAsync(Cancel);
        await serving;

        Assert.Equal(LiveSessionState.Running, runner.State);
        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(runner)).Status);
    }

    [Fact]
    public async Task HealthCheck_WhileReconnecting_IsDegraded()
    {
        var backingOff = new TaskCompletionSource();

        await using var gateway = new MockLiveGateway(DatasetName);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Cancel);

        // The injected Delay holds the runner inside the backoff for as long as the test needs, and
        // completing backingOff from inside it is proof the state is already Reconnecting rather
        // than a guess: TryRecoverAsync sets the state before it ever calls Delay.
        var supervisor = new ReconnectSupervisor(ResolvedReconnect.Default)
        {
            Delay = async (_, token) =>
            {
                backingOff.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            },
        };

        await using var runner = new LiveSessionRunner(
            Session(gateway, "health-reconnecting", ResolvedReconnect.Default),
            new RecordingHandler(),
            supervisor);

        var serving = ServeHandshakeAsync(gateway);
        await runner.StartSessionAsync(Cancel);
        await serving;

        var running = runner.RunAsync(stopping.Token);

        // Silence, not a close: a clean close is a stream ending, which nothing reconnects from.
        await backingOff.Task;

        Assert.Equal(LiveSessionState.Reconnecting, runner.State);
        Assert.Equal(HealthStatus.Degraded, (await CheckAsync(runner)).Status);

        await stopping.CancelAsync();
        await running;
    }

    [Fact]
    public async Task HealthCheck_AfterACleanStop_IsUnhealthy()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var runner = new LiveSessionRunner(
            Session(gateway, "health-stopped", NoReconnect),
            new RecordingHandler(),
            new ReconnectSupervisor(NoReconnect));

        var serving = ServeHandshakeAsync(gateway);
        await runner.StartSessionAsync(Cancel);
        await serving;
        await gateway.CloseAsync();

        await runner.RunAsync(Cancel);

        // Stopped is not a fault, and it is still not healthy: the worker is doing nothing. A
        // deliberate shutdown makes this unreachable in practice, because the endpoint answering
        // the probe stops with the host.
        Assert.Equal(LiveSessionState.Stopped, runner.State);
        Assert.Null(runner.Fault);
        Assert.Equal(HealthStatus.Unhealthy, (await CheckAsync(runner)).Status);
    }

    [Fact]
    public async Task HealthCheck_AfterAFault_IsUnhealthyAndCarriesTheReason()
    {
        const string Reason = "the handler refused a record";

        await using var gateway = new MockLiveGateway(DatasetName);
        var handler = new RecordingHandler { ThrowOnRecord = new InvalidOperationException(Reason) };

        await using var runner = new LiveSessionRunner(
            Session(gateway, "health-faulted", NoReconnect),
            handler,
            new ReconnectSupervisor(NoReconnect));

        var serving = ServeHandshakeAsync(gateway);
        await runner.StartSessionAsync(Cancel);
        await serving;

        var running = runner.RunAsync(Cancel);
        await gateway.SendRecordAsync(SyntheticMbo.Record(1), Cancel);

        await Assert.ThrowsAsync<InvalidOperationException>(() => running);

        var result = await CheckAsync(runner);

        Assert.Equal(LiveSessionState.Faulted, runner.State);
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains(Reason, result.Description, StringComparison.Ordinal);
        Assert.Same(runner.Fault, result.Exception);
    }

    [Fact]
    public void AddHealthCheck_RegistersUnderADefaultName()
    {
        using var provider = Provider(services => services
            .AddDatabentoLive("equities")
            .AddRecordHandler(_ => new RecordingHandler())
            .AddHealthCheck());

        var registration = Assert.Single(RegistrationsOf(provider));
        Assert.Equal("databento-live-equities", registration.Name);
    }

    [Fact]
    public void AddHealthCheck_WithTwoSessions_RegistersTwoChecks()
    {
        using var provider = Provider(services =>
        {
            services.AddDatabentoLive("equities")
                    .AddRecordHandler(_ => new RecordingHandler())
                    .AddHealthCheck();
            services.AddDatabentoLive("futures")
                    .AddRecordHandler(_ => new RecordingHandler())
                    .AddHealthCheck();
        });

        var names = RegistrationsOf(provider).Select(registration => registration.Name).ToArray();

        Assert.Equal(2, names.Length);
        Assert.Equal(["databento-live-equities", "databento-live-futures"], names.Order().ToArray());
    }

    [Fact]
    public void AddHealthCheck_WhenNeverCalled_RegistersNothing()
    {
        // The opt-in guarantee, asserted rather than described: a consumer who registers a live
        // session and no health check gets no health check, and so pays nothing for one.
        using var provider = Provider(services => services
            .AddDatabentoLive("equities")
            .AddRecordHandler(_ => new RecordingHandler()));

        Assert.Empty(RegistrationsOf(provider));
    }

    /// <summary>
    /// Counts records per flush rather than in total, so a test can assert what each published
    /// measurement should have been rather than only what they should sum to.
    /// </summary>
    /// <remarks>
    /// Every member is touched on the runner's own loop thread and read by the test only after it
    /// has awaited <c>RunAsync</c>, which is what makes the plain <see cref="List{T}"/> safe.
    /// <see cref="AllReceived"/> is the exception and is a <see cref="TaskCompletionSource"/> for
    /// exactly that reason — it is read from the test thread, mid-run.
    /// </remarks>
    private sealed class FlushCountingHandler(int expected) : ILiveRecordHandler
    {
        private readonly TaskCompletionSource _allReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<long> _recordsPerFlush = [];
        private int _seen;
        private long _sinceFlush;

        /// <summary>Completes once <see cref="OnRecord"/> has been called <c>expected</c> times.</summary>
        public Task AllReceived => _allReceived.Task;

        /// <summary>How many records each record-bearing flush carried, in order.</summary>
        public long[] RecordsPerFlush => [.. _recordsPerFlush];

        public void OnRecord(scoped RecordRef record)
        {
            _sinceFlush++;

            if (Interlocked.Increment(ref _seen) == expected)
            {
                _allReceived.TrySetResult();
            }
        }

        public ValueTask OnFlushAsync(CancellationToken cancellationToken)
        {
            // An empty drain publishes nothing, so an empty flush is not recorded here either.
            if (_sinceFlush > 0)
            {
                _recordsPerFlush.Add(_sinceFlush);
                _sinceFlush = 0;
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>One measurement as the listener saw it, with its tags copied out of the span.</summary>
    private sealed record Measurement(string Instrument, double Value, KeyValuePair<string, object?>[] Tags)
    {
        /// <summary>The session this measurement was tagged with, or <see langword="null"/> if it was not.</summary>
        public string? Session =>
            Tags is [{ Key: SessionTag, Value: string session }] ? session : null;
    }

    /// <summary>
    /// A <see cref="MeterListener"/> subscribed to every instrument on
    /// <see cref="LiveSessionMetrics.MeterName"/>, recording what it is handed.
    /// </summary>
    private sealed class MeasurementRecorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Lock _gate = new();
        private readonly List<Measurement> _measurements = [];

        public MeasurementRecorder()
        {
            _listener.InstrumentPublished = static (instrument, listener) =>
            {
                if (instrument.Meter.Name == LiveSessionMetrics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };

            _listener.SetMeasurementEventCallback<long>(
                (instrument, measurement, tags, _) => Record(instrument, measurement, tags));
            _listener.SetMeasurementEventCallback<double>(
                (instrument, measurement, tags, _) => Record(instrument, measurement, tags));

            _listener.Start();
        }

        /// <summary>Everything seen on the meter, this test's measurements and anyone else's.</summary>
        public Measurement[] All
        {
            get
            {
                lock (_gate)
                {
                    return [.. _measurements];
                }
            }
        }

        /// <summary>One instrument's measurements for one session.</summary>
        public Measurement[] For(string instrument, string session)
        {
            lock (_gate)
            {
                return [.. _measurements.Where(
                    measurement => measurement.Instrument == instrument && measurement.Session == session)];
            }
        }

        public void Dispose() => _listener.Dispose();

        private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            // Out of the span before anything else: it is only valid for this call.
            var copied = tags.ToArray();

            lock (_gate)
            {
                _measurements.Add(new Measurement(instrument.Name, value, copied));
            }
        }
    }
}
