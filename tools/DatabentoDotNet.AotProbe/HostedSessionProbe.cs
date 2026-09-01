using DatabentoDotNet.Dbn;
using DatabentoDotNet.Dbn.Publishers;
using DatabentoDotNet.Extensions.Hosting;
using DatabentoDotNet.Historical;
using DatabentoDotNet.Live.Tests;
using DatabentoDotNet.Reference;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace DatabentoDotNet.AotProbe;

/// <summary>
/// A generic host — <see cref="HostApplicationBuilder"/>, a container built from
/// <c>DatabentoDotNet.Extensions.Hosting</c>'s keyed registrations, the configuration binding
/// source generator, and a <see cref="BackgroundService"/> — driving a whole live session against
/// <see cref="MockLiveGateway"/>, inside the native binary. Every registration the package
/// publishes is called from this one host.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a fifth probe rather than another check inside <see cref="LiveSessionProbe"/>.</b>
/// That probe reaches <c>DatabentoDotNet.Live</c>'s record loop directly. This one reaches the same
/// loop through <c>DatabentoDotNet.Extensions.Hosting</c> instead: a DI container, an options model
/// bound entirely by the configuration binding source generator, and <c>LiveSessionService</c>
/// running as a hosted service. None of that had ever been through ILC — the trim and AOT analyzers
/// are compile-time reasoning about IL, and #64's argument is that reasoning about IL is not the
/// same claim as running it.
/// </para>
/// <para>
/// <b>One host, and every <c>Add*</c> on it.</b> Until #100 this probe called
/// <c>AddDatabentoLive</c> and the <em>factory</em> <c>AddRecordHandler</c> overload and nothing
/// else, so four registrations shipped without ever meeting ILC —
/// <see cref="DatabentoLiveBuilder.AddRecordHandler{THandler}()"/>,
/// <c>AddDatabentoHistorical</c>, <c>AddDatabentoReference</c> and
/// <see cref="DatabentoLiveBuilder.AddHealthCheck"/>. The generic handler overload is the one that
/// matters most: it is the reflection-shaped member of the family, carrying a
/// <see cref="System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembersAttribute"/> so the
/// container can pick a constructor and resolve its parameters at run time — and this program's
/// whole premise is that an annotation is a claim, not a verification.
/// </para>
/// <para>
/// <b><c>AddInMemoryCollection</c>, not a file.</b> Every probe in this program is offline and
/// self-contained, so the session's configuration is built in memory rather than shipped as a JSON
/// file beside the binary for no reader this probe has.
/// </para>
/// <para>
/// <b>Sessions are declared in code, never conjured from keys</b> — <c>AddDatabentoLive</c>'s own
/// remarks say so, and this probe calls it exactly the way a consumer's <c>Program.cs</c> would:
/// once, by name, with configuration filling in the rest.
/// </para>
/// <para>
/// <b>The gateway sequencing is <see cref="MockGatewayHandshake.ServeAsync"/>'s, not
/// <see cref="LiveSessionProbe"/>'s.</b> <c>LiveSessionService.StartAsync</c> connects,
/// authenticates, subscribes and starts the session <em>before</em> <c>host.StartAsync()</c>
/// returns, so the gateway's three replies have to already be running in a background task before
/// this method calls it — get it backwards and both sides wait on each other rather than one of
/// them failing loudly. That helper is compiled in by <c>&lt;Compile Link&gt;</c> alongside
/// <see cref="MockLiveGateway"/> itself, which is what lets this probe share the rule with the five
/// test files rather than restate it as a seventh copy. See #97.
/// </para>
/// </remarks>
internal static class HostedSessionProbe
{
    private const string SessionName = "probe";
    private const int RecordCount = 8;

    /// <summary>
    /// The name <see cref="DatabentoLiveBuilder.AddHealthCheck"/> derives when a caller names none.
    /// Spelled out here rather than passed in, so the check below is about the default that ships.
    /// </summary>
    private const string HealthCheckName = "databento-live-" + SessionName;

