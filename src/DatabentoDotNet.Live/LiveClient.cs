using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Dbn.Internal;
using DatabentoDotNet.Live.Internal;
using NodaTime;

// System.Text.Encoding and DatabentoDotNet.Dbn.Encoding collide by simple name. Aliasing both
// keeps `encoding=dbn` sourced from the enum's own wire string rather than from a literal that
// nothing would notice drifting.
using DbnEncoding = DatabentoDotNet.Dbn.Encoding;
using Encoding = System.Text.Encoding;

namespace DatabentoDotNet.Live;

/// <summary>
/// A client for Databento's live gateway: real-time market data, and intraday replay from the
/// same socket.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>live::Client</c> (<c>live/client.rs</c>) and of the client half of its
/// <c>live::protocol::Protocol</c>. Upstream's <c>build()</c> connects <em>and</em>
/// authenticates in one call; splitting them is what lets each land with tests of its own against
/// the mock gateway, and it is what makes <see cref="ConnectTimeoutException"/> and
/// <see cref="AuthTimeoutException"/> nameable as the separate failures they are.
/// </para>
/// <para>
/// <b>The order is <see cref="ConnectAsync"/>, <see cref="AuthenticateAsync"/>,
/// <see cref="SubscribeAsync"/>, <see cref="StartAsync"/>, then records</b> — and
/// <see cref="StartAsync"/> is where billing begins. Nothing before it moves market data: a
/// subscription tells the gateway what to send later, and the gateway sends nothing at all until
/// the session is started.
/// </para>
/// <code>
/// await using var client = new LiveClient { ApiKey = key, Dataset = "EQUS.MINI" };
/// await client.ConnectAsync(ct);
/// await client.AuthenticateAsync(ct);
/// await client.SubscribeAsync(new Subscription { Schema = Schema.Trades, Symbols = Symbols.From("AAPL") }, ct);
///
/// var metadata = await client.StartAsync(ct);
/// while (true)
/// {
///     while (client.TryNextRecord(out var record))
///     {
///         if (record.TryGet&lt;TradeMsg&gt;(out var trade)) { Process(trade); }
///     }
///
///     if (await client.FillBufferAsync(ct) == 0) { break; }   // the gateway closed the stream
/// }
/// </code>
/// <para>
/// <see cref="RecordsAsync"/> is the same loop with each record copied onto the heap, for callers
/// who would rather write an <c>await foreach</c> than hold the zero-copy guarantee.
/// </para>
/// <para>
/// <b>Not thread-safe, and deliberately not made so.</b> One connection is one conversation with
/// the gateway, and the record loop is a single reader by construction — a lock around it would
/// suggest a concurrency the protocol does not have.
/// </para>
/// <para>
/// <b>No builder.</b> Upstream's <c>ClientBuilder&lt;AK, D&gt;</c> is generic type-state whose
/// only purpose is to make "no API key" and "no dataset" unrepresentable — <c>build()</c> exists
/// only on <c>ClientBuilder&lt;ApiKey, String&gt;</c>. C# 11 <c>required</c> init properties do
/// exactly that natively, checked by the compiler at every construction site. See PORTING.md §2.
/// </para>
/// <para>
/// <b><see cref="Endpoint"/> survives <see cref="CloseAsync"/>, on purpose.</b>
/// <see cref="ReconnectAsync"/> reuses the already-resolved address and does not re-resolve DNS,
/// as upstream's <c>reconnect()</c> does not (PORTING.md §4), so the resolved address has to
/// outlive the socket it came from.
/// </para>
/// <para>
/// <b>The handshake is not cancel-safe, and this type does not pretend otherwise.</b> A partially
/// written authentication line desynchronises the gateway, which closes the connection — so
/// <see cref="AuthenticateAsync"/> cancels by tearing the socket down rather than by abandoning a
/// half-finished write. A caller whose <see cref="AuthenticateAsync"/> throws is disconnected and
/// must connect again; there is no resuming it. PORTING.md §4.
/// </para>
/// </remarks>
public sealed class LiveClient : IAsyncDisposable
{
    /// <summary>The shortest heartbeat interval the gateway accepts.</summary>
    public static readonly Duration MinHeartbeatInterval = Duration.FromSeconds(5);

    /// <summary>The longest heartbeat interval the gateway accepts.</summary>
    public static readonly Duration MaxHeartbeatInterval = Duration.FromSeconds(1800);

    /// <summary>The connect budget used when none is set: ten seconds, matching upstream.</summary>
    public static readonly Duration DefaultConnectTimeout = Duration.FromSeconds(10);

    /// <summary>
    /// The handshake budget used when none is set: ten seconds, matching
    /// <see cref="DefaultConnectTimeout"/>. Upstream has no such budget at all — see
    /// <see cref="AuthTimeoutException"/>.
    /// </summary>
    public static readonly Duration DefaultAuthTimeout = Duration.FromSeconds(10);

    /// <summary>
    /// The read budget used when neither <see cref="ReadTimeout"/> nor
    /// <see cref="HeartbeatInterval"/> is set: 35 seconds, matching upstream's
    /// <c>heartbeat_timeout</c> fallback.
    /// </summary>
    public static readonly Duration DefaultReadTimeout = Duration.FromSeconds(35);

    /// <summary>
    /// How much longer than <see cref="HeartbeatInterval"/> the derived read budget runs: five
    /// seconds, matching upstream. It is the allowance for scheduling and network jitter between
    /// the gateway deciding to send a heartbeat and this client seeing one.
    /// </summary>
    public static readonly Duration ReadTimeoutHeartbeatMargin = Duration.FromSeconds(5);

    /// <summary>The prefix the gateway's challenge line must carry.</summary>
    private const string ChallengePrefix = "cram=";

    /// <summary>The line that starts the session and opens the record stream.</summary>
    private const string StartSessionRequest = "start_session";

    private readonly Duration? _heartbeatInterval;
    private readonly Duration? _readTimeout;
    private readonly List<Subscription> _subscriptions = [];

    private Socket? _socket;
    private NetworkStream? _stream;
    private uint _subscriptionCounter;

    /// <summary>
    /// The stream the record loop reads: <see cref="_stream"/> itself on a plain session, or a
    /// decompressor wrapped around it on a <see cref="Dbn.Compression.Zstd"/> one. Null until
    /// <see cref="StartAsync"/> has run, which is what makes it the check for "has the session
    /// started".
    /// </summary>
    private Stream? _reader;

    private DbnFsm? _fsm;

    /// <summary>The API key to authenticate with. Validated when it is constructed.</summary>
    public required ApiKey ApiKey { get; init; }

    /// <summary>
    /// The dataset to stream, in its wire spelling — <c>GLBX.MDP3</c>. A string rather than the
    /// codec's <c>Dataset</c> enum, for the reason given on <see cref="LiveGateway"/>.
    /// </summary>
    public required string Dataset { get; init; }

    /// <summary>
    /// Whether to ask the gateway to append its send timestamp to every record. When set, every
    /// record on the stream is eight bytes longer and decodes as
    /// <see cref="WithTsOut{T}"/>.
    /// </summary>
    public bool SendTsOut { get; init; }

    /// <summary>
    /// The compression to negotiate for the record stream. Defaults to
    /// <see cref="Dbn.Compression.None"/>.
    /// </summary>
    /// <remarks>
    /// Settled during the handshake and not afterwards: <c>compression=</c> travels on the
    /// authentication line, so what this says is what the stream after <c>start_session</c> is
    /// framed as. Control lines are always plaintext regardless.
    /// </remarks>
    public Compression Compression { get; init; } = Compression.None;

