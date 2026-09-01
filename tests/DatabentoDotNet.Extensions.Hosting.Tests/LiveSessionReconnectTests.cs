using DatabentoDotNet.Dbn;
using DatabentoDotNet.Dbn.Publishers;
using DatabentoDotNet.Live;
using DatabentoDotNet.Live.Tests;
using NodaTime;

namespace DatabentoDotNet.Extensions.Hosting.Tests;

/// <summary>
/// Reconnection: the order it happens in, the schedule it happens on, and when it stops.
/// </summary>
/// <remarks>
/// <para>
/// <b>The failure is provoked with silence, never with a close.</b> A read timeout is a failure
/// the mock can produce by doing nothing at all — see
/// <c>LiveClientReconnectTests.ReconnectAsync_AfterAHeartbeatTimeout_IsWhatTheClientIsFor</c>,
/// whose ordering every test here follows: the gateway stays silent, the client's own
/// <see cref="HeartbeatTimeoutException"/> tears its socket down, and <em>only then</em> does
/// <see cref="MockLiveGateway.CloseAsync"/> run — to release the gateway's half so a fresh
/// handshake can be accepted, not to cause the failure. Calling <c>CloseAsync</c> first was tried
/// and is wrong: it delivers a clean end-of-stream to the client, which <c>IsTransient</c>'s own
/// remarks say is not a failure at all, so nothing here reconnects and the mock hangs waiting for
/// a second handshake that is never attempted.
/// </para>
/// <para>
/// <b>The synchronisation point is the first call to <see cref="ReconnectSupervisor.Delay"/>.</b>
/// The failure fires inside <see cref="LiveSessionRunner.RunAsync"/> on a background task, so a
/// test cannot simply await the exception the way
/// <c>LiveClientReconnectTests</c> does. But <c>TryRecoverAsync</c> calls <c>TryNextDelay</c> and
/// then <c>Delay</c> before it reconnects — so the first invocation of a test's <c>Delay</c> stub
/// is proof, not a guess, that the timeout already fired and the runner has not reconnected yet.
/// Closing (or disposing) the gateway from inside that first call, before the stub returns, is
/// what makes "provoke with silence, clean up before the reconnect" deterministic rather than a
/// race against wall-clock time.
/// </para>
/// <para>
/// <b><see cref="ReconnectSupervisor.Delay"/> is used for exactly one thing in this runner.</b>
/// <see cref="LiveSessionRunner.CloseAsync"/>'s own shutdown-courtesy timeout used to borrow the
/// same seam, which meant a test's <c>Delay</c> override could be satisfied by an unrelated final
/// close rather than by a real reconnect attempt — see the git history for the diagnostic that
/// caught it. It no longer does, which is what makes the signal in the previous paragraph safe to
/// treat as proof.
/// </para>
/// <para>
/// <b>Each handshake is served by one task, not three.</b> <see cref="MockLiveGateway.ExpectSubscribeAsync"/>
/// and <see cref="MockLiveGateway.StartAsync"/> both read from the accepted connection before their
/// first <c>await</c>, via a <c>RequireReader()</c> that throws synchronously when nothing has been
/// accepted yet. Starting them as three independent tasks before
/// <see cref="MockLiveGateway.AuthenticateAsync(string, Duration?, CancellationToken)"/> has actually
/// accepted a connection — rather than after, as <c>LiveSessionRunnerTests.ServeStartupAsync</c>
/// and <c>LiveClientReconnectTests</c> both do — faults the later two immediately, before any
/// client has connected, and does so deterministically rather than as a timing race. See
/// <see cref="ServeHandshakeAsync"/>, which sequences the three the way every other test in this
/// repository that drives more than one step of the mock already does.
/// </para>
/// </remarks>
public class LiveSessionReconnectTests
{
    private const string SecondSessionId = "6";

    private static readonly string DatasetName = Dataset.XnasItch.ToWireString();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static ResolvedLiveSession Session(MockLiveGateway gateway, ResolvedReconnect reconnect) => new()
    {
        Name = "equities",
        ApiKey = new ApiKey(MockLiveGateway.TestApiKey),
        Dataset = gateway.Dataset,
        Gateway = gateway.Address,
        // Short enough that a silent gateway is a failure inside a test's patience.
        ReadTimeout = Duration.FromMilliseconds(250),
        Subscriptions =
        [
            new Subscription
            {
                Schema = Schema.Mbo,
                Symbols = Symbols.From(["AAPL"]),
                Start = Instant.FromUtc(2026, 8, 31, 13, 30),
            },
        ],
        Reconnect = reconnect,
    };