    /// <summary>
    /// One publisher, where <see cref="JsonContextProbe"/> serves four and the real 879 KB enum
    /// capture. That probe owns what the generated readers make of a response; this one owns only
    /// whether the client the <em>container</em> built — over an
    /// <see cref="System.Net.Http.IHttpMessageHandlerFactory"/> handler with a rotating
    /// <c>PooledConnectionLifetime</c>, rather than <see cref="System.Net.Http.HttpClient"/>'s own
    /// — reaches the endpoint at all. A larger body would restate a claim already settled.
    /// </summary>
    private const string OnePublisher = """
        [ { "publisher_id": 1, "dataset": "GLBX.MDP3", "venue": "GLBX", "description": "CME Globex MDP 3.0" } ]
        """;

    public static async Task RunAsync(ProbeReport report, CancellationToken cancellationToken)
    {
        ProbeReport.Section("hosting: one generic host, and every registration the package publishes");

        var dataset = Dataset.XnasItch.ToWireString();
        await using var gateway = new MockLiveGateway(dataset);
        await using var server = new LoopbackJsonServer(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            // System.Text.Encoding by its full name: DatabentoDotNet.Dbn.Encoding is a DBN wire enum,
            // and a using for the namespace makes the two ambiguous.
            [JsonContextProbe.PublishersSlug] = System.Text.Encoding.UTF8.GetBytes(OnePublisher),
            [JsonContextProbe.ListEnumsSlug] = "{}"u8.ToArray(),
        });

