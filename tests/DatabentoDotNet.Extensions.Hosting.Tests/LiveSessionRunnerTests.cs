using System.Collections.Immutable;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Dbn.Publishers;
using DatabentoDotNet.Live;
using DatabentoDotNet.Live.Tests;
using NodaTime;

namespace DatabentoDotNet.Extensions.Hosting.Tests;

/// <summary>
/// The session loop, driven directly by <see cref="MockLiveGateway"/> with no host and no
/// container.
/// </summary>
/// <remarks>
/// <para>
/// <b>The mock cannot confirm what it shares an author with</b>, and that limit is unchanged here
/// — but it also does not grow. This package adds no new reading of <c>live/protocol.rs</c>; it
/// composes calls whose protocol correctness <see cref="MockLiveGateway"/> and
/// <c>RealGatewaySessionTests</c> already established between them. Nothing in this file needs a
/// real gateway, and adding one would spend money to learn nothing new.
/// </para>
/// </remarks>
public class LiveSessionRunnerTests
{
    private static readonly string DatasetName = Dataset.XnasItch.ToWireString();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static ResolvedLiveSession Session(MockLiveGateway gateway) => new()
    {
        Name = "equities",
        ApiKey = new ApiKey(MockLiveGateway.TestApiKey),
        Dataset = gateway.Dataset,
        Gateway = gateway.Address,
        Subscriptions = [new Subscription { Schema = Schema.Mbo, Symbols = Symbols.From(["AAPL"]) }],
        // Off, so this file's failures are the loop's rather than the backoff's.
        // LiveSessionReconnectTests turns it on.
        Reconnect = ResolvedReconnect.Default with { Enabled = false },
    };

    private static LiveSessionRunner Runner(MockLiveGateway gateway, ILiveRecordHandler handler) =>
        new(Session(gateway), handler, new ReconnectSupervisor(ResolvedReconnect.Default with { Enabled = false }));

    /// <summary>Runs the gateway's side of connect, authenticate, subscribe and start.</summary>
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
    /// <paramref name="response"/> instead of a success line — the mock's own established pattern
    /// for a rejected key, ported from <c>LiveClientAuthenticationTests.RejectAsync</c>.
    /// </summary>
    private static async Task RejectAsync(MockLiveGateway gateway, string response)
    {
        await gateway.ExpectAuthenticationAsync(cancellationToken: Cancel);
        await gateway.SendAsync(response, Cancel);
    }

    [Fact]
    public async Task StartSessionAsync_CompletesTheWholeHandshake()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        var handler = new RecordingHandler();
        await using var runner = Runner(gateway, handler);

        var serving = ServeStartupAsync(gateway);
        await runner.StartSessionAsync(Cancel);
        await serving;

