using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

// System.Text.Encoding and DatabentoDotNet.Dbn.Encoding collide by simple name. Aliasing both
// keeps `encoding=dbn` sourced from the enum's own wire string rather than a literal that
// nothing would notice drifting.
using DbnEncoding = DatabentoDotNet.Dbn.Encoding;
using Encoding = System.Text.Encoding;
using DatabentoDotNet.Dbn;
using NodaTime;

namespace DatabentoDotNet.Live.Tests;

/// <summary>
/// A stand-in for the Databento live gateway: it speaks the gateway half of the line protocol
/// over a real loopback socket, asserts that the client speaks its half correctly, and then emits
/// DBN metadata and records.
/// </summary>
/// <remarks>
/// <para>
/// Ported from upstream's <c>MockGateway</c> (<c>databento-rs/src/live/client.rs:661</c>), which
/// CLAUDE.md names as the shape to port rather than invent. It goes in before any of
/// <see href="https://github.com/jerbersoft/databentodotnet/issues/10">#10</see>'s other pieces
/// because nothing below it is testable without it, and a harness grown ad hoc inside whichever
/// issue needed it first would be shaped by that one caller.
/// </para>
/// <para>
/// <b>No <c>Fixture</c>, no channel.</b> Upstream wraps the gateway in a <c>Fixture</c> that owns
/// a spawned task and takes commands over an unbounded channel. That indirection buys Rust a way
/// to drive both halves of a socket from one test; .NET does not need it. A test here starts the
/// client's leg, awaits the gateway's, and joins — every step after the handshake is a write that
/// completes without waiting for a reply, so the rest reads as straight-line code.
/// </para>
/// <para>
/// <b>Every failure is a <see cref="MockGatewayException"/> naming the offending line</b>, not a
/// bare assert. See that type for why.
/// </para>
/// <para>
/// <b>Deviations from upstream, each deliberate:</b> the CRAM response is checked against the
/// digest this gateway computes itself rather than only for hex-ness, so a client that hashes the
/// challenge and key in the wrong order fails here instead of passing; <c>compression=</c> is
/// checked at all; the metadata carries <c>ts_out</c> matching <see cref="SendTsOut"/>, which a
/// real gateway does and upstream's builder does not;
/// <see cref="SendRecordAsync{T}(T, CancellationToken)"/> appends the <c>ts_out</c> stamp itself
/// rather than leaving the caller to wrap the record; and <see cref="StartCompressedAsync"/>
/// installs the zstd encoder as this object's own output rather than handing it back, so the same
/// <see cref="SendRecordAsync{T}(T, CancellationToken)"/> call works in both modes.
/// </para>
/// <para>
/// <b>Not covered by a test of its own:</b> every record goes out as two writes with a flush
/// between them, which is upstream's probe for clients that assume one read yields one whole
/// record. Asserting the split from the receiving end would mean asserting how TCP segmented a
/// loopback write, which is not a thing a test can depend on. Its value shows up in the clients
/// tested against this harness, not here.
/// </para>
/// </remarks>
public sealed class MockLiveGateway : IAsyncDisposable
{
    /// <summary>
    /// The 32-character test API key, shared with upstream's test module so a failure here and a
    /// failure there can be compared directly. Its last five characters — <see cref="TestBucketId"/>
    /// — are the bucket id, and they are legible on purpose.
    /// </summary>
    public const string TestApiKey = "32-character-with-lots-of-filler";

    /// <summary>The bucket id derived from <see cref="TestApiKey"/>: its last five characters.</summary>
    public const string TestBucketId = "iller";

    /// <summary>
    /// The fixed CRAM challenge this gateway issues, again matching upstream. Fixed rather than
    /// random so the expected digest is a constant a test can pin.
    /// </summary>
    public const string Challenge = "t7kNhwj4xqR0QYjzFKtBEG2ec2pXJ4FK";

    /// <summary>The greeting line, sent before the challenge.</summary>
    public const string Greeting = "lsg-test";

    /// <summary>The session id this gateway reports on a successful authentication.</summary>
    public const string SessionId = "5";

    /// <summary>The line a client sends to end configuration and begin the record stream.</summary>
    public const string StartSession = "start_session";

    /// <summary>The number of characters of an API key that form its bucket id.</summary>
    public const int BucketIdLength = 5;

    private const int Sha256HexLength = 64;