    /// <summary>
    /// How often the gateway should emit a heartbeat when no other record is due, or
    /// <see langword="null"/> to leave it to the gateway's own default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Heartbeats arrive as ordinary <c>SystemMsg</c> records carrying
    /// <see cref="SystemCode.Heartbeat"/>, not as control frames.
    /// </para>
    /// <para>
    /// <b>Validated here, where upstream leaves it to the gateway.</b> Upstream's builder documents
    /// the 5–1800 second range but only warns about sub-second precision, which it then silently
    /// discards (<c>live.rs:133-146</c>). Both are rejected instead: a value out of range costs a
    /// round trip and a closed connection to discover, and a silently truncated one means the
    /// interval in the caller's code is not the interval on the wire — the confidently-wrong
    /// failure this codebase exists to prevent.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The interval is outside <see cref="MinHeartbeatInterval"/>..<see cref="MaxHeartbeatInterval"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The interval is not a whole number of seconds.</exception>
    public Duration? HeartbeatInterval
    {
        get => _heartbeatInterval;
        init
        {
            if (value is { } interval)
            {
                if (interval.SubsecondNanoseconds != 0)
                {
                    throw new ArgumentException(
                        $"The gateway takes a heartbeat interval in whole seconds; {interval} has "
                        + $"{interval.SubsecondNanoseconds} ns left over.",
                        nameof(value));
                }

                if (interval < MinHeartbeatInterval || interval > MaxHeartbeatInterval)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value),
                        interval,
                        $"The heartbeat interval must be between {MinHeartbeatInterval} and "
                        + $"{MaxHeartbeatInterval}.");
                }
            }

            _heartbeatInterval = value;
        }
    }

    /// <summary>
    /// What the gateway should do when this client falls behind real time, or
    /// <see langword="null"/> to leave it to the gateway's default.
    /// </summary>
    public SlowReaderBehavior? SlowReaderBehavior { get; init; }

    /// <summary>
    /// How records from earlier DBN versions are handled while decoding. Defaults to
    /// <see cref="VersionUpgradePolicy.UpgradeToV3"/>, as upstream does.
    /// </summary>
    public VersionUpgradePolicy UpgradePolicy { get; init; } = VersionUpgradePolicy.UpgradeToV3;

    /// <summary>
    /// Connect here instead of at the host <see cref="LiveGateway"/> derives from
    /// <see cref="Dataset"/>. For tests against a mock gateway, and for the rare deployment that
    /// is told a different address.
    /// </summary>
    public EndPoint? Gateway { get; init; }

    /// <summary>
    /// How long <see cref="ConnectAsync"/> may spend before raising
    /// <see cref="ConnectTimeoutException"/>. Defaults to <see cref="DefaultConnectTimeout"/>.
    /// </summary>
    public Duration ConnectTimeout { get; init; } = DefaultConnectTimeout;

    /// <summary>
    /// How long the whole of <see cref="AuthenticateAsync"/> may spend before raising
    /// <see cref="AuthTimeoutException"/>. Defaults to <see cref="DefaultAuthTimeout"/>.
    /// </summary>
    /// <remarks>
    /// One budget for the exchange rather than one per line: a gateway that sends the greeting and
    /// then stalls has spent the caller's time just as surely as one that never speaks at all.
    /// </remarks>
    public Duration AuthTimeout { get; init; } = DefaultAuthTimeout;

    /// <summary>
    /// How long the record stream may go silent before <see cref="FillBufferAsync"/> raises
    /// <see cref="HeartbeatTimeoutException"/>, or <see langword="null"/> to derive it from
    /// <see cref="HeartbeatInterval"/>. See <see cref="EffectiveReadTimeout"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Upstream has no such setting</b> — its <c>heartbeat_timeout()</c> is always
    /// <c>heartbeat_interval + 5s</c>, or 35 seconds when no interval was requested, with no way
    /// to override it. That derivation is the right default and is what
    /// <see cref="EffectiveReadTimeout"/> computes; it is a poor *only* option, because the
    /// budget that matters is a property of the deployment — a replay of a quiet overnight
    /// session and a busy equities open are the same code reading very different streams.
    /// </para>
    /// <para>
    /// <b>Setting this shorter than the gateway's heartbeat interval will time out a healthy
    /// connection</b>, since a heartbeat is the only traffic guaranteed on a quiet feed. Nothing
    /// here rejects that combination: <see cref="HeartbeatInterval"/> may be left unset, in which
    /// case the gateway picks its own interval and this client has no way to know what it is.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The budget is zero or negative.</exception>
    public Duration? ReadTimeout
    {
        get => _readTimeout;
        init
        {
            if (value is { } timeout && timeout <= Duration.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    timeout,
                    "The read timeout must be positive. Leave it null to derive it from "
                    + $"{nameof(HeartbeatInterval)}.");
            }

            _readTimeout = value;
        }
    }

    /// <summary>
    /// The read budget actually applied: <see cref="ReadTimeout"/> when it is set, otherwise
    /// <see cref="HeartbeatInterval"/> plus <see cref="ReadTimeoutHeartbeatMargin"/>, otherwise
    /// <see cref="DefaultReadTimeout"/>.
    /// </summary>
    /// <remarks>
    /// Port of upstream's <c>heartbeat_timeout()</c>. Exposed rather than kept private because it
    /// is the number a caller has to know to interpret a
    /// <see cref="HeartbeatTimeoutException"/> — and because a derived value that cannot be read
    /// back is a setting whose effect can only be discovered by waiting for it.
    /// </remarks>
    public Duration EffectiveReadTimeout =>
        _readTimeout
        ?? (_heartbeatInterval is { } interval
            ? interval + ReadTimeoutHeartbeatMargin
            : DefaultReadTimeout);

    /// <summary>
    /// The address <see cref="ConnectAsync"/> actually reached, once it has. Survives
    /// <see cref="CloseAsync"/> so a reconnect can reuse it rather than resolving DNS again.
    /// </summary>
    public IPEndPoint? Endpoint { get; private set; }

    /// <summary>
    /// The gateway's greeting line — <c>lsg_version=…</c> — kept verbatim for diagnostics, and
    /// deliberately not parsed. Set by <see cref="AuthenticateAsync"/>, cleared by
    /// <see cref="ConnectAsync"/>.
    /// </summary>
    /// <remarks>
    /// Upstream reads it and logs it at debug, and nothing in the protocol depends on it. Keeping
    /// the string is what makes "which gateway build did this happen against" answerable from an
    /// exception report rather than only from a packet capture.
    /// </remarks>
    public string? Greeting { get; private set; }

    /// <summary>
    /// The <c>session_id</c> the gateway assigned, once <see cref="AuthenticateAsync"/> has
    /// succeeded, or <see langword="null"/> when it did not send one.
    /// </summary>
    /// <remarks>
    /// Upstream maps an absent <c>session_id</c> to the empty string (<c>protocol.rs</c>,
    /// <c>unwrap_or_default</c>), which makes "the gateway sent no id" and "the gateway sent an
    /// empty id" the same value. They are kept apart here: this is the identifier a support
    /// request is answered against, so whether it exists is worth being able to tell.
    /// </remarks>
    public string? SessionId { get; private set; }

    /// <summary>
    /// Every subscription sent on this client, in the order it was sent, with
    /// <see cref="Subscription.Id"/> filled in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Upstream's <c>subscriptions()</c>. It survives <see cref="CloseAsync"/> for the same reason
    /// <see cref="Endpoint"/> does: a reconnect has to replay them, and a list cleared on
    /// disconnect would leave nothing for <see cref="ResubscribeAsync"/> to replay.
    /// </para>
    /// <para>
    /// Read-only, where upstream also exposes <c>subscriptions_mut()</c>. The one thing that
    /// mutation is for upstream — clearing each <c>start</c> before a resubscribe, so a reconnect
    /// does not replay the same history twice — belongs to the resubscribe itself rather than to
    /// callers.
    /// </para>
    /// </remarks>
    public IReadOnlyList<Subscription> Subscriptions => _subscriptions;

    /// <summary>Whether a socket is currently open.</summary>
    public bool IsConnected => _socket is not null;

    /// <summary>Whether the handshake on the current connection has succeeded.</summary>
    public bool IsAuthenticated { get; private set; }

    /// <summary>
    /// Whether <see cref="StartAsync"/> has run on the current connection, so the record stream
    /// is open and <see cref="FillBufferAsync"/> and <see cref="TryNextRecord"/> may be called.
    /// </summary>
    public bool IsSessionStarted => _reader is not null;

    /// <summary>
    /// The DBN metadata the gateway sent when the session started, or <see langword="null"/>
    /// before <see cref="StartAsync"/>.
    /// </summary>
    /// <remarks>
    /// The same object <see cref="StartAsync"/> returns, kept because it is what
    /// <see cref="Dbn.Metadata.TsOut"/> and the symbol mappings are read from long after the call
    /// that produced it. It survives <see cref="CloseAsync"/> and is cleared by
    /// <see cref="ConnectAsync"/>, exactly as <see cref="Greeting"/> and <see cref="SessionId"/>
    /// are: what the last session said is a diagnostic, and a reconnect is the only thing that
    /// can replace it.
    /// </remarks>
    public Metadata? Metadata { get; private set; }

    /// <summary>
    /// Whether the record stream has ended: the gateway closed it cleanly, the read budget
    /// elapsed, or <see cref="CloseAsync"/> was called.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of upstream's <c>is_closed()</c>, including its starting value: a client that has
    /// never connected reports <see langword="false"/>, because this answers "did the stream
    /// end", not "is there a stream". <see cref="IsConnected"/> and
    /// <see cref="IsSessionStarted"/> answer the other two questions.
    /// </para>
    /// <para>
    /// <b>A clean close is not an error.</b> It surfaces as <see langword="false"/> from
    /// <see cref="TryNextRecord"/> and <c>0</c> from <see cref="FillBufferAsync"/> — the same
    /// values a merely-empty buffer produces — which is what makes this property the way to tell
    /// "no records right now" from "no records ever again". PORTING.md §2.
    /// </para>
    /// </remarks>
    public bool IsClosed { get; private set; }

    /// <summary>
    /// Opens a TCP connection to the gateway. Sends nothing: the handshake is a separate step.
    /// </summary>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <exception cref="InvalidOperationException">A connection is already open.</exception>
    /// <exception cref="ArgumentException">
    /// <see cref="Gateway"/> is unset and <see cref="Dataset"/> does not produce a usable host
    /// name. See <see cref="LiveGateway.For"/>.
    /// </exception>
    /// <exception cref="ConnectTimeoutException">The attempt outlived <see cref="ConnectTimeout"/>.</exception>
    /// <exception cref="LiveConnectException">The attempt failed — refused, unreachable, unresolvable.</exception>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_socket is not null)
        {
            throw new InvalidOperationException(
                "This client is already connected. Call CloseAsync before connecting again.");
        }

        await ConnectCoreAsync(Gateway ?? LiveGateway.For(Dataset), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the socket to <paramref name="endPoint"/> and resets everything a new connection
    /// invalidates.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="ConnectAsync"/> for <see cref="ReconnectAsync"/>'s sake, and that
    /// is the whole reason it takes an endpoint: <see cref="ConnectAsync"/> derives one from
    /// <see cref="Gateway"/> or the dataset, and <see cref="ReconnectAsync"/> passes the
    /// <see cref="Endpoint"/> a previous connect already resolved. Neither goes through the
    /// other's endpoint.
    /// </remarks>
    private async Task ConnectCoreAsync(EndPoint endPoint, CancellationToken cancellationToken)
    {
        // A budget that has already run out can only ever time out, and saying so before opening a
        // socket is both faster and easier to read than a race with a zero-length timer.
        var budgetMs = ConnectTimeout.TotalMilliseconds;
        if (budgetMs <= 0)
        {
            throw new ConnectTimeoutException(ConnectTimeout, endPoint);
        }

        // Match the address family when the caller named an address, so RemoteEndPoint comes back
        // in the same form they passed in rather than as an IPv4-mapped IPv6 address. A host name
        // gets the dual-stack socket, which is what resolves either family.
        var socket = endPoint is IPEndPoint address
            ? new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            : new Socket(SocketType.Stream, ProtocolType.Tcp);

        try
        {
            socket.NoDelay = true;

            if (socket.AddressFamily == AddressFamily.InterNetworkV6)
            {
                // A host-name connect goes out on a dual-stack socket, so RemoteEndPoint — and
                // therefore Endpoint — comes back as an IPv4-mapped IPv6 address whenever the
                // gateway answered over IPv4. ReconnectAsync then dials that address on a socket
                // built for its family, and an IPv6 socket is V6ONLY by default: it would refuse a
                // mapped address outright, so a client that reached the gateway by name could not
                // reconnect to it at all. The two-argument constructor above already produces a
                // dual-mode socket, which is what makes the first connect work; this is what makes
                // the second one work.
                socket.DualMode = true;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(checked((int)Math.Min(budgetMs, int.MaxValue)));

            try
            {
                await socket.ConnectAsync(endPoint, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ConnectTimeoutException(ConnectTimeout, endPoint);
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                // A refused or unreachable endpoint comes back immediately and is not a timeout.
                // Wrapping it is what puts the endpoint in the message: the host was derived from
                // the dataset, so it is the one thing the caller cannot work out for themselves.
                throw new LiveConnectException(endPoint, exception);
            }
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        _socket = socket;

        // ownsSocket: false — CloseAsync shuts the socket down itself, and it does so after the
        // stream is gone. Letting the stream own it would make the order of those two a matter of
        // which field happened to be disposed first.
        _stream = new NetworkStream(socket, ownsSocket: false);
        Endpoint = (IPEndPoint)socket.RemoteEndPoint!;

        // A new connection carries no session, whatever the last one did.
        Greeting = null;
        SessionId = null;
        Metadata = null;
        IsClosed = false;
    }

    /// <summary>
    /// Runs the CRAM handshake: reads the greeting and the challenge, sends the authentication
    /// request, and reads the response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of upstream's <c>Protocol::authenticate</c> (<c>live/protocol.rs</c>). The request is
    /// one line:
    /// </para>
    /// <code>
    /// auth={sha256_hex}-{bucket_id}|dataset={ds}|encoding=dbn|compression={c}|ts_out={0|1}|client={ua}
    /// </code>
    /// <para>
    /// where the digest is <c>SHA256(challenge + "|" + apiKey)</c> in lowercase hex and the bucket
    /// id is the <em>last</em> five characters of the key — the only part of it that goes on the
    /// wire. <c>heartbeat_interval_s</c> and <c>slow_reader_behavior</c> follow when they are set,
    /// and are omitted entirely when they are not, so the gateway applies its own defaults rather
    /// than ours.
    /// </para>
    /// <para>
    /// <b>Not cancel-safe, and it cancels by disconnecting.</b> The gateway reads a control
    /// message as a whole line; half of one desynchronises it and it closes the connection. So
    /// neither <paramref name="cancellationToken"/> nor <see cref="AuthTimeout"/> is threaded into
    /// the middle of a write — both abort by tearing the socket down, which fails the pending read
    /// or write outright. Every failure here leaves the client disconnected: there is nothing
    /// left to retry the handshake on. PORTING.md §4.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">
    /// Cancels the handshake by closing the connection. See the remarks.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// No connection is open, or this connection has already authenticated.
    /// </exception>
    /// <exception cref="DatabentoAuthenticationException">The gateway rejected the credentials.</exception>
    /// <exception cref="LiveProtocolException">
    /// The gateway sent something that is not a handshake, or stopped sending mid-way.
    /// </exception>
    /// <exception cref="AuthTimeoutException">The exchange outlived <see cref="AuthTimeout"/>.</exception>
    public async Task AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        if (_socket is null || _stream is null)
        {
            throw new InvalidOperationException(
                "This client is not connected. Call ConnectAsync before authenticating.");
        }

        if (IsAuthenticated)
        {
            throw new InvalidOperationException(
                "This connection has already authenticated. Reconnect to authenticate again.");
        }

        var budgetMs = AuthTimeout.TotalMilliseconds;
        if (budgetMs <= 0)
        {
            Teardown();
            throw new AuthTimeoutException(AuthTimeout);
        }

        var socket = _socket;
        var channel = new ControlChannel(_stream);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(checked((int)Math.Min(budgetMs, int.MaxValue)));

        // The cancellation mechanism, and the reason nothing below takes a token: disposing the
        // socket fails whichever read or write is outstanding, instead of abandoning a write with
        // some of the auth line already on the wire.
        using var abort = timeout.Token.Register(static state => ((Socket)state!).Dispose(), socket);

        try
        {
            Greeting = await channel.ReadLineAsync("the gateway greeting", CancellationToken.None)
                .ConfigureAwait(false);

            var challengeLine = await channel.ReadLineAsync("the CRAM challenge", CancellationToken.None)
                .ConfigureAwait(false);
            if (!challengeLine.StartsWith(ChallengePrefix, StringComparison.Ordinal))
            {
                // Upstream logs the response and returns Error::internal. Failing here rather than
                // reading past it matters: strip_prefix on a line without the prefix yields no
                // challenge at all, and hashing "" against the key produces a digest the gateway
                // rejects — reporting a broken gateway as a bad API key.
                throw new LiveProtocolException(
                    $"Expected a '{ChallengePrefix}' challenge from the live gateway, got: '{challengeLine}'.");
            }

            await channel.SendLineAsync(
                    BuildAuthRequest(challengeLine[ChallengePrefix.Length..]),
                    CancellationToken.None)
                .ConfigureAwait(false);

            var response = await channel.ReadLineAsync("the authentication response", CancellationToken.None)
                .ConfigureAwait(false);

            // The exchange is over, so stop the budget before it can tear down a socket this method
            // is about to call authenticated — a race whose window is the gap between the gateway's
            // last byte and the assignments below, and whose result would be an IsAuthenticated
            // client whose next read throws ObjectDisposedException. Dispose waits for a callback
            // already in flight, so the check after it is conclusive rather than another race.
            abort.Dispose();
            if (timeout.IsCancellationRequested)
            {
                Teardown();
                cancellationToken.ThrowIfCancellationRequested();
                throw new AuthTimeoutException(AuthTimeout);
            }

            var fields = ParseFields(response);

            // An absent success key means something went wrong just as surely as success=0 does;
            // upstream treats the two the same way and so does this.
            if (!fields.TryGetValue("success", out var success)
                || !string.Equals(success, "1", StringComparison.Ordinal))
            {
                var error = fields.GetValueOrDefault("error");
                throw new DatabentoAuthenticationException(
                    error is null
                        ? $"The live gateway rejected the API key {ApiKey} without giving a reason. "
                          + $"It answered: '{response}'."
                        : $"The live gateway rejected the API key {ApiKey}: {error}")
                {
                    Error = error,
                    Response = response,
                };
            }

            SessionId = fields.GetValueOrDefault("session_id");
            IsAuthenticated = true;
        }
        catch (Exception exception) when (timeout.IsCancellationRequested && IsTornDown(exception))
        {
            Teardown();
            cancellationToken.ThrowIfCancellationRequested();
            throw new AuthTimeoutException(AuthTimeout);
        }
        catch
        {
            // Every other failure here also ends the connection: the gateway closes it after a
            // rejected or malformed handshake, and a socket that survived in this client would
            // only invite a second AuthenticateAsync on a stream the gateway has stopped reading.
            Teardown();
            throw;
        }
    }

    /// <summary>
    /// Closes the current connection and opens a fresh one to the same address, running the
    /// handshake again. Subscriptions are kept but not replayed — see <see cref="ResubscribeAsync"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of upstream's <c>reconnect()</c>. <b>It reuses <see cref="Endpoint"/> rather than
    /// resolving the host again</b>, which is deliberate upstream and is why that property
    /// survives <see cref="CloseAsync"/>: a reconnect should reach the same gateway instance, and
    /// DNS may have moved on. Neither <see cref="Gateway"/> nor <see cref="Dataset"/> is consulted
    /// here at all.
    /// </para>
    /// <para>
    /// <b>What it replaces:</b> the socket, the handshake, and therefore <see cref="Greeting"/> and
    /// <see cref="SessionId"/> — a reconnect is a new session and the gateway issues a new id for
    /// it. <see cref="Metadata"/> is cleared, and <see cref="IsClosed"/> goes back to
    /// <see langword="false"/>.
    /// </para>
    /// <para>
    /// <b>What it does not do:</b> replay subscriptions, or start the session. Both are separate
    /// calls because both are the caller's decision — upstream keeps <c>reconnect</c> and
    /// <c>resubscribe</c> apart for exactly that reason, and fusing them into an auto-reconnect
    /// would replay subscriptions a caller may no longer want. The full sequence after a stream
    /// ends is <see cref="ReconnectAsync"/>, <see cref="ResubscribeAsync"/>,
    /// <see cref="StartAsync"/>.
    /// </para>
    /// <para>
    /// <b>A close that fails does not stop the reconnect</b>, matching upstream, which logs a
    /// warning and carries on. The connection being replaced is by definition the broken one, and
    /// refusing to replace it because it would not shut down politely is strictly worse than
    /// replacing it.
    /// </para>
    /// <para>
    /// <b>The subscription id counter is not reset, where upstream sets it back to zero.</b>
    /// Upstream's <c>resubscribe</c> then raises it to the highest id it replayed, so in the
    /// ordinary reconnect-then-resubscribe sequence the two agree exactly. They differ only when a
    /// caller reconnects and subscribes to something new <em>without</em> resubscribing: upstream
    /// hands out id 1 again while its retained list still holds a different subscription with that
    /// id, so <see cref="Subscriptions"/> would carry two entries the gateway cannot tell apart in
    /// an error. A monotonic counter costs nothing on the wire — the id is a correlation handle,
    /// not a sequence the gateway checks — and it cannot produce that pair. See PORTING.md §4.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Cancels the connect, then the handshake. See the remarks
    /// on <see cref="AuthenticateAsync"/> for what cancelling a handshake costs.</param>
    /// <exception cref="InvalidOperationException">
    /// This client has never connected, so there is no address to reuse.
    /// </exception>
    /// <exception cref="ConnectTimeoutException">The attempt outlived <see cref="ConnectTimeout"/>.</exception>
    /// <exception cref="LiveConnectException">The attempt failed — refused, or unreachable.</exception>
    /// <exception cref="DatabentoAuthenticationException">The gateway rejected the credentials.</exception>
    /// <exception cref="LiveProtocolException">The gateway sent something that is not a handshake.</exception>
    /// <exception cref="AuthTimeoutException">The handshake outlived <see cref="AuthTimeout"/>.</exception>
    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        if (Endpoint is null)
        {
            throw new InvalidOperationException(
                "This client has never connected, so there is no resolved address to reconnect "
                + $"to. Call {nameof(ConnectAsync)} first.");
        }

        try
        {
            await CloseAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTornDown(exception))
        {
            // Upstream logs this and proceeds; there is no logger here and nothing a caller could
            // usefully do with it. CloseAsync clears every field before it disposes anything, so
            // whatever failed on the way out, the reconnect below starts from a clean slate.
        }

        await ConnectCoreAsync(Endpoint, cancellationToken).ConfigureAwait(false);
        await AuthenticateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a subscription, splitting it across as many messages as its symbol count needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of upstream's <c>Client::subscribe</c> and the <c>Protocol::subscribe</c> it calls.
    /// Each message is one line:
    /// </para>
    /// <code>
    /// schema={s}|stype_in={t}|symbols={csv}|snapshot={0|1}|is_last={0|1}[|start={unix_nanos}]|id={n}
    /// </code>
    /// <para>
    /// with at most <see cref="Symbols.ChunkSize"/> symbols per line and <c>is_last=1</c> on the
    /// final one only. A gateway that sees <c>is_last=1</c> treats the subscription as complete,
    /// so the flag is what makes a chunked subscription one subscription rather than several
    /// partial ones.
    /// </para>
    /// <para>
    /// <b>Returning what was sent, rather than nothing.</b> Upstream mutates the caller's
    /// <c>Subscription</c> to record the id it assigned; <see cref="Subscription"/> is immutable,
    /// so the sent form comes back instead. It is also appended to
    /// <see cref="Subscriptions"/>, which is what <see cref="ResubscribeAsync"/> replays.
    /// </para>
    /// <para>
    /// <b>Subscribing is legal before and after the session starts.</b> Both are the same code
    /// path on the same socket — the gateway distinguishes them, this client does not need to.
    /// </para>
    /// <para>
    /// <b>Not cancel-safe, and it cancels by disconnecting</b>, for the same reason
    /// <see cref="AuthenticateAsync"/> is not: a half-written subscription line desynchronises the
    /// gateway, which closes the connection. Cancelling tears the socket down rather than
    /// abandoning a partial write, and any failure here leaves the client disconnected.
    /// PORTING.md §4.
    /// </para>
    /// </remarks>
    /// <param name="subscription">What to subscribe to.</param>
    /// <param name="cancellationToken">Cancels by closing the connection. See the remarks.</param>
    /// <returns>The subscription as sent, with <see cref="Subscription.Id"/> filled in.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="subscription"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The subscription combines a snapshot with a replay start, asks for a snapshot on a schema
    /// other than <see cref="Schema.Mbo"/>, or names no symbols. Nothing is written to the socket.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The client is not connected or has not authenticated, or every subscription id has been
    /// used.
    /// </exception>
    /// <exception cref="LiveProtocolException">The gateway closed the connection mid-write.</exception>
    public async Task<Subscription> SubscribeAsync(
        Subscription subscription,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        // Before the connection checks, so that a subscription the client would never send is
        // rejected the same way whether or not a socket happens to be open.
        subscription.Validate(nameof(subscription));

        RequireAuthenticatedConnection("subscribing");

        var sent = subscription.Id is null
            ? subscription with { Id = NextSubscriptionId() }
            : subscription;

        await SendSubscriptionAsync(sent, cancellationToken).ConfigureAwait(false);

        _subscriptions.Add(sent);
        return sent;
    }

    /// <summary>
    /// Sends every subscription this client has made again, each without its replay
    /// <see cref="Subscription.Start"/>. Usually the call after <see cref="ReconnectAsync"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of upstream's <c>resubscribe()</c>. <b>Clearing <see cref="Subscription.Start"/> is the
    /// whole point of it.</b> A reconnect that replayed the original subscriptions verbatim would
    /// ask the gateway for the same intraday history a second time, and the symptom — duplicated
    /// records after a reconnect — looks like a gateway fault and is not one. PORTING.md §4.
    /// </para>
    /// <para>
    /// <b>The retained subscriptions are cleared too, not just the lines on the wire.</b>
    /// <see cref="Subscriptions"/> reports what was last sent, so after this every entry has a
    /// <see langword="null"/> <see cref="Subscription.Start"/> — which is also what stops a second
    /// reconnect from replaying a start this one already dropped. Upstream mutates its stored
    /// subscriptions in place for the same reason; <see cref="Subscription"/> is immutable, so the
    /// entry is replaced rather than edited.
    /// </para>
    /// <para>
    /// <b>Ids are kept, not reassigned.</b> A replayed subscription is the same subscription, and
    /// the id is what the gateway quotes when it raises an error about one. Nothing is appended to
    /// <see cref="Subscriptions"/> either — this replays the list, it does not grow it.
    /// </para>
    /// <para>
    /// <b>Not cancel-safe, and it cancels by disconnecting</b>, exactly as
    /// <see cref="SubscribeAsync"/> is not. A resubscribe that fails part way through has left
    /// some subscriptions sent and some not, on a socket the gateway has stopped reading; the
    /// repair is another <see cref="ReconnectAsync"/> and another call to this, which by then has
    /// no starts left to drop.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Cancels by closing the connection. See the remarks.</param>
    /// <exception cref="InvalidOperationException">
    /// The client is not connected or has not authenticated.
    /// </exception>
    /// <exception cref="LiveProtocolException">The gateway closed the connection mid-write.</exception>
    public async Task ResubscribeAsync(CancellationToken cancellationToken = default)
    {
        RequireAuthenticatedConnection("resubscribing");

        for (var i = 0; i < _subscriptions.Count; i++)
        {
            // Cleared in the retained list before the line goes out rather than after it, so a
            // resubscribe that fails half way cannot leave a start behind for the next attempt to
            // replay — the one thing this method exists to prevent.
            var replay = _subscriptions[i] with { Start = null };
            _subscriptions[i] = replay;

            // Upstream raises its counter to cover the ids it just replayed, having reset it to
            // zero in reconnect(). This counter never resets, so it is already past every id it
            // assigned itself; what this covers is an id a *caller* chose, which SubscribeAsync
            // records without counting.
            if (replay.Id is { } id && id > _subscriptionCounter)
            {
                _subscriptionCounter = id;
            }

            await SendSubscriptionAsync(replay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes one subscription to the socket, split across as many lines as its symbol count
    /// needs. Shared by <see cref="SubscribeAsync"/> and <see cref="ResubscribeAsync"/>, which
    /// differ in what they do with the list and not in what they put on the wire.
    /// </summary>
    /// <remarks>
    /// The caller has already run <see cref="RequireAuthenticatedConnection"/>, so the socket and
    /// the stream are both there.
    /// </remarks>
    private async Task SendSubscriptionAsync(
        Subscription subscription,
        CancellationToken cancellationToken)
    {
        var chunks = subscription.Symbols.ToChunks();
        var socket = _socket!;
        var channel = new ControlChannel(_stream!);

        using var abort = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(static state => ((Socket)state!).Dispose(), socket)
            : default;

        try
        {
            for (var i = 0; i < chunks.Length; i++)
            {
                await channel.SendLineAsync(
                        BuildSubscribeRequest(subscription, chunks[i], isLast: i == chunks.Length - 1),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested && IsTornDown(exception))
        {
            Teardown();
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch
        {
            // A subscription that failed part-way through has left the gateway reading a message
            // it will never see the end of. There is nothing to retry on this socket.
            Teardown();
            throw;
        }
    }

    /// <summary>
    /// Throws unless a connection is open and has completed the handshake.
    /// </summary>
    /// <param name="action">
    /// What the caller was about to do, as a gerund — it completes "Call ConnectAsync before
    /// <paramref name="action"/>."
    /// </param>
    private void RequireAuthenticatedConnection(string action)
    {
        if (_socket is null || _stream is null)
        {
            throw new InvalidOperationException(
                $"This client is not connected. Call {nameof(ConnectAsync)} before {action}.");
        }

        if (!IsAuthenticated)
        {
            throw new InvalidOperationException(
                $"This connection has not authenticated. Call {nameof(AuthenticateAsync)} before "
                + $"{action}.");
        }
    }

    /// <summary>
    /// Starts the session: sends <c>start_session</c> and reads the DBN metadata the gateway
    /// answers with, after which records flow.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of upstream's <c>Client::start</c>. <b>This is the line that begins billing.</b>
    /// Everything before it — the handshake, every subscription — moves no market data and costs
    /// nothing; the gateway sends nothing at all until this call, and then sends everything the
    /// subscriptions asked for.
    /// </para>
    /// <para>
    /// <b>Compression is settled here, not negotiated here.</b> <see cref="Compression"/> travels
    /// on the authentication line, so by the time this runs the gateway has already decided how
    /// the stream after this point is framed. A <see cref="Dbn.Compression.Zstd"/> session gets a
    /// decompressor between the socket and the state machine from the metadata block onwards;
    /// control lines were and remain plaintext, which is why <see cref="Internal.ControlChannel"/>
    /// reads the socket directly and one byte at a time — a buffered reader would have swallowed
    /// the front of this metadata while reading the authentication response.
    /// </para>
    /// <para>
    /// <b><c>ts_out</c> comes from the metadata, not from <see cref="SendTsOut"/>.</b> What the
    /// client asked for and what the stream carries are two different facts, and only the second
    /// one determines whether each record is eight bytes longer. The state machine is built
    /// without a <c>tsOut</c> hint precisely so the metadata block is the only thing that can set
    /// it; a client that asked and was refused then decodes correctly rather than confidently
    /// misreading every record by eight bytes.
    /// </para>
    /// <para>
    /// <b>Not cancel-safe, and it cancels by disconnecting</b>, for the same reason
    /// <see cref="AuthenticateAsync"/> is not: <c>start_session</c> is a control line, and half of
    /// one desynchronises the gateway. The wait for the metadata is bounded by
    /// <see cref="EffectiveReadTimeout"/>.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Cancels by closing the connection. See the remarks.</param>
    /// <returns>The session's DBN metadata, also kept in <see cref="Metadata"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// The client is not connected, has not authenticated, or has already started a session.
    /// </exception>
    /// <exception cref="LiveProtocolException">
    /// The gateway closed the connection before sending the metadata block.
    /// </exception>
    /// <exception cref="HeartbeatTimeoutException">
    /// The metadata did not arrive within <see cref="EffectiveReadTimeout"/>.
    /// </exception>
    /// <exception cref="DbnDecodeException">What the gateway sent is not valid DBN metadata.</exception>
    public async Task<Metadata> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_socket is null || _stream is null)
        {
            throw new InvalidOperationException(
                "This client is not connected. Call ConnectAsync before starting the session.");
        }

        if (!IsAuthenticated)
        {
            throw new InvalidOperationException(
                "This connection has not authenticated. Call AuthenticateAsync before starting the session.");
        }

        if (IsSessionStarted)
        {
            throw new InvalidOperationException(
                "This session has already been started. Reconnect to start another.");
        }

        var socket = _socket;
        var stream = _stream;
        var channel = new ControlChannel(stream);
        var budget = EffectiveReadTimeout;

        var fsm = new DbnFsm(UpgradePolicy);

        // leaveOpen: the socket outlives the frame, and CloseAsync disposes the two in order.
        var reader = Compression == Dbn.Compression.Zstd
            ? ZstdDecompressor.Decompress(stream, leaveOpen: true)
            : stream;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ToMilliseconds(budget));

            // The same mechanism AuthenticateAsync uses, for the same reason: there is no way to
            // abandon a pending socket read in .NET without disposing the socket, and abandoning
            // a half-written control line is worse than losing the connection.
            using var abort = timeout.Token.Register(static state => ((Socket)state!).Dispose(), socket);

            try
            {
                await channel.SendLineAsync(StartSessionRequest, CancellationToken.None).ConfigureAwait(false);

                while (true)
                {
                    var read = await reader.ReadAsync(fsm.SpaceMemory(), CancellationToken.None)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new LiveProtocolException(
                            "The live gateway closed the connection before sending the session metadata.");
                    }

                    fsm.Fill(read);
                    if (TryDecodeMetadata(fsm))
                    {
                        break;
                    }
                }

                // Stop the budget before the session is called started, so it cannot tear down a
                // socket this method is about to hand back as live. AuthenticateAsync closes the
                // same window the same way.
                abort.Dispose();
                if (timeout.IsCancellationRequested)
                {
                    Teardown();
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new HeartbeatTimeoutException(budget, "the session metadata");
                }
            }
            catch (Exception exception) when (timeout.IsCancellationRequested && IsTornDown(exception))
            {
                Teardown();
                cancellationToken.ThrowIfCancellationRequested();
                throw new HeartbeatTimeoutException(budget, "the session metadata");
            }
            catch
            {
                // A session that failed to start leaves the gateway mid-stream with no way to
                // resynchronise, exactly as a failed handshake does.
                Teardown();
                throw;
            }
        }
        catch
        {
            // The decompressor, if one was built, is this method's to clean up until _reader
            // owns it. Teardown has already dealt with the socket underneath it.
            if (!ReferenceEquals(reader, stream))
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }

        _fsm = fsm;
        _reader = reader;
        Metadata = fsm.Metadata;

        // Not null: TryDecodeMetadata returns true only for ProcessStatus.Metadata, which the
        // state machine reports from the same step that assigns its Metadata property.
        return fsm.Metadata!;
    }

    /// <summary>
    /// Reads whatever the gateway has sent into the decoder's buffer, ready for
    /// <see cref="TryNextRecord"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of upstream's <c>fill_buf()</c>, and the half of the zero-copy pair that does the
    /// I/O. The canonical loop is:
    /// </para>
    /// <code>
    /// while (true)
    /// {
    ///     while (client.TryNextRecord(out var record)) { Process(record); }
    ///     if (await client.FillBufferAsync(ct) == 0) { break; }
    /// }
    /// </code>
    /// <para>
    /// <b>Zero is the end of the stream, not an error</b> — PORTING.md §2. It reads directly into
    /// the state machine's own buffer through <c>SpaceMemory()</c>, so no byte is copied on the
    /// way in; that, and <see cref="TryNextRecord"/> handing back a reference into the same
    /// buffer, is what makes the pair allocation-free per record.
    /// </para>
    /// <para>
    /// <b>A read the socket can already satisfy costs nothing, and that is deliberate.</b> The
    /// read is started before any timeout machinery exists, and when it completes synchronously —
    /// the ordinary case on a stream with bytes waiting — this returns without building a
    /// <see cref="CancellationTokenSource"/>, without registering a callback, and without boxing
    /// an <c>async</c> state machine. Those three allocations are what a naive shape would pay on
    /// every call, and they are what the allocation assertion in the test suite would find. The
    /// read budget therefore applies only to a read that actually waits, which is the only read it
    /// was ever describing.
    /// </para>
    /// <para>
    /// <b>Cancellation ends the session here, where upstream's is cancel-safe.</b> Upstream's
    /// <c>fill_buf</c> can be dropped mid-read inside a <c>tokio::select!</c> and lose nothing,
    /// because tokio's <c>AsyncRead</c> guarantees a cancelled read consumed nothing. .NET makes
    /// no such guarantee about a socket read, and bytes taken off the socket but not handed back
    /// are not a lost read — they are a decoder that silently resumes mid-record. So a cancelled
    /// fill marks the client <see cref="IsClosed"/> rather than pretending the stream is still
    /// intact. Use the token to <em>stop</em> the loop, not to pause it.
    /// </para>
    /// <para>
    /// The obvious repair — race the read against the token and keep the pending
    /// <see cref="Task"/> for the next call — was rejected: the buffer that read is writing into
    /// belongs to the state machine, and the next <c>SpaceMemory()</c> may shift it underneath an
    /// in-flight read. That trades a detectable failure for a data race.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Stops the loop. See the remarks.</param>
    /// <returns>
    /// How many bytes were read, or <c>0</c> when the gateway closed the stream cleanly — after
    /// which <see cref="IsClosed"/> is set and every later call returns <c>0</c> without touching
    /// the socket.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The session has not been started. A session that has <em>ended</em> returns <c>0</c>
    /// instead — see <see cref="IsClosed"/>.
    /// </exception>
    /// <exception cref="HeartbeatTimeoutException">
    /// Nothing arrived within <see cref="EffectiveReadTimeout"/>. The connection is torn down.
    /// </exception>
    public ValueTask<int> FillBufferAsync(CancellationToken cancellationToken = default)
    {
        // Checked before the session guard, not after, so that a client whose stream has ended —
        // by a clean close, a timeout, or CloseAsync, all of which drop the decoder — answers the
        // question the caller is actually asking. Idempotent rather than an error: a caller
        // draining a loop should not have to guard the call whose result they are already
        // checking.
        if (IsClosed)
        {
            return new ValueTask<int>(0);
        }

        var reader = _reader;
        var fsm = _fsm;
        if (reader is null || fsm is null)
        {
            throw new InvalidOperationException(
                "This session has not started. Call StartAsync before reading records.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Started before any timeout machinery exists, so that a read the socket can satisfy from
        // bytes it already holds costs nothing at all — see the "read already waiting" paragraph
        // in the remarks. The token goes in here rather than into a linked source because on this
        // path there is nothing to link it to.
        var pending = reader.ReadAsync(fsm.SpaceMemory(), cancellationToken);

        return pending.IsCompletedSuccessfully
            ? new ValueTask<int>(Complete(fsm, pending.Result))
            : AwaitFillAsync(pending, fsm, EffectiveReadTimeout, cancellationToken);
    }

    /// <summary>
    /// The slow half of <see cref="FillBufferAsync"/>: a read that did not complete synchronously,
    /// and therefore the only one the read budget has anything to say about.
    /// </summary>
    private async ValueTask<int> AwaitFillAsync(
        ValueTask<int> pending,
        DbnFsm fsm,
        Duration budget,
        CancellationToken cancellationToken)
    {
        var socket = _socket;

        // Not linked to the caller's token: `pending` already carries it, so this source has one
        // meaning and one only — the budget elapsed. That is what lets IsReadBudgetElapsed tell a
        // timeout from a cancellation without inspecting both.
        using var timeout = new CancellationTokenSource();
        timeout.CancelAfter(ToMilliseconds(budget));

        // The token alone cannot be relied on to interrupt a read already in flight, so the budget
        // is backed by disposing the socket. That is destructive, which is exactly what a
        // heartbeat timeout means: upstream marks itself closed and requires a reconnect too.
        using var abort = socket is null
            ? default
            : timeout.Token.Register(static state => ((Socket)state!).Dispose(), socket);

        try
        {
            return Complete(fsm, await pending.ConfigureAwait(false));
        }
        catch (Exception exception) when (IsReadBudgetElapsed(timeout, exception, cancellationToken))
        {
            IsClosed = true;
            Teardown();
            throw new HeartbeatTimeoutException(budget, "the next record");
        }
        catch
        {
            // Caller cancellation and I/O failures alike leave the decoder's position in the
            // stream unknowable. Saying so is the whole difference between a client that stops
            // and one that resumes mid-record.
            IsClosed = true;
            throw;
        }
    }

    /// <summary>
    /// Books a completed read in: hands the bytes to the state machine, or notes that the stream
    /// has ended.
    /// </summary>
    private int Complete(DbnFsm fsm, int read)
    {
        if (read == 0)
        {
            IsClosed = true;
            return 0;
        }

        fsm.Fill(read);
        return read;
    }

    /// <summary>
    /// Decodes the next record already in the buffer, without touching the socket.
    /// </summary>
    /// <param name="record">
    /// Receives the decoded record. <b>Valid only until the next call on this client</b> — it
    /// points into the decoder's buffer, which the next <see cref="FillBufferAsync"/> may move.
    /// Copy what you need out of it, or use <see cref="RecordsAsync"/>, which does that for you.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when a record was decoded. <see langword="false"/> means the buffer
    /// holds no complete record — call <see cref="FillBufferAsync"/> and try again, and check
    /// <see cref="IsClosed"/> to tell "not yet" from "not ever".
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The session has not been started. A session that has <em>ended</em> returns
    /// <see langword="false"/> instead — see <see cref="IsClosed"/>.
    /// </exception>
    /// <exception cref="DbnDecodeException">The buffered bytes are not valid DBN.</exception>
    /// <remarks>
    /// Port of upstream's <c>try_next_record()</c>. Its single-call <c>next_record()</c> has no
    /// .NET equivalent and never can: an <c>async</c> method cannot return a <c>ref struct</c>,
    /// so there is no <c>Task&lt;RecordRef&gt;</c>. A <see cref="RecordRef"/> <em>local</em>
    /// inside an <c>async</c> method is fine — only one that survives an <c>await</c> is
    /// rejected, as CS4007, which is the lifetime rule the sentence above states by hand.
    /// PORTING.md §1.
    /// </remarks>
    public bool TryNextRecord(out RecordRef record)
    {
        var fsm = _fsm;
        if (fsm is not null)
        {
            return fsm.TryNextRecord(out record);
        }

        if (IsClosed)
        {
            // The session ended and its decoder went with it. "No more records" is the honest
            // answer and the one the drain loop is already checking for — the same reading that
            // makes a clean close `false` here rather than an exception. Only a client that never
            // started a session is a caller mistake.
            record = default;
            return false;
        }

        throw new InvalidOperationException(
            "This session has not started. Call StartAsync before reading records.");
    }

    /// <summary>
    /// The record stream as an <see cref="IAsyncEnumerable{T}"/>: the same loop as
    /// <see cref="FillBufferAsync"/> and <see cref="TryNextRecord"/>, with each record copied so
    /// it can cross the <c>yield</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the convenient surface, and it copies — necessarily.</b> <c>yield return</c>
    /// carries the same restriction <c>await</c> does, so a <c>ref struct</c> cannot leave an
    /// iterator at all. Each record therefore arrives as an <see cref="OwnedRecord"/>: two
    /// allocations, stated on that type rather than hidden here. Callers who need the zero-copy
    /// guarantee want the <see cref="FillBufferAsync"/>/<see cref="TryNextRecord"/> pair, which
    /// this is written in terms of and does not bypass.
    /// </para>
    /// <para>
    /// The enumeration ends when the gateway closes the stream cleanly. Cancelling it ends the
    /// session, for the reason given on <see cref="FillBufferAsync"/>.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Stops the enumeration and ends the session.</param>
    /// <returns>Every record the gateway sends, in order, until the stream ends.</returns>
    /// <exception cref="InvalidOperationException">
    /// The session has not been started. Raised from the first enumeration step, not from this
    /// call — an iterator method's body does not run until it is enumerated.
    /// </exception>
    public async IAsyncEnumerable<OwnedRecord> RecordsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            // The drain is a separate method because a RecordRef cannot be in scope across the
            // yield below — the copy has to happen where the compiler can see the reference die.
            while (TryNextOwnedRecord(out var record))
            {
                yield return record;
            }

            if (await FillBufferAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Closes the connection, keeping <see cref="Endpoint"/>. A no-op when nothing is open.
    /// </summary>
    public async Task CloseAsync()
    {
        if (_socket is null)
        {
            return;
        }

        // Every field is taken and cleared before anything is disposed. A dispose that throws —
        // a zstd decompressor asked to finish a frame over a socket the peer has already reset —
        // would otherwise leave this client holding a half-closed connection that still reports
        // itself connected, session started, and authenticated. ReconnectAsync proceeds through a
        // failed close, and what it proceeds into has to be a clean slate.
        var reader = _reader;
        var stream = _stream;
        var socket = _socket;

        _reader = null;
        _fsm = null;
        _stream = null;
        _socket = null;
        IsClosed = true;
        IsAuthenticated = false;

        // The decompressor before the stream it reads through, so it is never asked to touch a
        // socket that has already gone. It is only ever a distinct object on a zstd session; on a
        // plain one the reader *is* the stream and disposing it here would double-dispose.
        if (reader is not null && !ReferenceEquals(reader, stream))
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }

        if (stream is not null)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            socket.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
            // The peer is already gone; there is nothing to shut down politely.
        }
        catch (ObjectDisposedException)
        {
            // Likewise.
        }

        socket.Dispose();
    }

    /// <summary>Closes the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Whether <paramref name="exception"/> is what an outstanding read or write turns into when
    /// the socket is disposed underneath it.
    /// </summary>
    /// <remarks>
    /// Which of these surfaces depends on how far the operation had got, and a disposed socket can
    /// also complete a read as end-of-stream — which <see cref="ControlChannel"/> reports as a
    /// gateway that hung up. Combined with a cancellation that has actually been requested, all
    /// four mean the same thing: we tore this connection down ourselves.
    /// </remarks>
    private static bool IsTornDown(Exception exception) =>
        exception is ObjectDisposedException or IOException or SocketException or LiveProtocolException;

    /// <summary>
    /// Whether <paramref name="exception"/> is the read budget elapsing rather than the caller
    /// cancelling or the stream failing.
    /// </summary>
    /// <remarks>
    /// The budget fires two ways at once — the linked token faulting the read, and the socket
    /// registration disposing the socket underneath it — and which one wins is a race. Both mean
    /// the same thing, so both are folded in here. The caller's own token is checked first
    /// because the linked source reports <c>IsCancellationRequested</c> for either cause, and a
    /// caller who cancelled deserves an <see cref="OperationCanceledException"/> rather than a
    /// timeout they did not experience.
    /// </remarks>
    private static bool IsReadBudgetElapsed(
        CancellationTokenSource timeout,
        Exception exception,
        CancellationToken cancellationToken) =>
        timeout.IsCancellationRequested
        && !cancellationToken.IsCancellationRequested
        && (exception is OperationCanceledException || IsTornDown(exception));

    /// <summary>
    /// Advances the state machine one step while it is still in the metadata phase.
    /// </summary>
    /// <returns><see langword="true"/> once the metadata block has been decoded.</returns>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="StartAsync"/> because <c>Process</c>'s second out-parameter is a
    /// <c>ref struct</c>, and keeping it out of an <c>async</c> method's state machine entirely
    /// is cheaper to reason about than relying on it not crossing an await.
    /// </para>
    /// <para>
    /// It cannot swallow the first record on the way past: the state machine reports
    /// <see cref="ProcessStatus.Metadata"/> from the step that decodes the block and only enters
    /// its record state afterwards, so the discarded record is always <see langword="default"/>.
    /// </para>
    /// </remarks>
    private static bool TryDecodeMetadata(DbnFsm fsm) =>
        fsm.Process(out _, out _) == ProcessStatus.Metadata;

    /// <summary>A NodaTime budget as the milliseconds <see cref="CancellationTokenSource"/> takes.</summary>
    private static int ToMilliseconds(Duration budget) =>
        checked((int)Math.Min(budget.TotalMilliseconds, int.MaxValue));

    private static Dictionary<string, string> ParseFields(string line)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in line.Split('|'))
        {
            // Upstream's parse_kv_pairs drops anything without an '=' rather than failing. The
            // success check below is what catches a response that parsed to nothing useful.
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator >= 0)
            {
                fields[pair[..separator]] = pair[(separator + 1)..];
            }
        }

        return fields;
    }

    /// <summary>
    /// The CRAM response: lowercase-hex <c>SHA256(challenge + "|" + apiKey)</c>.
    /// </summary>
    /// <remarks>
    /// The order of the two halves and the separator between them are the whole of the shared
    /// secret's protection, and every wrong version of them produces an equally well-formed
    /// 64-character digest. <c>MockLiveGateway</c> recomputes this rather than checking that the
    /// digest is hex, so a transposition fails in the test suite rather than against a real
    /// gateway.
    /// </remarks>
    private static string CramResponse(string challenge, string apiKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{challenge}|{apiKey}")));

    private string BuildAuthRequest(string challenge)
    {
        // Field order follows AuthRequest::new in protocol.rs. The gateway parses into a map and so
        // does not depend on it, which is exactly why this should not quietly reorder.
        var request = new StringBuilder()
            .Append("auth=").Append(CramResponse(challenge, ApiKey.Value))
            .Append('-').Append(ApiKey.BucketId)
            .Append("|dataset=").Append(Dataset)
            .Append("|encoding=").Append(DbnEncoding.Dbn.ToWireString())
            .Append("|compression=").Append(Compression.ToWireString())
            .Append("|ts_out=").Append(SendTsOut ? '1' : '0')
            .Append("|client=").Append(UserAgent.Value);

        if (HeartbeatInterval is { } interval)
        {
            // Integer division on the exact nanosecond count, not (long)TotalSeconds: the init
            // accessor has already rejected anything with a fraction, so this cannot truncate —
            // and it cannot pick up a rounding error from a double either.
            var seconds = interval.ToInt64Nanoseconds() / NodaConstants.NanosecondsPerSecond;
            request.Append("|heartbeat_interval_s=")
                .Append(seconds.ToString(CultureInfo.InvariantCulture));
        }

        if (SlowReaderBehavior is { } behavior)
        {
            request.Append("|slow_reader_behavior=").Append(behavior.ToWireString());
        }

        return request.ToString();
    }

    /// <summary>
    /// The next auto-assigned subscription id, counting from one as upstream does.
    /// </summary>
    /// <remarks>
    /// Upstream logs a warning at <c>u32::MAX</c> and then keeps handing out the same id, so two
    /// subscriptions share one and the gateway's errors become unattributable. This client has no
    /// logger to warn through, and a silently duplicated id is the confidently-wrong outcome
    /// rather than the safe one, so it stops instead. Four billion subscriptions on one client is
    /// not a thing that happens; a caller who genuinely needs more can set
    /// <see cref="Subscription.Id"/> themselves.
    /// </remarks>
    private uint NextSubscriptionId()
    {
        if (_subscriptionCounter == uint.MaxValue)
        {
            throw new InvalidOperationException(
                $"This client has assigned every subscription id up to {uint.MaxValue}. Set "
                + $"{nameof(Subscription)}.{nameof(Subscription.Id)} explicitly to keep going.");
        }

        return ++_subscriptionCounter;
    }

    private static string BuildSubscribeRequest(Subscription subscription, string symbols, bool isLast)
    {
        // Field order follows SubRequest::new in protocol.rs, for the same reason the auth line
        // does: the gateway parses into a map and so does not depend on it, which is exactly why
        // this should not quietly reorder.
        var request = new StringBuilder()
            .Append("schema=").Append(subscription.Schema.ToWireString())
            .Append("|stype_in=").Append(subscription.StypeIn.ToWireString())
            .Append("|symbols=").Append(symbols)
            .Append("|snapshot=").Append(subscription.UseSnapshot ? '1' : '0')
            .Append("|is_last=").Append(isLast ? '1' : '0');

        if (subscription.Start is { } start)
        {
            // DbnTime is the one crossing from NodaTime to wire nanoseconds, and it is exact:
            // an Instant carries true nanosecond precision, so a start of …0001 goes out as
            // …0001 rather than being rounded to a 100 ns tick boundary. CLAUDE.md, "Dates and
            // times".
            request.Append("|start=")
                .Append(DbnTime.ToUnixNanoseconds(start).ToString(CultureInfo.InvariantCulture));
        }

        // Always present: SubscribeAsync assigns one when the caller did not, so unlike upstream
        // there is no unidentified subscription to leave the field off for.
        request.Append("|id=")
            .Append(subscription.Id!.Value.ToString(CultureInfo.InvariantCulture));

        return request.ToString();
    }

    /// <summary>
    /// Drops the connection without the polite shutdown <see cref="CloseAsync"/> does. Used on
    /// every handshake failure, including the ones where the socket is already disposed because
    /// tearing it down is what ended the handshake.
    /// </summary>
    private void Teardown()
    {
        // Only ever a distinct object on a zstd session; on a plain one it is _stream itself.
        if (_reader is not null && !ReferenceEquals(_reader, _stream))
        {
            _reader.Dispose();
        }

        _reader = null;
        _fsm = null;

        _stream?.Dispose();
        _stream = null;

        _socket?.Dispose();
        _socket = null;

        IsAuthenticated = false;
    }

    /// <summary>
    /// Copies the next buffered record onto the heap, for the surfaces that cannot hold a
    /// <c>ref struct</c>.
    /// </summary>
    private bool TryNextOwnedRecord([NotNullWhen(true)] out OwnedRecord? record)
    {
        if (TryNextRecord(out var next))
        {
            record = OwnedRecord.CopyOf(next);
            return true;
        }

        record = null;
        return false;
    }
}