    /// <summary>
    /// Runs the gateway's side of one handshake — authenticate, then subscribe, then start, each
    /// awaited before the next begins.
    /// </summary>
    /// <remarks>
    /// The two things that differ between the first handshake and the one after a reconnect are
    /// parameters rather than copies of this method: the session id a real gateway would issue
    /// fresh per session, and the subscription's replay <c>start</c> — present on the first
    /// handshake, <see langword="null"/> on every one after, because <c>ResubscribeAsync</c>
    /// drops it. See the type-level remarks for why the three steps are sequenced in one task
    /// rather than started as three.
    /// </remarks>
    /// <param name="gateway">The gateway to serve.</param>
    /// <param name="sessionId">The <c>session_id</c> this handshake reports.</param>
    /// <param name="start">
    /// The replay start this handshake's subscription must carry, or <see langword="null"/> for a
    /// resubscribe, which carries none.
    /// </param>
    private static async Task ServeHandshakeAsync(MockLiveGateway gateway, string sessionId, Instant? start)
    {
        await gateway.AuthenticateAsync(sessionId, cancellationToken: Cancel);
        await gateway.ExpectSubscribeAsync(
            new ExpectedSubscription
            {
                Schema = Schema.Mbo,
                StypeIn = SType.RawSymbol,
                Symbols = ["AAPL"],
                Start = start,
            },
            isLast: true,
            Cancel);
        await gateway.StartAsync(Cancel);
    }

    [Fact]
    public async Task RunAsync_AfterATransientFailure_ReconnectsResubscribesAndRestarts()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        var handler = new RecordingHandler();
        var delays = new List<Duration>();

        // Set once, from inside the first Delay call — see the type-level remarks. By the time
        // Delay runs, the client's own read budget has already elapsed and torn its socket down;
        // only then is it safe to release the gateway's half without that release itself looking
        // like the failure.
        Task? reserving = null;
        var ready = new TaskCompletionSource();

        var supervisor = new ReconnectSupervisor(ResolvedReconnect.Default)
        {
            Jitter = () => 1.0,
            Delay = async (delay, _) =>
            {
                delays.Add(delay);
                await gateway.CloseAsync();
                reserving = ServeHandshakeAsync(gateway, SecondSessionId, start: null);
                ready.SetResult();
            },
        };

        await using var runner = new LiveSessionRunner(
            Session(gateway, ResolvedReconnect.Default), handler, supervisor);

        // First session, with a replay start.
        var serving = ServeHandshakeAsync(
            gateway, MockLiveGateway.SessionId, Instant.FromUtc(2026, 8, 31, 13, 30));

        await runner.StartSessionAsync(Cancel);
        await serving;

        var running = runner.RunAsync(Cancel);

        // No explicit close here: the gateway simply says nothing after the first StartAsync, so
        // the client's 250 ms read budget expires on its own and the runner enters the backoff.
        // The Delay stub above is what releases the gateway's half and serves the second
        // handshake — the replayed subscription carries no Start, which is the whole point of
        // ResubscribeAsync and the reason the order is reconnect, resubscribe, start: a reconnect
        // that replayed the original subscription verbatim would ask for the same intraday
        // history twice.
        await ready.Task;
        await reserving!;

        await gateway.SendRecordAsync(SyntheticMbo.Record(7), Cancel);
        await gateway.CloseAsync();
        await running;

