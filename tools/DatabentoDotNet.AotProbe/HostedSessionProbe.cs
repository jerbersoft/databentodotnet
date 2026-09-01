using DatabentoDotNet.Dbn;
using DatabentoDotNet.Dbn.Publishers;
using DatabentoDotNet.Extensions.Hosting;
using DatabentoDotNet.Live;
using DatabentoDotNet.Live.Tests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DatabentoDotNet.AotProbe;

/// <summary>
/// A generic host — <see cref="HostApplicationBuilder"/>, a container built from
/// <c>DatabentoDotNet.Extensions.Hosting</c>'s keyed registrations, the configuration binding
/// source generator, and a <see cref="BackgroundService"/> — driving a whole live session against
/// <see cref="MockLiveGateway"/>, inside the native binary.
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
/// <b>The gateway sequencing is <c>LiveSessionServiceTests.ServeStartupAsync</c>'s shape, not
/// <see cref="LiveSessionProbe"/>'s.</b> <c>LiveSessionService.StartAsync</c> connects,
/// authenticates, subscribes and starts the session <em>before</em> <c>host.StartAsync()</c>
/// returns, so the gateway's three replies have to already be running in a background task before
/// this method calls it — the same ordering every test in that file uses, for the same reason: get
/// it backwards and both sides wait on each other rather than one of them failing loudly.
/// </para>
/// </remarks>
internal static class HostedSessionProbe
{
    private const string SessionName = "probe";
    private const int RecordCount = 8;

    private static readonly string[] ProbeSymbols = ["AAPL"];

    public static async Task RunAsync(ProbeReport report, CancellationToken cancellationToken)
    {
        ProbeReport.Section("hosting: a generic host driving a session over the mock gateway");

        var dataset = Dataset.XnasItch.ToWireString();
        await using var gateway = new MockLiveGateway(dataset);
        var handler = new CountingRecordHandler(RecordCount);

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Databento:ApiKey"] = MockLiveGateway.TestApiKey,
            [$"Databento:Live:{SessionName}:Dataset"] = dataset,
            [$"Databento:Live:{SessionName}:Gateway"] = gateway.Address.ToString(),
            [$"Databento:Live:{SessionName}:Subscriptions:0:Schema"] = "mbo",
            [$"Databento:Live:{SessionName}:Subscriptions:0:Symbols:0"] = ProbeSymbols[0],
            [$"Databento:Live:{SessionName}:Reconnect:Enabled"] = "false",
        });

        builder.Services.AddDatabento();
        builder.Services.AddDatabentoLive(SessionName).AddRecordHandler(_ => handler);

        var host = builder.Build();
        await using var disposable = (IAsyncDisposable)host;

        var serving = ServeStartupAsync(gateway, cancellationToken);

        // The load-bearing sequencing point, same as LiveSessionServiceTests: by the time
        // host.StartAsync() returns, LiveSessionService.StartAsync has already connected,
        // authenticated, subscribed and started the session — under the container and the
        // generated binder, not merely under the managed test runtime.
        await host.StartAsync(cancellationToken).ConfigureAwait(false);
        await serving.ConfigureAwait(false);

        var runner = host.Services.GetRequiredKeyedService<LiveSessionRunner>(SessionName);
        report.RequireEqual(dataset, runner.Session.Dataset, "the generated binder carried the dataset into the resolved session");
        report.Require(runner.State == LiveSessionState.Running, "the session reached Running under the host");

        var sent = SyntheticMbo.Records(RecordCount);
        foreach (var record in sent)
        {
            await gateway.SendRecordAsync(record, cancellationToken).ConfigureAwait(false);
        }

        await handler.AllReceived.ConfigureAwait(false);
        await gateway.CloseAsync().ConfigureAwait(false);
        await host.StopAsync(cancellationToken).ConfigureAwait(false);

        report.RequireEqual(RecordCount, handler.Sequences.Count, "every record sent came back through the hosted service");
        report.Require(
            handler.Sequences.SequenceEqual(sent.Select(record => record.Sequence)),
            "the sequence numbers arrived in order and intact, through the zero-copy loop behind an ILiveRecordHandler dispatch");
        report.Require(runner.State == LiveSessionState.Stopped, "the host stopped the session cleanly");
        report.Require(runner.Fault is null, "the session carries no fault after a clean stop");

        ProbeReport.Note($"hosting: {handler.Sequences.Count} records through a generic host, over a loopback socket.");
    }

    /// <summary>
    /// Runs the gateway's side of connect, authenticate, subscribe and start — ported verbatim
    /// from <c>LiveSessionServiceTests.ServeStartupAsync</c>: the three steps are awaited in order
    /// inside one task, started before the client side runs.
    /// </summary>
    private static async Task ServeStartupAsync(MockLiveGateway gateway, CancellationToken cancellationToken)
    {
        await gateway.AuthenticateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await gateway.ExpectSubscribeAsync(
            new ExpectedSubscription { Schema = Schema.Mbo, StypeIn = SType.RawSymbol, Symbols = ProbeSymbols },
            isLast: true,
            cancellationToken).ConfigureAwait(false);
        await gateway.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The registered <see cref="ILiveRecordHandler"/>: records sequence numbers and completes
    /// <see cref="AllReceived"/> once <paramref name="expected"/> have arrived.
    /// </summary>
    /// <remarks>
    /// The same data-driven synchronization <c>LiveSessionServiceTests.CountingHandler</c> uses,
    /// for the same reason: the hosted service drains on a background task this method does not
    /// control the scheduling of, so a fixed sleep would be a flaky proxy for the real condition.
    /// </remarks>
    private sealed class CountingRecordHandler(int expected) : ILiveRecordHandler
    {
        private readonly TaskCompletionSource _allReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<uint> Sequences { get; } = [];

        public Task AllReceived => _allReceived.Task;

        public void OnRecord(scoped RecordRef record)
        {
            if (record.TryGet<MboMsg>(out var mbo))
            {
                Sequences.Add(mbo.Sequence);
            }

            if (Sequences.Count == expected)
            {
                _allReceived.TrySetResult();
            }
        }

        public ValueTask OnFlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
