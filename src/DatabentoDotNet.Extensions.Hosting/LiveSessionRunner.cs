using System.Diagnostics;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Extensions.Hosting.Internal;
using DatabentoDotNet.Live;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;

namespace DatabentoDotNet.Extensions.Hosting;

/// <summary>
/// Connects, authenticates, subscribes and starts one live session, then drains its records into
/// an <see cref="ILiveRecordHandler"/> until the stream ends or the caller cancels.
/// </summary>
/// <remarks>
/// <para>
/// <b>Needs no host and no container.</b> The constructor takes a <see cref="ResolvedLiveSession"/>,
/// a handler and a <see cref="ReconnectSupervisor"/> — nothing that only exists inside
/// <see cref="Microsoft.Extensions.DependencyInjection"/> or
/// <see cref="Microsoft.Extensions.Hosting"/>. That is what lets <c>MockLiveGateway</c> drive every
/// behaviour here on an ordinary <c>dotnet test</c>, with no <c>IServiceProvider</c> and no
/// <c>IHostedService</c> anywhere near it. The hosted service that wraps this in a
/// <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> is thin enough to be uninteresting
/// precisely because everything interesting already works without it.
/// </para>
/// <para>
/// <b>Starting is a separate call from running, and that split is deliberate.</b>
/// <see cref="Microsoft.Extensions.Hosting.BackgroundService.StartAsync"/> only awaits
/// <c>ExecuteAsync</c> until its first <c>await</c> that does not complete synchronously, so a
/// session that connected and authenticated inside the record loop would fail in the background —
/// with the host already reporting itself started. <see cref="StartSessionAsync"/> is the part a
/// hosted service's own <c>StartAsync</c> override awaits, so a rejected API key fails the boot
/// instead of vanishing into a background task nobody is watching.
/// </para>
/// <para>
/// <b>The drain is synchronous and the fill is the only <c>await</c>, because there is no other
/// shape available.</b> An <c>async</c> method cannot return a <c>ref struct</c> — there is no
/// <c>Task&lt;RecordRef&gt;</c>, and there never can be — so upstream's single-call
/// <c>next_record()</c> does not port. What does port is its <c>fill_buf()</c> /
/// <c>try_next_record()</c> pair: <see cref="PumpAsync"/> awaits
/// <see cref="LiveClient.FillBufferAsync"/> once per pass and hands every record the fill produced
/// to <see cref="Drain"/>, a plain synchronous method, before awaiting anything else. A
/// <see cref="RecordRef"/> <em>local</em> inside an <c>async</c> method is fine; only one that
/// survives an <c>await</c> is rejected, by the compiler, as CS4007 — which is the same lifetime
/// rule <see cref="LiveClient.TryNextRecord"/> already imposes.
/// </para>
/// <para>
/// <b>No <c>System.IO.Pipelines</c> and no <c>Channel&lt;T&gt;</c> between the socket and the
/// handler.</b> Both were considered and rejected: a <see cref="System.Buffers.ReadOnlySequence{T}"/>
/// may be non-contiguous, which breaks the reinterpret cast every record decode depends on, and
/// either one adds a second buffering layer over the decoder's own. A channel would also force a
/// copy per record to cross it — the one thing this library exists not to do.
/// </para>
/// <para>
/// <b>An exception from the handler, or from the client, ends the session.</b> Swallowing one would
/// lose market data invisibly, which is the failure class this codebase exists to convert into loud
/// ones. Cancelling <see cref="RunAsync"/>'s token is the one way to stop without that being a
/// fault: it moves <see cref="State"/> to <see cref="LiveSessionState.Stopped"/> rather than
/// <see cref="LiveSessionState.Faulted"/>, because a host stopping a background service must not be
/// reported as a failed session.
/// </para>
/// </remarks>
public sealed class LiveSessionRunner : IAsyncDisposable
{
    /// <summary>The tag key every measurement this runner publishes carries.</summary>
    private const string SessionTagName = "databento.session";