        var sink = new RecordSink(RecordCount);

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Databento:ApiKey"] = MockLiveGateway.TestApiKey,
            // Through configuration rather than through the Action<HistoricalOptions> overload,
            // deliberately: HistoricalOptions is a second options type for the generated binder to
            // fill, and the lambda overload is three lines of Configure() with nothing in it for
            // ILC to get wrong.
            ["Databento:Historical:BaseUrl"] = server.BaseUrl.ToString(),
            [$"Databento:Live:{SessionName}:Dataset"] = dataset,
            [$"Databento:Live:{SessionName}:Gateway"] = gateway.Address.ToString(),
            [$"Databento:Live:{SessionName}:Subscriptions:0:Schema"] = "mbo",
            [$"Databento:Live:{SessionName}:Subscriptions:0:Symbols:0"] = MockGatewayHandshake.Symbol,
            [$"Databento:Live:{SessionName}:Reconnect:Enabled"] = "false",
        });

        builder.Services.AddDatabento();
        builder.Services.AddDatabentoHistorical();
        builder.Services.AddDatabentoReference();
        builder.Services.AddSingleton(sink);
        builder.Services.AddDatabentoLive(SessionName)
                        .AddRecordHandler<CountingRecordHandler>()
                        .AddHealthCheck();

        // After AddHealthCheck, and that order is the check. AddHealthCheck writes straight into
        // HealthCheckServiceOptions precisely so a consumer's own AddHealthChecks() composes in
        // either order; going through IHealthChecksBuilder instead would have made this line a
        // prerequisite, and this is the order that would have failed.
        builder.Services.AddHealthChecks();

        var host = builder.Build();
        await using var disposable = (IAsyncDisposable)host;

        var serving = MockGatewayHandshake.ServeAsync(gateway, cancellationToken);

        // The load-bearing sequencing point, same as LiveSessionServiceTests: by the time
        // host.StartAsync() returns, LiveSessionService.StartAsync has already connected,
        // authenticated, subscribed and started the session — under the container and the
        // generated binder, not merely under the managed test runtime.
        await host.StartAsync(cancellationToken).ConfigureAwait(false);
        await serving.ConfigureAwait(false);

        var runner = host.Services.GetRequiredKeyedService<LiveSessionRunner>(SessionName);
        report.RequireEqual(dataset, runner.Session.Dataset, "the generated binder carried the dataset into the resolved session");
        report.Require(runner.State == LiveSessionState.Running, "the session reached Running under the host");

        // AddRecordHandler<THandler>()'s own check, and it is about activation rather than about
        // dispatch: nothing in this program names CountingRecordHandler's constructor, so reaching
        // an instance of it at all means ILC kept the constructor the annotation asked for and the
        // container found it, chose it, and resolved its parameter.
        report.Require(
            host.Services.GetRequiredKeyedService<ILiveRecordHandler>(SessionName) is CountingRecordHandler,
            "AddRecordHandler<THandler>() had the container construct the handler reflectively");

        await RequireHealthAsync(report, host, HealthStatus.Healthy, "while the session is running", cancellationToken)
            .ConfigureAwait(false);
        await RequireTransportAsync(report, host, cancellationToken).ConfigureAwait(false);

        var sent = SyntheticMbo.Records(RecordCount);
        foreach (var record in sent)
        {
            await gateway.SendRecordAsync(record, cancellationToken).ConfigureAwait(false);
        }

        await sink.AllReceived.ConfigureAwait(false);
        await gateway.CloseAsync().ConfigureAwait(false);
        await host.StopAsync(cancellationToken).ConfigureAwait(false);

        report.RequireEqual(RecordCount, sink.Sequences.Count, "every record sent came back through the hosted service");
        report.Require(
            sink.Sequences.SequenceEqual(sent.Select(record => record.Sequence)),
            "the sequence numbers arrived in order and intact, through the zero-copy loop behind an ILiveRecordHandler dispatch");
        report.Require(runner.State == LiveSessionState.Stopped, "the host stopped the session cleanly");
        report.Require(runner.Fault is null, "the session carries no fault after a clean stop");

        // A stopped session is unhealthy, and the health check is asked a second time rather than
        // once because one answer proves only that something answered. Two different answers, from
        // two different session states, are what say the check is reading the runner.
        //
        // The framework logs an unhealthy result at Error level, which is why the note below is
        // printed first: a reader of a green CI log should not have to place a stray "fail:" line.
        // ProbeReport prints its own failures as "FAIL", and the verdict is the exit code either way.
        ProbeReport.Note("the next line is the host logging the unhealthy answer this probe asked for.");
        await RequireHealthAsync(report, host, HealthStatus.Unhealthy, "once the session has stopped", cancellationToken)
            .ConfigureAwait(false);

        report.Require(server.Fault is null, $"the loopback server served both requests cleanly ({server.Fault?.Message})");
        report.RequireEqual(2, server.Served.Count, "both HTTP registrations reached the loopback server");

        ProbeReport.Note($"hosting: {sink.Sequences.Count} records through a generic host, over a loopback socket.");
    }

    /// <summary>
    /// Runs the host's whole health report and requires this session's entry to carry
    /// <paramref name="expected"/>.
    /// </summary>
    /// <remarks>
    /// Through <see cref="HealthCheckService"/> rather than by newing up a
    /// <c>LiveSessionHealthCheck</c>, because constructing one directly would skip the two things
    /// only the registration can be wrong about: that the <see cref="HealthCheckRegistration"/>'s
    /// lazy factory resolves the keyed runner, and that the name a consumer's <c>/health</c>
    /// endpoint reports is the one <c>AddHealthCheck</c> derived. <c>ObservabilityTests</c> owns the
    /// state-to-status table; what is unsettled here is whether any of that survives ILC.
    /// </remarks>
    private static async Task RequireHealthAsync(
        ProbeReport report,
        IHost host,
        HealthStatus expected,
        string when,
        CancellationToken cancellationToken)
    {
        var health = await host.Services.GetRequiredService<HealthCheckService>()
                               .CheckHealthAsync(cancellationToken)
                               .ConfigureAwait(false);

        var found = health.Entries.TryGetValue(HealthCheckName, out var entry);

        report.Require(found, $"the health report carries an entry named {HealthCheckName} {when}");
        report.Require(
            found && entry.Status == expected,
            $"the session reports {expected} {when} (got {(found ? entry.Status.ToString() : "no entry at all")})");
    }

    /// <summary>
    /// The two HTTP registrations: one transport between them, and a request each over it.
    /// </summary>
    /// <remarks>
    /// The requests are the part <see cref="JsonContextProbe"/> cannot make. Its clients are
    /// constructed directly and so carry <see cref="System.Net.Http.HttpClient"/>'s own handler;
    /// these two were built by the container over a <see cref="System.Net.Http.SocketsHttpHandler"/>
    /// the package configures through <c>IHttpClientFactory</c>, which is a different object graph
    /// for ILC to keep.
    /// </remarks>
    private static async Task RequireTransportAsync(ProbeReport report, IHost host, CancellationToken cancellationToken)
    {
        var historical = host.Services.GetRequiredService<HistoricalClient>();
        var reference = host.Services.GetRequiredService<ReferenceClient>();

        // The spec's §1 promise, asserted where it is cheapest to assert: AddDatabentoReference
        // calls AddDatabentoHistorical and then TryAddSingleton, so the two registrations name one
        // transport — one HttpClient, one connection pool.
        report.Require(
            ReferenceEquals(reference.Transport, historical),
            "AddDatabentoReference reused the transport AddDatabentoHistorical registered");

        var publishers = await historical.Metadata.ListPublishersAsync(cancellationToken).ConfigureAwait(false);
        report.RequireEqual(1, publishers.Count, "the container-built HistoricalClient completed a request");

        var groups = await reference.CorporateActions.ListEnumsAsync(cancellationToken).ConfigureAwait(false);
        report.RequireEqual(0, groups.Count, "the container-built ReferenceClient completed a request over that same transport");
    }

    /// <summary>
    /// Where the records land, and the reason <see cref="CountingRecordHandler"/> has a constructor
    /// parameter at all.
    /// </summary>
    /// <remarks>
    /// <see cref="DatabentoLiveBuilder.AddRecordHandler{THandler}()"/> hands the container a type
    /// rather than an instance, so the probe cannot hold the handler it registers — it holds this
    /// instead and registers it, which turns the handler into a type the container has to select a
    /// constructor for and inject into. A parameterless handler would exercise strictly less of the
    /// annotated path and would leave the probe nothing to read the sequences out of.
    /// </remarks>
    private sealed class RecordSink(int expected)
    {
        private readonly TaskCompletionSource _allReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The sequence numbers seen, in arrival order.</summary>
        public List<uint> Sequences { get; } = [];

        /// <summary>Completes once <c>expected</c> records have arrived.</summary>
        /// <remarks>
        /// The same data-driven synchronization <c>LiveSessionServiceTests.CountingHandler</c> uses,
        /// for the same reason: the hosted service drains on a background task this program does not
        /// control the scheduling of, so a fixed sleep would be a flaky proxy for the real condition.
        /// </remarks>
        public Task AllReceived => _allReceived.Task;

        public void Add(uint sequence)
        {
            Sequences.Add(sequence);

            if (Sequences.Count == expected)
            {
                _allReceived.TrySetResult();
            }
        }
    }

    /// <summary>
    /// The registered <see cref="ILiveRecordHandler"/>: hands each record's sequence number to the
    /// <see cref="RecordSink"/> the container injects.
    /// </summary>
    private sealed class CountingRecordHandler : ILiveRecordHandler
    {
        private readonly RecordSink _sink;

        /// <summary>
        /// Public, on a private nested type, and both halves are deliberate. The container's
        /// activator considers public constructors only — which is exactly what
        /// <c>[DynamicallyAccessedMembers(PublicConstructors)]</c> asks ILC to keep — while the type
        /// staying private is what makes this a real question for the trimmer rather than one
        /// answered by the type being reachable from somewhere else.
        /// </summary>
        public CountingRecordHandler(RecordSink sink) => _sink = sink;

        public void OnRecord(scoped RecordRef record)
        {
            if (record.TryGet<MboMsg>(out var mbo))
            {
                _sink.Add(mbo.Sequence);
            }
        }

        public ValueTask OnFlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