        Assert.Equal(LiveSessionState.Running, runner.State);
        Assert.Null(runner.Fault);
        Assert.Equal(DatasetName, runner.Metadata!.Dataset);
    }

    [Fact]
    public async Task RunAsync_DrainsEveryRecordInOrder_AndFlushesAtEveryBufferBoundary()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        var handler = new RecordingHandler();
        await using var runner = Runner(gateway, handler);

        var serving = ServeStartupAsync(gateway);
        await runner.StartSessionAsync(Cancel);
        await serving;

        for (var sequence = 1u; sequence <= 3u; sequence++)
        {
            await gateway.SendRecordAsync(SyntheticMbo.Record(sequence), Cancel);
        }

        await gateway.CloseAsync();
        await runner.RunAsync(Cancel);

        Assert.Equal([1u, 2u, 3u], handler.Sequences);

        // The loop drains before it fills, so the first flush precedes every record: on the first
        // pass there is nothing buffered yet. That ordering is load-bearing rather than incidental
        // — a fill may shift the buffer, which is what invalidates a RecordRef, so the inner drain
        // must run to completion before each refill.
        Assert.Equal("flush", handler.Events[0]);

        // Not "one flush, then all three records": how many fills it takes to receive three small
        // records is a property of the socket, not of the runner — MockLiveGateway sends each one
        // as two separate flushed writes, and whether the OS delivers them as one buffer or several
        // is a real timing race, observed to go either way under load. What the loop actually
        // guarantees on every framing is that every record is drained, in order, exactly once, and
        // that nothing is left undrained at the tail — so that is what is asserted, rather than the
        // one framing a quiet loopback socket usually happens to produce.
        Assert.Equal(
            ["record:1", "record:2", "record:3"],
            handler.Events.Where(e => e.StartsWith("record:", StringComparison.Ordinal)));
        Assert.Equal("flush", handler.Events[^1]);

        Assert.Equal(3, handler.Sequences.Count);
        Assert.Equal(3, runner.RecordsReceived);
    }

    [Fact]
    public async Task RunAsync_WhenTheGatewayClosesCleanly_Stops()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        var handler = new RecordingHandler();
        await using var runner = Runner(gateway, handler);

        var serving = ServeStartupAsync(gateway);
        await runner.StartSessionAsync(Cancel);
        await serving;
        await gateway.CloseAsync();

        await runner.RunAsync(Cancel);

        Assert.Equal(LiveSessionState.Stopped, runner.State);
        Assert.Null(runner.Fault);
    }

    [Fact]
    public async Task RunAsync_WhenCancelled_StopsWithoutFaulting()
    {
        // Shutdown is not a fault. A host stopping must not log a session as having failed.
        await using var gateway = new MockLiveGateway(DatasetName);
        var handler = new RecordingHandler();
        await using var runner = Runner(gateway, handler);

        var serving = ServeStartupAsync(gateway);
        await runner.StartSessionAsync(Cancel);
        await serving;

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var running = runner.RunAsync(stopping.Token);
        await gateway.SendRecordAsync(SyntheticMbo.Record(1), Cancel);

        await stopping.CancelAsync();
        await running;

        Assert.Equal(LiveSessionState.Stopped, runner.State);
        Assert.Null(runner.Fault);
    }

    [Fact]
    public async Task RunAsync_GivenAnAlreadyCancelledToken_StopsWithoutEverPumping()
    {
        // Pins the exact path LiveSessionService.StopAsync falls back to (see its remarks on
        // BackgroundService.StartAsync scheduling ExecuteAsync via a Task.Run(Func<Task>,
        // CancellationToken) whose cancellation can win before the delegate is ever dispatched,
        // #95): a session that started successfully but whose loop never gets to run even once
        // still has to leave State somewhere other than Running, with no fault, and without
        // touching a client that has never been drained.
        await using var gateway = new MockLiveGateway(DatasetName);
        var handler = new RecordingHandler();
        await using var runner = Runner(gateway, handler);

        var serving = ServeStartupAsync(gateway);
        await runner.StartSessionAsync(Cancel);
        await serving;

        await runner.RunAsync(new CancellationToken(canceled: true));

        Assert.Equal(LiveSessionState.Stopped, runner.State);
        Assert.Null(runner.Fault);
        Assert.Empty(handler.Events);
    }

    [Fact]
    public async Task RunAsync_WhenTheHandlerThrows_IsFatalToTheSession()
    {
        // Swallowing it loses market data invisibly, which is the failure class this codebase
        // exists to convert into loud ones. A handler that wants to continue catches its own.
        await using var gateway = new MockLiveGateway(DatasetName);
        var handler = new RecordingHandler { ThrowOnRecord = new InvalidOperationException("boom") };
        await using var runner = Runner(gateway, handler);

        var serving = ServeStartupAsync(gateway);
        await runner.StartSessionAsync(Cancel);
        await serving;
        await gateway.SendRecordAsync(SyntheticMbo.Record(1), Cancel);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Cancel));

        Assert.Equal("boom", thrown.Message);
        Assert.Equal(LiveSessionState.Faulted, runner.State);
        Assert.Same(thrown, runner.Fault);
    }

    [Fact]
    public async Task RunAsync_WhenTheFlushThrows_IsAlsoFatal()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        var handler = new RecordingHandler { ThrowOnFlush = new InvalidOperationException("flush failed") };
        await using var runner = Runner(gateway, handler);

        var serving = ServeStartupAsync(gateway);
        await runner.StartSessionAsync(Cancel);
        await serving;

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Cancel));
        Assert.Equal(LiveSessionState.Faulted, runner.State);
    }

    [Fact]
    public async Task StartSessionAsync_WithAKeyTheGatewayRejects_Faults()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        var handler = new RecordingHandler();
        await using var runner = Runner(gateway, handler);

        // A wrong ExpectedApiKey would make the mock's own CRAM check throw before it ever answers
        // the client, which is a fixture bug (AuthTimeoutException, not a rejection) rather than the
        // gateway rejecting the credentials — see LiveClientAuthenticationTests.RejectAsync, the
        // established pattern for this exact scenario. Accept the handshake normally and answer it
        // with success=0 instead: that is what a real gateway does when it declines a key.
        var serving = RejectAsync(gateway, "success=0|error=invalid API key");

        await Assert.ThrowsAsync<DatabentoAuthenticationException>(() => runner.StartSessionAsync(Cancel));

        Assert.Equal(LiveSessionState.Faulted, runner.State);
        Assert.IsType<DatabentoAuthenticationException>(runner.Fault);
        await serving;
    }

    [Fact]
    public async Task StartSessionAsync_WhenTheClientCannotBeBuilt_Faults()
    {
        // LiveClient checks HeartbeatInterval's 5-1800 second range in its own init accessor — one
        // of the three documented constraints LiveSessionResolver deliberately does not
        // re-implement, see its remarks — so building the client is a step that throws, and a
        // configuration carrying it passes ValidateOnStart and reaches here.
        //
        // BuildClient() used to sit above the try, so that throw left State at Starting and Fault
        // at null: an invariant this public type states on Fault and did not hold, with
        // LiveSessionHealthCheck going on reporting Degraded — "coming up, not yet serving" — for a
        // session that was never going to start.
        await using var gateway = new MockLiveGateway(DatasetName);
        var session = Session(gateway) with { HeartbeatInterval = Duration.FromSeconds(1) };
        await using var runner = new LiveSessionRunner(
            session,
            new RecordingHandler(),
            new ReconnectSupervisor(ResolvedReconnect.Default with { Enabled = false }));

        var thrown = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => runner.StartSessionAsync(Cancel));

        Assert.Equal(LiveSessionState.Faulted, runner.State);
        Assert.Same(thrown, runner.Fault);
    }

    [Fact]
    public async Task RunAsync_BeforeStartSessionAsync_Throws()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var runner = Runner(gateway, new RecordingHandler());

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Cancel));
        Assert.Equal(LiveSessionState.NotStarted, runner.State);
    }

    [Fact]
    public async Task StartSessionAsync_Twice_Throws()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var runner = Runner(gateway, new RecordingHandler());

        var serving = ServeStartupAsync(gateway);
        await runner.StartSessionAsync(Cancel);
        await serving;

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.StartSessionAsync(Cancel));
    }
}
