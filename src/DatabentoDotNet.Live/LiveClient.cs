using System.Net;
using System.Net.Sockets;
using DatabentoDotNet.Dbn;
using NodaTime;

namespace DatabentoDotNet.Live;

/// <summary>
/// A client for Databento's live gateway: real-time market data, and intraday replay from the
/// same socket.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>live::Client</c> (<c>live/client.rs</c>). At this stage it opens the
/// connection and no more — the CRAM handshake is
/// <see href="https://github.com/jerbersoft/databentodotnet/issues/20">#20</see>, subscriptions
/// are #21, and the record loop is #22. Upstream's <c>build()</c> connects <em>and</em>
/// authenticates in one call; splitting them is what lets each of those land with tests of its
/// own against the mock gateway. There is deliberately no <c>NetworkStream</c> here yet either:
/// #20 is the first thing with a byte to write, and a stream created here would be constructed,
/// disposed, and never read — the shape of code that ships wrong because nothing exercises it.
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
/// </remarks>
public sealed class LiveClient : IAsyncDisposable
{
    /// <summary>The shortest heartbeat interval the gateway accepts.</summary>
    public static readonly Duration MinHeartbeatInterval = Duration.FromSeconds(5);

    /// <summary>The longest heartbeat interval the gateway accepts.</summary>
    public static readonly Duration MaxHeartbeatInterval = Duration.FromSeconds(1800);

    /// <summary>The connect budget used when none is set: ten seconds, matching upstream.</summary>
    public static readonly Duration DefaultConnectTimeout = Duration.FromSeconds(10);

    private readonly Duration? _heartbeatInterval;

    private Socket? _socket;

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
    /// The address <see cref="ConnectAsync"/> actually reached, once it has. Survives
    /// <see cref="CloseAsync"/> so a reconnect can reuse it rather than resolving DNS again.
    /// </summary>
    public IPEndPoint? Endpoint { get; private set; }

    /// <summary>Whether a socket is currently open.</summary>
    public bool IsConnected => _socket is not null;

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
        Endpoint = (IPEndPoint)socket.RemoteEndPoint!;
    }

    /// <summary>
    /// Closes the connection, keeping <see cref="Endpoint"/>. A no-op when nothing is open.
    /// </summary>
    public Task CloseAsync()
    {
        if (_socket is null)
        {
            return Task.CompletedTask;
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
        return Task.CompletedTask;
    }

    /// <summary>Closes the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