    private readonly TcpListener _listener;
    private readonly IClock _clock;
    private readonly string _expectedApiKey = TestApiKey;

    private TcpClient? _connection;
    private NetworkStream? _stream;
    private GatewayLineReader? _reader;
    private Stream? _encoder;

    /// <summary>
    /// Binds a listener on an ephemeral loopback port. Nothing is accepted until
    /// <see cref="AcceptAsync"/> or <see cref="AuthenticateAsync"/> is called.
    /// </summary>
    /// <param name="dataset">
    /// The dataset this gateway serves, in its wire spelling — <c>Dataset.XnasItch.ToWireString()</c>,
    /// not the enum. The gateway is a protocol-level double and the wire carries a string; taking
    /// one keeps a test free to send a dataset name the enum does not have.
    /// </param>
    /// <param name="sendTsOut">
    /// Whether this session appends the gateway's send timestamp to every record. Checked against
    /// the client's <c>ts_out=</c> at authentication, reported in the session metadata, and acted
    /// on by <see cref="SendRecordAsync{T}(T, CancellationToken)"/>.
    /// </param>
    /// <param name="clock">
    /// Where the session start time and every <c>ts_out</c> stamp come from. Defaults to
    /// <see cref="SystemClock.Instance"/>; pass a fixed clock to make a <c>ts_out</c> assertion
    /// exact.
    /// </param>
    public MockLiveGateway(string dataset, bool sendTsOut = false, IClock? clock = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataset);

        Dataset = dataset;
        SendTsOut = sendTsOut;
        _clock = clock ?? SystemClock.Instance;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();