    /// <summary>
    /// Converts a <c>Stopwatch.GetTimestamp()</c> difference to milliseconds without ever naming a
    /// banned date/time type: two <see cref="long"/>s and a <see cref="double"/>, and no
    /// <c>Stopwatch.GetElapsedTime</c>, whose return type is <c>TimeSpan</c> even when a
    /// <see langword="var"/> hides it.
    /// </summary>
    private static readonly double MillisecondsPerTimestampTick = 1000.0 / Stopwatch.Frequency;

    private readonly ILiveRecordHandler _handler;
    private readonly ReconnectSupervisor _supervisor;
    private readonly ILogger<LiveSessionRunner> _logger;
    private readonly LiveSessionMetrics? _metrics;

    /// <summary>
    /// The session tag, built once here and passed to every publish by readonly reference.
    /// </summary>
    /// <remarks>
    /// <b>A field rather than an expression at each call site, and that is the difference between
    /// free and not.</b> Constructing the pair per publish allocates nothing on its own, but the
    /// overloads that would accept it built inline — <c>TagList</c>, the <c>params</c> array — do,
    /// and a per-flush allocation still fails <c>ExtensionsAllocationTests</c>, which measures the
    /// whole loop rather than only the drain. Built once, passed by <see langword="in"/>, and
    /// handed to the single-tag overload, it costs a 16-byte stack copy per call. A
    /// <see cref="string"/> in the pair's <see cref="object"/> value does not box.
    /// </remarks>
    private readonly KeyValuePair<string, object?> _sessionTag;

    private LiveClient? _client;

    /// <summary>Creates a runner for one resolved session.</summary>
    /// <param name="session">The session to run.</param>
    /// <param name="handler">Where every record and every flush goes.</param>
    /// <param name="supervisor">
    /// The reconnection policy — unused by <see cref="StartSessionAsync"/> and <see cref="RunAsync"/>
    /// beyond <see cref="ReconnectSupervisor.RecordSuccess"/> until reconnection lands.
    /// </param>
    /// <param name="logger">
    /// Where <c>ExtensionsLog</c> writes. Defaults to <see cref="NullLogger{T}.Instance"/>, so a
    /// caller who never configures logging pays nothing for it.
    /// </param>
    /// <param name="metrics">
    /// Where the four instruments are published, or <see langword="null"/> to publish none. Last
    /// and optional for the same reason <paramref name="logger"/> is: a caller who wants neither
    /// writes neither, and every existing three- and four-argument call site still compiles.
    /// </param>
    public LiveSessionRunner(
        ResolvedLiveSession session,
        ILiveRecordHandler handler,
        ReconnectSupervisor supervisor,
        ILogger<LiveSessionRunner>? logger = null,
        LiveSessionMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(supervisor);

        Session = session;
        _handler = handler;
        _supervisor = supervisor;
        _logger = logger ?? NullLogger<LiveSessionRunner>.Instance;
        _metrics = metrics;
        _sessionTag = new KeyValuePair<string, object?>(SessionTagName, session.Name);
    }

    /// <summary>The session this runner is running.</summary>
    public ResolvedLiveSession Session { get; }

    /// <summary>Where this session is in its lifecycle.</summary>
    public LiveSessionState State { get; private set; }

    /// <summary>Why the session faulted, or <see langword="null"/> while it has not.</summary>
    public Exception? Fault { get; private set; }

    /// <summary>
    /// The DBN metadata the gateway sent when the session started, or <see langword="null"/>
    /// before <see cref="StartSessionAsync"/> has completed.
    /// </summary>
    public Metadata? Metadata { get; private set; }

    /// <summary>How many records this session has handed to the handler so far.</summary>
    public long RecordsReceived { get; private set; }

    /// <summary>
    /// How long <see cref="RunAsync"/> waits for a courteous close before dropping the socket
    /// instead. Defaults to five seconds.
    /// </summary>
    public Duration CloseTimeout { get; init; } = Duration.FromSeconds(5);

