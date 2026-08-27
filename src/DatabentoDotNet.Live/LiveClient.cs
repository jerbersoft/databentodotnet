using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DatabentoDotNet.Dbn;
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
/// <c>live::protocol::Protocol</c>. At this stage it connects and authenticates — subscriptions
/// are #21 and the record loop is #22. Upstream's <c>build()</c> connects <em>and</em>
/// authenticates in one call; splitting them is what lets each land with tests of its own against
/// the mock gateway, and it is what makes <see cref="ConnectTimeoutException"/> and
/// <see cref="AuthTimeoutException"/> nameable as the separate failures they are.
/// </para>
/// <para>
/// <b>No builder.</b> Upstream's <c>ClientBuilder&lt;AK, D&gt;</c> is generic type-state whose
/// only purpose is to make "no API key" and "no dataset" unrepresentable — <c>build()</c> exists
/// only on <c>ClientBuilder&lt;ApiKey, String&gt;</c>. C# 11 <c>required</c> init properties do
/// exactly that natively, checked by the compiler at every construction site. See PORTING.md §2.
/// </para>
/// <para>
/// <b><see cref="Endpoint"/> survives <see cref="CloseAsync"/>, on purpose.</b> Upstream's
/// <c>reconnect()</c> reuses the already-resolved <c>peer_addr</c> and does not re-resolve DNS
/// (PORTING.md §4), so the resolved address has to outlive the socket it came from. #23 is what
/// consumes that.
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

    /// <summary>The prefix the gateway's challenge line must carry.</summary>
    private const string ChallengePrefix = "cram=";

    private readonly Duration? _heartbeatInterval;

    private Socket? _socket;
    private NetworkStream? _stream;

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

    /// <summary>Whether a socket is currently open.</summary>
    public bool IsConnected => _socket is not null;

    /// <summary>Whether the handshake on the current connection has succeeded.</summary>
    public bool IsAuthenticated { get; private set; }

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

        var endPoint = Gateway ?? LiveGateway.For(Dataset);

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
    /// Closes the connection, keeping <see cref="Endpoint"/>. A no-op when nothing is open.
    /// </summary>
    public async Task CloseAsync()
    {
        if (_socket is null)
        {
            return;
        }

        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }

        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
            // The peer is already gone; there is nothing to shut down politely.
        }
        catch (ObjectDisposedException)
        {
            // Likewise.
        }

        _socket.Dispose();
        _socket = null;
        IsAuthenticated = false;
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
    /// Drops the connection without the polite shutdown <see cref="CloseAsync"/> does. Used on
    /// every handshake failure, including the ones where the socket is already disposed because
    /// tearing it down is what ended the handshake.
    /// </summary>
    private void Teardown()
    {
        _stream?.Dispose();
        _stream = null;

        _socket?.Dispose();
        _socket = null;

        IsAuthenticated = false;
    }
}