        SessionMetadata = new Metadata
        {
            Version = DbnConstants.Version,
            Dataset = dataset,
            Schema = null,
            Start = DbnTime.ToUnixNanoseconds(_clock.GetCurrentInstant()),
            StypeIn = null,
            StypeOut = SType.InstrumentId,
            TsOut = sendTsOut,
            SymbolCstrLength = Metadata.SymbolCstrLengthForVersion(DbnConstants.Version),
        };
    }

    /// <summary>The dataset this gateway serves, in its wire spelling.</summary>
    public string Dataset { get; }

    /// <summary>Whether this session appends the gateway's send timestamp to every record.</summary>
    public bool SendTsOut { get; }

    /// <summary>The loopback endpoint a client should connect to.</summary>
    public IPEndPoint Address => (IPEndPoint)_listener.LocalEndpoint;

    /// <summary>
    /// The metadata <see cref="StartAsync"/> and <see cref="StartCompressedAsync"/> send. Built in
    /// the constructor from the dataset and the clock; replace it with an object initializer to
    /// serve a stream with symbol mappings, a schema, or a different DBN version.
    /// </summary>
    public Metadata SessionMetadata { get; init; }

    /// <summary>
    /// The key whose digest and bucket id the client's authentication must match. Defaults to
    /// <see cref="TestApiKey"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The key is shorter than <see cref="BucketIdLength"/>, so it has no bucket id.
    /// </exception>
    public string ExpectedApiKey
    {
        get => _expectedApiKey;
        init
        {
            ArgumentException.ThrowIfNullOrEmpty(value);
            if (value.Length < BucketIdLength)
            {
                throw new ArgumentException(
                    $"An API key needs at least {BucketIdLength} characters to have a bucket id; "
                    + $"'{value}' has {value.Length}.",
                    nameof(value));
            }

            _expectedApiKey = value;
        }
    }

    /// <summary>
    /// The compression the client must request. Defaults to <see cref="Compression.None"/>; set it
    /// to <see cref="Compression.Zstd"/> for a session that will be started with
    /// <see cref="StartCompressedAsync"/>.
    /// </summary>
    public Compression ExpectedCompression { get; init; } = Compression.None;

    /// <summary>
    /// The exact <c>client=</c> user agent the client must send, or <see langword="null"/> to
    /// require only that it sends a non-empty one.
    /// </summary>
    /// <remarks>
    /// Upstream compares against its crate-level <c>USER_AGENT</c>. This repo has no user agent
    /// until <see href="https://github.com/jerbersoft/databentodotnet/issues/19">#19</see> defines
    /// one, and a harness that hard-coded a guess at it would have to be edited when the real one
    /// lands. Presence is checked either way, so a client that omits the field still fails.
    /// </remarks>
    public string? ExpectedClient { get; init; }

    /// <summary>
    /// The exact <c>slow_reader_behavior=</c> value the client must send, or
    /// <see langword="null"/> to require that it sends none.
    /// </summary>
    /// <remarks>
    /// A string rather than an enum: <c>SlowReaderBehavior</c> belongs to
    /// <see href="https://github.com/jerbersoft/databentodotnet/issues/23">#23</see>, and this
    /// harness ships before it. The string is the wire form, which is what the gateway sees.
    /// </remarks>
    public string? ExpectedSlowReaderBehavior { get; init; }

    /// <summary>
    /// How long any single wait for the client may take before it fails as a
    /// <see cref="MockGatewayException"/>. Defaults to ten seconds.
    /// </summary>
    /// <remarks>
    /// A mock gateway that blocks forever is the worst failure a test suite can have: the run
    /// hangs and the reason is invisible. Every read and the accept are bounded, and the timeout
    /// message names what the gateway was waiting for.
    /// </remarks>
    public Duration Timeout { get; init; } = Duration.FromSeconds(10);

    /// <summary>
    /// The <c>ts_out</c> stamp on the most recent record, or zero if none has been sent or
    /// <see cref="SendTsOut"/> is <see langword="false"/>.
    /// </summary>
    public ulong LastTsOut { get; private set; }

    /// <summary>Accepts one connection, without exchanging anything on it.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <exception cref="InvalidOperationException">A connection is already open.</exception>
    /// <exception cref="MockGatewayException">No client connected within <see cref="Timeout"/>.</exception>
    public async Task AcceptAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is not null)
        {
            throw new InvalidOperationException(
                "A connection is already open. Call CloseAsync before accepting another.");
        }

        using var timeout = StartTimeout(cancellationToken);
        try
        {
            _connection = await _listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MockGatewayException(
                $"Timed out after {Timeout} waiting for a client to connect to {Address}.");
        }

        _connection.NoDelay = true;
        _stream = _connection.GetStream();
        _reader = new GatewayLineReader(_stream);
    }

    /// <summary>
    /// Accepts a connection and runs the whole CRAM handshake: greeting, challenge, the client's
    /// authentication request, and the success response.
    /// </summary>
    /// <param name="heartbeatInterval">
    /// The heartbeat interval the client must have requested, or <see langword="null"/> to
    /// require that it requested none.
    /// </param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The parsed fields of the client's authentication request.</returns>
    /// <exception cref="MockGatewayException">The client's authentication request is wrong.</exception>
    public async Task<IReadOnlyDictionary<string, string>> AuthenticateAsync(
        Duration? heartbeatInterval = null,
        CancellationToken cancellationToken = default)
    {
        var fields = await ExpectAuthenticationAsync(heartbeatInterval, cancellationToken).ConfigureAwait(false);

        await SendAsync($"success=1|session_id={SessionId}", cancellationToken).ConfigureAwait(false);
        return fields;
    }

    /// <summary>
    /// Runs the handshake up to and including the client's authentication request, and stops
    /// there without answering it.
    /// </summary>
    /// <remarks>
    /// The seam a rejection test needs: the response is then whatever the test sends with
    /// <see cref="SendAsync"/> — <c>success=0|error=…</c>, a response with no <c>success</c> field
    /// at all, or nothing, followed by <see cref="CloseAsync"/>. The client's request is still
    /// validated in full, so a test about the <em>response</em> cannot quietly pass while the
    /// client is sending a malformed request.
    /// </remarks>
    /// <param name="heartbeatInterval">
    /// The heartbeat interval the client must have requested, or <see langword="null"/> to
    /// require that it requested none.
    /// </param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The parsed fields of the client's authentication request.</returns>
    /// <exception cref="MockGatewayException">The client's authentication request is wrong.</exception>
    public async Task<IReadOnlyDictionary<string, string>> ExpectAuthenticationAsync(
        Duration? heartbeatInterval = null,
        CancellationToken cancellationToken = default)
    {
        await AcceptAsync(cancellationToken).ConfigureAwait(false);
        await SendAsync(Greeting, cancellationToken).ConfigureAwait(false);
        await SendAsync($"cram={Challenge}", cancellationToken).ConfigureAwait(false);

        var line = await ReadLineAsync("an authentication request", cancellationToken).ConfigureAwait(false);
        var fields = ParseFields(line, "authentication request");
        ValidateAuth(fields, line, heartbeatInterval);
        return fields;
    }

    /// <summary>
    /// Reads one subscription line and checks it against <paramref name="subscription"/>.
    /// </summary>
    /// <param name="subscription">
    /// What this one line must say. For a subscription the client chunks, this is one chunk — see
    /// <see cref="ExpectedSubscription"/>.
    /// </param>
    /// <param name="isLast">Whether this line must be the last chunk (<c>is_last=1</c>).</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The parsed fields of the subscription line.</returns>
    /// <exception cref="MockGatewayException">The line does not match.</exception>
    public async Task<IReadOnlyDictionary<string, string>> ExpectSubscribeAsync(
        ExpectedSubscription subscription,
        bool isLast,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        var line = await ReadLineAsync("a subscription request", cancellationToken).ConfigureAwait(false);
        var fields = ParseFields(line, "subscription request");

        RequireValue(fields, "schema", subscription.Schema.ToWireString(), line);
        RequireValue(fields, "stype_in", subscription.StypeIn.ToWireString(), line);
        RequireValue(fields, "symbols", subscription.SymbolsWireValue, line);
        RequireValue(fields, "snapshot", subscription.UseSnapshot ? "1" : "0", line);
        RequireValue(fields, "is_last", isLast ? "1" : "0", line);

        if (subscription.Start is { } start)
        {
            RequireValue(fields, "start", Text(DbnTime.ToUnixNanoseconds(start)), line);
        }
        else
        {
            RequireAbsent(fields, "start", line);
        }

        var id = Require(fields, "id", line);
        if (subscription.Id is { } expectedId)
        {
            RequireValue(fields, "id", Text(expectedId), line);
        }
        else if (!uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            throw new MockGatewayException($"id='{id}' is not a subscription id, in: '{line}'.");
        }

        return fields;
    }

    /// <summary>
    /// Reads <c>start_session</c> and sends <see cref="SessionMetadata"/> uncompressed. Every
    /// record after this goes out uncompressed too.
    /// </summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <exception cref="MockGatewayException">The client sent something other than <c>start_session</c>.</exception>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await ExpectStartSessionAsync(cancellationToken).ConfigureAwait(false);
        await WriteToOutputAsync(MetadataEncoder.Encode(SessionMetadata), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads <c>start_session</c>, wraps the socket in a zstd encoder, and sends
    /// <see cref="SessionMetadata"/> through it. Every record after this is compressed too, so the
    /// same <see cref="SendRecordAsync{T}(T, CancellationToken)"/> serves both modes.
    /// </summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <exception cref="InvalidOperationException">
    /// <see cref="ExpectedCompression"/> is not <see cref="Compression.Zstd"/>, so
    /// <see cref="AuthenticateAsync"/> already required the client to ask for something else.
    /// </exception>
    /// <exception cref="MockGatewayException">The client sent something other than <c>start_session</c>.</exception>
    public async Task StartCompressedAsync(CancellationToken cancellationToken = default)
    {
        if (ExpectedCompression != Compression.Zstd)
        {
            throw new InvalidOperationException(
                "StartCompressedAsync frames the session in zstd, but ExpectedCompression is "
                + $"{ExpectedCompression}, so AuthenticateAsync required the client to request "
                + $"compression={ExpectedCompression.ToWireString()}. Set ExpectedCompression to "
                + "Compression.Zstd.");
        }

        await ExpectStartSessionAsync(cancellationToken).ConfigureAwait(false);

        // leaveOpen: the socket outlives the frame. CloseAsync disposes this encoder first, which
        // is what writes the frame epilogue.
        _encoder = new ZstdSharp.CompressionStream(RequireStream(), leaveOpen: true);
        await WriteToOutputAsync(MetadataEncoder.Encode(SessionMetadata), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one control line, always uncompressed.
    /// </summary>
    /// <remarks>
    /// Control lines are the plaintext part of the protocol: the <c>compression=</c> a client
    /// negotiates in its authentication request applies to the DBN stream that follows
    /// <c>start_session</c>, never to the greeting, the challenge, or the auth response.
    /// </remarks>
    /// <param name="line">The line, without a terminator — this method appends it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ArgumentException"><paramref name="line"/> already contains a newline.</exception>
    public async Task SendAsync(string line, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "SendAsync sends exactly one line and appends the terminator itself, so the line "
                + $"must not contain one: '{line}'.",
                nameof(line));
        }

        var stream = RequireStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + '\n'), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one record, appending the gateway's send timestamp when <see cref="SendTsOut"/> is
    /// set, and compressing it when the session was started with
    /// <see cref="StartCompressedAsync"/>.
    /// </summary>
    /// <remarks>
    /// The record goes out as two writes with a flush between them — upstream's probe for a client
    /// that assumes one read yields one whole record.
    /// </remarks>
    /// <typeparam name="T">The record type.</typeparam>
    /// <param name="record">The record. Its header length is fixed up for <c>ts_out</c> if needed.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public Task SendRecordAsync<T>(T record, CancellationToken cancellationToken = default)
        where T : unmanaged, IRecord<T>
    {
        if (!SendTsOut)
        {
            return SendRecordBytesAsync(ToBytes(in record), cancellationToken);
        }

        LastTsOut = DbnTime.ToUnixNanoseconds(_clock.GetCurrentInstant());

        // WithTsOut's constructor rewrites hd.length to cover the extra eight bytes. Appending the
        // stamp by hand instead would leave every record on the stream one word short, which
        // desynchronises the client from the record after it rather than failing here.
        var stamped = new WithTsOut<T>(record, LastTsOut);
        return SendRecordBytesAsync(ToBytes(in stamped), cancellationToken);
    }

    /// <summary>
    /// Closes the current connection, leaving the listener bound so a client can reconnect to the
    /// same <see cref="Address"/>. A no-op when nothing is connected.
    /// </summary>
    public async Task CloseAsync()
    {
        if (_encoder is not null)
        {
            // Disposing the encoder writes the zstd frame epilogue. Doing it after the socket is
            // gone would throw instead.
            await _encoder.DisposeAsync().ConfigureAwait(false);
            _encoder = null;
        }

        if (_connection is not null)
        {
            try
            {
                // A clean FIN, so the client reads end-of-stream rather than a connection reset.
                _connection.Client.Shutdown(SocketShutdown.Send);
            }
            catch (SocketException)
            {
                // The client is already gone; there is nothing to shut down politely.
            }
            catch (ObjectDisposedException)
            {
                // Likewise.
            }
        }

        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }

        _connection?.Dispose();
        _connection = null;
        _reader = null;
    }

    /// <summary>Closes the connection and releases the listener.</summary>
    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _listener.Dispose();
        GC.SuppressFinalize(this);
    }

    private Stream Output => _encoder ?? RequireStream();

    private NetworkStream RequireStream() =>
        _stream ?? throw new InvalidOperationException(
            "No client is connected. Call AcceptAsync or AuthenticateAsync first.");

    private GatewayLineReader RequireReader() =>
        _reader ?? throw new InvalidOperationException(
            "No client is connected. Call AcceptAsync or AuthenticateAsync first.");

    private async Task ExpectStartSessionAsync(CancellationToken cancellationToken)
    {
        var line = await ReadLineAsync($"'{StartSession}'", cancellationToken).ConfigureAwait(false);
        if (!string.Equals(line, StartSession, StringComparison.Ordinal))
        {
            throw new MockGatewayException($"Expected '{StartSession}', got: '{line}'.");
        }
    }

    private async Task<string> ReadLineAsync(string expecting, CancellationToken cancellationToken)
    {
        var reader = RequireReader();
        using var timeout = StartTimeout(cancellationToken);
        try
        {
            return await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MockGatewayException(
                $"Timed out after {Timeout} waiting for the client to send {expecting}.");
        }
    }

    private async Task SendRecordBytesAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        var half = bytes.Length / 2;
        var output = Output;

        await output.WriteAsync(bytes.AsMemory(0, half), cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(bytes.AsMemory(half), cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteToOutputAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        var output = Output;
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private CancellationTokenSource StartTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(checked((int)Timeout.TotalMilliseconds));
        return timeout;
    }

    private void ValidateAuth(
        IReadOnlyDictionary<string, string> fields,
        string line,
        Duration? heartbeatInterval)
    {
        var auth = Require(fields, "auth", line);

        // The digest is hex, so it holds no '-'; the first one is the bucket separator. Upstream
        // splits the same way.
        var separator = auth.IndexOf('-', StringComparison.Ordinal);
        if (separator < 0)
        {
            throw new MockGatewayException(
                $"auth='{auth}' has no '-{{bucket_id}}' suffix, in: '{line}'.");
        }

        var digest = auth[..separator];
        var bucket = auth[(separator + 1)..];

        if (digest.Length != Sha256HexLength || !IsLowercaseHex(digest))
        {
            throw new MockGatewayException(
                $"Expected the CRAM response to be {Sha256HexLength} lowercase hex digits, got "
                + $"'{digest}' ({digest.Length} characters), in: '{line}'.");
        }

        // Stronger than upstream, which only checks that the digest is hex. Checking the value
        // catches a client that hashes the key and the challenge in the wrong order, or joins them
        // with the wrong separator — both of which produce a perfectly well-formed digest.
        var expectedDigest = Sha256Hex($"{Challenge}|{ExpectedApiKey}");
        if (!string.Equals(digest, expectedDigest, StringComparison.Ordinal))
        {
            throw new MockGatewayException(
                $"Wrong CRAM response. Expected sha256(\"{Challenge}|<api key>\") = "
                + $"{expectedDigest}, got {digest}.");
        }

        var expectedBucket = ExpectedApiKey[^BucketIdLength..];
        if (!string.Equals(bucket, expectedBucket, StringComparison.Ordinal))
        {
            throw new MockGatewayException(
                $"Expected bucket id '{expectedBucket}' — the last {BucketIdLength} characters of "
                + $"the API key — got '{bucket}', in: '{line}'.");
        }

        RequireValue(fields, "dataset", Dataset, line);
        RequireValue(fields, "encoding", DbnEncoding.Dbn.ToWireString(), line);
        RequireValue(fields, "compression", ExpectedCompression.ToWireString(), line);
        RequireValue(fields, "ts_out", SendTsOut ? "1" : "0", line);

        if (ExpectedClient is { } expectedClient)
        {
            RequireValue(fields, "client", expectedClient, line);
        }
        else if (Require(fields, "client", line).Length == 0)
        {
            throw new MockGatewayException($"client= is empty, in: '{line}'.");
        }

        if (heartbeatInterval is { } interval)
        {
            RequireValue(fields, "heartbeat_interval_s", Text(WholeSeconds(interval)), line);
        }
        else
        {
            RequireAbsent(fields, "heartbeat_interval_s", line);
        }

        if (ExpectedSlowReaderBehavior is { } behavior)
        {
            RequireValue(fields, "slow_reader_behavior", behavior, line);
        }
        else
        {
            RequireAbsent(fields, "slow_reader_behavior", line);
        }
    }

    private static Dictionary<string, string> ParseFields(string line, string what)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in line.Split('|'))
        {
            // Upstream's parse_kv_pairs drops anything without an '=' rather than failing, and the
            // per-field checks below catch what that hides.
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator >= 0)
            {
                fields[pair[..separator]] = pair[(separator + 1)..];
            }
        }

        if (fields.Count == 0)
        {
            throw new MockGatewayException($"The {what} carried no key=value pairs: '{line}'.");
        }

        return fields;
    }

    private static string Require(IReadOnlyDictionary<string, string> fields, string key, string line)
        => fields.TryGetValue(key, out var value)
            ? value
            : throw new MockGatewayException($"Missing '{key}=', in: '{line}'.");

    private static void RequireValue(
        IReadOnlyDictionary<string, string> fields,
        string key,
        string expected,
        string line)
    {
        var actual = Require(fields, key, line);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new MockGatewayException(
                $"Expected '{key}={expected}', got '{key}={actual}', in: '{line}'.");
        }
    }

    private static void RequireAbsent(IReadOnlyDictionary<string, string> fields, string key, string line)
    {
        if (fields.TryGetValue(key, out var value))
        {
            throw new MockGatewayException(
                $"Expected no '{key}=' field, got '{key}={value}', in: '{line}'.");
        }
    }

    private static bool IsLowercaseHex(string value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    // Integer division on the exact nanosecond count, not (long)Duration.TotalSeconds: the same
    // truncation toward zero as Rust's whole_seconds(), with no double in the middle of it.
    private static long WholeSeconds(Duration duration) =>
        duration.ToInt64Nanoseconds() / NodaConstants.NanosecondsPerSecond;

    private static string Text<T>(T value)
        where T : IFormattable
        => value.ToString(null, CultureInfo.InvariantCulture);

    private static byte[] ToBytes<T>(in T value)
        where T : unmanaged
        => MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)).ToArray();
}