    /// <summary>
    /// Connects, authenticates, sends every subscription in <see cref="Session"/> in order, and
    /// starts the session.
    /// </summary>
    /// <remarks>
    /// <b>Separate from <see cref="RunAsync"/> on purpose.</b> See the type-level remarks: this is
    /// the half a hosted service's own start-up awaits, so a rejected key or an unreachable gateway
    /// fails the host's boot rather than a background loop nobody is watching yet.
    /// </remarks>
    /// <param name="cancellationToken">
    /// Cancels the handshake. Cancelling any step here leaves the underlying connection unusable —
    /// see <see cref="LiveClient.AuthenticateAsync"/> — so there is nothing to resume; construct
    /// another runner to try again.
    /// </param>
    /// <exception cref="InvalidOperationException">This runner has already been started.</exception>
    public async Task StartSessionAsync(CancellationToken cancellationToken)
    {
        if (State != LiveSessionState.NotStarted)
        {
            throw new InvalidOperationException(
                $"This session is {State}; {nameof(StartSessionAsync)} runs once per "
                + $"{nameof(LiveSessionRunner)}. Construct another to start a new session.");
        }

        State = LiveSessionState.Starting;
        var client = BuildClient();

        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await client.AuthenticateAsync(cancellationToken).ConfigureAwait(false);

            foreach (var subscription in Session.Subscriptions)
            {
                await client.SubscribeAsync(subscription, cancellationToken).ConfigureAwait(false);
            }

            Metadata = await client.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Fault = exception;
            State = LiveSessionState.Faulted;
            throw;
        }

        // Only assigned on success, so a failed start leaves nothing for RunAsync to be guarded
        // against reaching — the client's own failure handling has already torn its socket down.
        _client = client;
        _supervisor.RecordSuccess();
        State = LiveSessionState.Running;