        Assert.Equal([7u], handler.Sequences);
        Assert.Equal(LiveSessionState.Stopped, runner.State);
        Assert.Equal([Duration.FromSeconds(1)], delays);
        // The counter reset when the session restarted.
        Assert.Equal(0, supervisor.ConsecutiveFailures);
    }

    [Fact]
    public async Task RunAsync_WhenEveryAttemptFails_GivesUpAfterMaxAttemptsAndRethrows()
    {
        var policy = ResolvedReconnect.Default with { MaxAttempts = 3 };
        var delays = new List<Duration>();

        await using var gateway = new MockLiveGateway(DatasetName);
        var disposed = false;

        var supervisor = new ReconnectSupervisor(policy)
        {
            Jitter = () => 1.0,
            Delay = async (delay, _) =>
            {
                delays.Add(delay);

                if (!disposed)
                {
                    disposed = true;

                    // Only now — after the first timeout has already fired — is it safe to take
                    // the whole gateway down. Disposing it before the timeout fires would be read
                    // as a clean end instead, which is the mistake this file exists not to make;
                    // disposing it once is enough, because a dead listener stays dead for every
                    // attempt after this one.
                    await gateway.DisposeAsync();
                }
            },
        };

        await using var runner = new LiveSessionRunner(
            Session(gateway, policy), new RecordingHandler(), supervisor);

        var serving = ServeHandshakeAsync(
            gateway, MockLiveGateway.SessionId, Instant.FromUtc(2026, 8, 31, 13, 30));

        await runner.StartSessionAsync(Cancel);
        await serving;

        var running = runner.RunAsync(Cancel);

        await Assert.ThrowsAnyAsync<Exception>(() => running);

        Assert.Equal(LiveSessionState.Faulted, runner.State);
        Assert.NotNull(runner.Fault);
        Assert.Equal(3, delays.Count);
        Assert.Equal(
            [Duration.FromSeconds(1), Duration.FromSeconds(2), Duration.FromSeconds(4)],
            delays);
    }

    [Fact]
    public async Task RunAsync_WithReconnectionDisabled_PropagatesTheFailureImmediately()
    {
        var policy = ResolvedReconnect.Default with { Enabled = false };

        await using var gateway = new MockLiveGateway(DatasetName);
        await using var runner = new LiveSessionRunner(
            Session(gateway, policy), new RecordingHandler(), new ReconnectSupervisor(policy));

        var serving = ServeHandshakeAsync(
            gateway, MockLiveGateway.SessionId, Instant.FromUtc(2026, 8, 31, 13, 30));

        await runner.StartSessionAsync(Cancel);
        await serving;

        await Assert.ThrowsAsync<HeartbeatTimeoutException>(() => runner.RunAsync(Cancel));
        Assert.Equal(LiveSessionState.Faulted, runner.State);
    }

    [Fact]
    public async Task RunAsync_WhenTheGatewayClosesCleanly_DoesNotReconnect()
    {
        // A clean close is how a session ends — a completed replay, or a gateway shutting down
        // deliberately. Treating it as a failure would turn every orderly end into a reconnect
        // storm, and every reconnect is a newly billed session. Unlike every other test in this
        // file, the close here runs *before* RunAsync is ever called — there is no failure to
        // provoke with silence first, because this test's whole point is that closing before any
        // read is even pending is the one case that is genuinely a clean end.
        var delays = new List<Duration>();

        await using var gateway = new MockLiveGateway(DatasetName);
        var supervisor = new ReconnectSupervisor(ResolvedReconnect.Default)
        {
            Delay = (delay, _) => { delays.Add(delay); return Task.CompletedTask; },
        };

        await using var runner = new LiveSessionRunner(
            Session(gateway, ResolvedReconnect.Default), new RecordingHandler(), supervisor);

        var serving = ServeHandshakeAsync(
            gateway, MockLiveGateway.SessionId, Instant.FromUtc(2026, 8, 31, 13, 30));

        await runner.StartSessionAsync(Cancel);
        await serving;
        await gateway.CloseAsync();

        await runner.RunAsync(Cancel);

        Assert.Equal(LiveSessionState.Stopped, runner.State);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task RunAsync_DuringTheBackoff_StopsWhenCancelled()
    {
        var started = new TaskCompletionSource();

        await using var gateway = new MockLiveGateway(DatasetName);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Cancel);

        var supervisor = new ReconnectSupervisor(ResolvedReconnect.Default)
        {
            Delay = async (delay, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            },
        };

        await using var runner = new LiveSessionRunner(
            Session(gateway, ResolvedReconnect.Default), new RecordingHandler(), supervisor);

        var serving = ServeHandshakeAsync(
            gateway, MockLiveGateway.SessionId, Instant.FromUtc(2026, 8, 31, 13, 30));

        await runner.StartSessionAsync(Cancel);
        await serving;

        // No explicit close: the gateway simply goes silent and the client's own 250 ms read
        // budget elapses on its own, entering RunAsync's catch. started completing is now proof
        // that happened, because CloseAsync no longer shares this seam (see the type-level
        // remarks) — nothing else in this runner can ever complete it.
        var running = runner.RunAsync(stopping.Token);
        await started.Task;

        await stopping.CancelAsync();
        await running;

        // Cancelled during a backoff is a shutdown, not a fault: the host is stopping.
        Assert.Equal(LiveSessionState.Stopped, runner.State);
        Assert.Null(runner.Fault);
    }
}