        ExtensionsLog.SessionStarted(_logger, Session.Name, Session.Dataset, Session.Subscriptions.Length);
        _metrics?.SessionStarted(in _sessionTag);
    }

    /// <summary>
    /// Drains records into the handler until the gateway closes the stream or
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="cancellationToken">
    /// Stops the loop. Cancelling it ends the session — see <see cref="LiveClient.FillBufferAsync"/>
    /// — but is reported as <see cref="LiveSessionState.Stopped"/>, not
    /// <see cref="LiveSessionState.Faulted"/>: a host stopping is not a failure.
    /// </param>
    /// <exception cref="InvalidOperationException"><see cref="StartSessionAsync"/> has not completed.</exception>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_client is not { } client)
        {
            throw new InvalidOperationException(
                $"This session has not started. Call {nameof(StartSessionAsync)} before running it.");
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (await PumpAsync(client, cancellationToken).ConfigureAwait(false))
                    {
                        // A clean close, which is how a session ends. Not a failure, and not
                        // something to reconnect from — see IsTransient's remarks.
                        break;
                    }
                }
                catch (Exception exception)
                    when (IsTransient(exception) && !cancellationToken.IsCancellationRequested)
                {
                    if (!await TryRecoverAsync(client, exception, cancellationToken).ConfigureAwait(false))
                    {
                        throw;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping, not a fault — fall through to the shared close below.
        }
        catch (Exception exception)
        {
            Fault = exception;
            State = LiveSessionState.Faulted;
            throw;
        }

        await CloseAsync(client).ConfigureAwait(false);
        State = LiveSessionState.Stopped;

        ExtensionsLog.SessionEnded(_logger, Session.Name, RecordsReceived);
    }

    /// <summary>
    /// Runs the backoff until a session restarts or the policy is exhausted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Reconnect, resubscribe, then start, and that order is not interchangeable.</b>
    /// <c>ResubscribeAsync</c> clears each subscription's <c>Start</c>, so a reconnect does not ask
    /// the gateway for the same intraday history a second time — and the symptom of getting it
    /// wrong, duplicated records after a reconnect, looks like a gateway fault and is not one.
    /// PORTING.md §4.
    /// </para>
    /// <para>
    /// <b>Every successful restart is a newly billed session</b>, which is why
    /// <see cref="ReconnectSupervisor"/> bounds the attempts and why the success is logged at
    /// information level rather than debug.
    /// </para>
    /// </remarks>
    /// <returns><see langword="true"/> when a session is running again.</returns>
    private async Task<bool> TryRecoverAsync(LiveClient client, Exception cause, CancellationToken cancellationToken)
    {
        State = LiveSessionState.Reconnecting;

        while (_supervisor.TryNextDelay(out var delay))
        {
            ExtensionsLog.ReconnectAttempted(
                _logger, Session.Name, _supervisor.ConsecutiveFailures,
                _supervisor.Policy.MaxAttempts, delay, cause);
            _metrics?.ReconnectAttempted(in _sessionTag);

            await _supervisor.Delay(delay, cancellationToken).ConfigureAwait(false);

            try
            {
                await client.ReconnectAsync(cancellationToken).ConfigureAwait(false);
                await client.ResubscribeAsync(cancellationToken).ConfigureAwait(false);
                Metadata = await client.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
                when (IsTransient(exception) && !cancellationToken.IsCancellationRequested)
            {
                // The reason reported on the next attempt is the reason the *last* attempt failed,
                // not the one that started the backoff. A log that kept repeating the original
                // cause would hide a connection that started failing differently half way through.
                cause = exception;
                continue;
            }

            // Logged before the reset, so the message can say how many attempts it took.
            ExtensionsLog.ReconnectSucceeded(_logger, Session.Name, _supervisor.ConsecutiveFailures);
            _supervisor.RecordSuccess();
            State = LiveSessionState.Running;

            // The same counter StartSessionAsync publishes, because this is the same event: a
            // session was opened, and the paragraph above says a restart is a newly billed one.
            // A sessions.started that skipped this path would under-report exactly the sessions an
            // operator is watching it for, while still reading as authoritative.
            _metrics?.SessionStarted(in _sessionTag);
            return true;
        }

        ExtensionsLog.ReconnectExhausted(_logger, Session.Name, _supervisor.ConsecutiveFailures, cause);
        return false;
    }

    /// <summary>Whether a failure is worth reconnecting for.</summary>
    /// <remarks>
    /// <para>
    /// An explicit list, not <c>is not DatabentoAuthenticationException</c>. A negation classifies
    /// every exception type added later as transient by default, including one that means "stop",
    /// and nothing would say so.
    /// </para>
    /// <para>
    /// <see cref="ConnectTimeoutException"/> needs no arm of its own: it derives from
    /// <see cref="LiveConnectException"/>, which is already here.
    /// </para>
    /// </remarks>
    private static bool IsTransient(Exception exception) => exception switch
    {
        // Retrying a wrong key bills nothing and fixes nothing.
        DatabentoAuthenticationException => false,
        LiveConnectException => true,
        AuthTimeoutException => true,
        HeartbeatTimeoutException => true,
        LiveProtocolException => true,
        IOException => true,
        System.Net.Sockets.SocketException => true,
        _ => false,
    };

    /// <summary>
    /// Drains everything buffered, flushes, then refills. <see langword="true"/> when the gateway
    /// closed the stream.
    /// </summary>
    /// <remarks>
    /// <b>Drain before fill, and the inner loop must run to <see langword="false"/> before each
    /// refill.</b> That is not a style preference: a refill may shift the decoder's buffer, which
    /// is what invalidates a <c>RecordRef</c> the handler is still holding. It is also why nothing
    /// here needs the "drain once more at the end" that a fill-first loop needs — records read by
    /// the fill in one pass are drained at the top of the next, before the fill that returns zero.
    /// </remarks>
    private async Task<bool> PumpAsync(LiveClient client, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Drain(client);

            // A long from a monotonic counter, read twice. Nothing allocates, nothing is timed
            // when nobody is measuring, and no banned type is named — see MillisecondsPerTimestampTick.
            var flushStarted = _metrics is null ? 0L : Stopwatch.GetTimestamp();
            await _handler.OnFlushAsync(cancellationToken).ConfigureAwait(false);

            if (_metrics is { } metrics)
            {
                metrics.FlushCompleted(
                    (Stopwatch.GetTimestamp() - flushStarted) * MillisecondsPerTimestampTick,
                    in _sessionTag);
            }

            if (await client.FillBufferAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Hands every buffered record to the handler.</summary>
    /// <remarks>
    /// Non-<c>async</c> because a <c>RecordRef</c> cannot be in scope across an <c>await</c>, and
    /// free of delegates and closures because either would be a per-record allocation in the one
    /// loop that promises none. <c>ExtensionsAllocationTests</c> is what holds that.
    /// </remarks>
    private void Drain(LiveClient client)
    {
        var received = 0L;

        while (client.TryNextRecord(out var record))
        {
            _handler.OnRecord(record);
            received++;
        }

        // One field write per fill rather than one per record: the same number, for less — and the
        // same argument, for the same reason, applies to the counter below. A Counter<long>.Add
        // per record would put a call, a listener walk and a tag copy on the one path that
        // promises none; a long increment above reports the identical number for nothing. See
        // LiveSessionMetrics, which is written as though a listener is always attached because in
        // ExtensionsAllocationTests one is.
        RecordsReceived += received;

        if (received > 0)
        {
            _metrics?.RecordsReceived(received, in _sessionTag);
        }
    }

    /// <summary>
    /// Half-closes, so the gateway gets to finish rather than having the socket dropped on it —
    /// but bounded, so a gateway that never answers cannot hold the host's shutdown open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The losing task is left to complete on its own rather than cancelled. It holds a timer and
    /// nothing else, it finishes within <see cref="CloseTimeout"/>, and cancelling it would leave
    /// a faulted task nobody awaits — noise, in exchange for reclaiming one timer five seconds
    /// early.
    /// </para>
    /// <para>
    /// <b>The timer is a plain <see cref="Task.Delay(TimeSpan, CancellationToken)"/>, not
    /// <see cref="ReconnectSupervisor.Delay"/>.</b> It once was; <see cref="ReconnectSupervisor"/>
    /// is documented to hold the reconnect schedule and nothing else, and a caller can now replace
    /// that seam to turn it into a synchronisation point for the backoff itself — see
    /// <c>LiveSessionReconnectTests</c>. Routing this unrelated shutdown ceiling through the same
    /// seam would corrupt that signal, not just be untidy, so this waits on the BCL primitive
    /// directly. <see cref="Duration.ToTimeSpan"/> converts <see cref="CloseTimeout"/> for the one
    /// call that needs it; nothing here stores a <c>TimeSpan</c>.
    /// </para>
    /// </remarks>
    private async Task CloseAsync(LiveClient client)
    {
        var closing = client.CloseAsync();
        var expiring = Task.Delay(CloseTimeout.ToTimeSpan(), CancellationToken.None);

        if (await Task.WhenAny(closing, expiring).ConfigureAwait(false) == expiring)
        {
            ExtensionsLog.CloseTimedOut(_logger, Session.Name, CloseTimeout);
            return;   // DisposeAsync tears the socket down; the half-close was the courtesy.
        }

        await closing.ConfigureAwait(false);
    }

    /// <summary>Builds the <see cref="LiveClient"/> this runner drives, from <see cref="Session"/>.</summary>
    private LiveClient BuildClient() => new()
    {
        ApiKey = Session.ApiKey,
        Dataset = Session.Dataset,
        SendTsOut = Session.SendTsOut,
        Compression = Session.Compression,
        SlowReaderBehavior = Session.SlowReaderBehavior,
        HeartbeatInterval = Session.HeartbeatInterval,
        ReadTimeout = Session.ReadTimeout,
        Gateway = Session.Gateway,
    };

    /// <summary>Disposes the underlying <see cref="LiveClient"/>, if one was built. Idempotent.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_client is { } client)
        {
            _client = null;
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
