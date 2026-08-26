using System.Globalization;
using System.Net;
using System.Net.Sockets;
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
/// A hand-written client that speaks the client half of the live protocol, used to drive
/// <see cref="MockLiveGateway"/> in the harness's own tests.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately <em>not</em> the library's live client — that arrives in
/// <see href="https://github.com/jerbersoft/databentodotnet/issues/19">#19</see>, and a harness
/// verified against the thing it exists to verify proves nothing. It is written straight from
/// <c>databento-rs/src/live/protocol.rs</c>, and it stays here afterwards as the second opinion:
/// when a later issue's client disagrees with the gateway, this says which of the two moved.
/// </para>
/// <para>
/// <b>It reads control lines one byte at a time.</b> The very next thing it reads after
/// <c>start_session</c> is DBN — possibly inside a zstd frame — so a buffered line reader that
/// pulled eight bytes too many would swallow the front of the metadata with no way to give it
/// back. Three short lines make the cost of the syscalls irrelevant. The gateway's own reader
/// buffers freely, because it never reads anything but lines; see
/// <see cref="GatewayLineReader"/>.
/// </para>
/// </remarks>
public sealed class StubLiveClient : IAsyncDisposable
{
    /// <summary>The user agent this stub reports, so <c>client=</c> is present and non-empty.</summary>
    public const string UserAgent = "DatabentoDotNet.Live.Tests stub";

    private readonly TcpClient _connection = new();
    private readonly byte[] _single = new byte[1];

    private NetworkStream? _stream;
    private Stream? _decompressor;

    /// <summary>
    /// How long any single wait for the gateway may take. Defaults to ten seconds, matching
    /// <see cref="MockLiveGateway.Timeout"/>, so a test that deadlocks fails from both ends
    /// instead of hanging the run.
    /// </summary>
    public Duration Timeout { get; init; } = Duration.FromSeconds(10);

    /// <summary>The greeting line read at the start of the handshake.</summary>
    public string? Greeting { get; private set; }

    /// <summary>The CRAM challenge read at the start of the handshake, without its <c>cram=</c> prefix.</summary>
    public string? Challenge { get; private set; }

    /// <summary>Connects to <paramref name="address"/>.</summary>
    /// <param name="address">The gateway endpoint.</param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    public async Task ConnectAsync(IPEndPoint address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);

        await _connection.ConnectAsync(address, cancellationToken).ConfigureAwait(false);
        _connection.NoDelay = true;
        _stream = _connection.GetStream();
    }

    /// <summary>
    /// Connects, then runs the whole CRAM handshake and returns the gateway's parsed auth
    /// response.
    /// </summary>
    /// <param name="address">The gateway endpoint.</param>
    /// <param name="apiKey">The API key to authenticate with.</param>
    /// <param name="dataset">The dataset to request, in its wire spelling.</param>
    /// <param name="compression">The compression to request for the record stream.</param>
    /// <param name="sendTsOut">Whether to ask the gateway to stamp every record with its send time.</param>
    /// <param name="heartbeatInterval">The heartbeat interval to request, or <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The gateway's auth response, parsed into its key-value pairs.</returns>
    public async Task<IReadOnlyDictionary<string, string>> ConnectAndAuthenticateAsync(
        IPEndPoint address,
        string apiKey,
        string dataset,
        Compression compression = Compression.None,
        bool sendTsOut = false,
        Duration? heartbeatInterval = null,
        CancellationToken cancellationToken = default)
    {
        await ConnectAsync(address, cancellationToken).ConfigureAwait(false);

        Greeting = await ReadLineAsync(cancellationToken).ConfigureAwait(false);

        var challengeLine = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (!challengeLine.StartsWith("cram=", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected a 'cram=' challenge, got: '{challengeLine}'.");
        }

        Challenge = challengeLine["cram=".Length..];

        await SendLineAsync(
            BuildAuthLine(Challenge, apiKey, dataset, compression, sendTsOut, heartbeatInterval),
            cancellationToken).ConfigureAwait(false);

        var response = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        return ParseFields(response);
    }

    /// <summary>
    /// Builds an authentication request line, without its terminator.
    /// </summary>
    /// <remarks>
    /// Public and static so a test can build a well-formed line and then break exactly one thing
    /// about it — an uppercased digest, a wrong bucket, a dropped field — which is how
    /// <see cref="MockLiveGateway"/>'s own assertions are shown to fail rather than merely to pass.
    /// </remarks>
    /// <param name="challenge">The challenge the gateway issued.</param>
    /// <param name="apiKey">The API key.</param>
    /// <param name="dataset">The dataset, in its wire spelling.</param>
    /// <param name="compression">The compression to request.</param>
    /// <param name="sendTsOut">Whether to request <c>ts_out</c>.</param>
    /// <param name="heartbeatInterval">The heartbeat interval to request, or <see langword="null"/> for none.</param>
    /// <returns>The line.</returns>
    public static string BuildAuthLine(
        string challenge,
        string apiKey,
        string dataset,
        Compression compression = Compression.None,
        bool sendTsOut = false,
        Duration? heartbeatInterval = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(apiKey);

        var bucket = apiKey[^MockLiveGateway.BucketIdLength..];
        var line = new StringBuilder()
            .Append("auth=").Append(CramResponse(challenge, apiKey)).Append('-').Append(bucket)
            .Append("|dataset=").Append(dataset)
            .Append("|encoding=").Append(DbnEncoding.Dbn.ToWireString())
            .Append("|compression=").Append(compression.ToWireString())
            .Append("|ts_out=").Append(sendTsOut ? '1' : '0')
            .Append("|client=").Append(UserAgent);

        if (heartbeatInterval is { } interval)
        {
            var seconds = interval.ToInt64Nanoseconds() / NodaConstants.NanosecondsPerSecond;
            line.Append("|heartbeat_interval_s=").Append(seconds.ToString(CultureInfo.InvariantCulture));
        }

        return line.ToString();
    }

    /// <summary>
    /// The CRAM response: lowercase-hex <c>sha256(challenge + "|" + apiKey)</c>.
    /// </summary>
    /// <param name="challenge">The challenge the gateway issued.</param>
    /// <param name="apiKey">The API key.</param>
    /// <returns>The 64-character lowercase hex digest.</returns>
    public static string CramResponse(string challenge, string apiKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{challenge}|{apiKey}")));

    /// <summary>
    /// Sends one subscription line, without chunking — the caller decides what one chunk is.
    /// </summary>
    /// <param name="schema">The schema to subscribe to.</param>
    /// <param name="stypeIn">The input symbology.</param>
    /// <param name="symbols">The symbols for this one line.</param>
    /// <param name="isLast">Whether this is the last chunk of the subscription.</param>
    /// <param name="id">The subscription id.</param>
    /// <param name="useSnapshot">Whether to request an initial snapshot.</param>
    /// <param name="start">The intraday-replay start time, or <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public Task SubscribeAsync(
        Schema schema,
        SType stypeIn,
        IReadOnlyList<string> symbols,
        bool isLast,
        uint id,
        bool useSnapshot = false,
        Instant? start = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        // Field order follows SubRequest::new in protocol.rs. The gateway parses into a map and so
        // does not depend on it, which is exactly why the stub should not quietly reorder: a real
        // gateway might.
        var line = new StringBuilder()
            .Append("schema=").Append(schema.ToWireString())
            .Append("|stype_in=").Append(stypeIn.ToWireString())
            .Append("|symbols=").AppendJoin(',', symbols)
            .Append("|snapshot=").Append(useSnapshot ? '1' : '0')
            .Append("|is_last=").Append(isLast ? '1' : '0');

        if (start is { } startTime)
        {
            line.Append("|start=")
                .Append(DbnTime.ToUnixNanoseconds(startTime).ToString(CultureInfo.InvariantCulture));
        }

        line.Append("|id=").Append(id.ToString(CultureInfo.InvariantCulture));

        return SendLineAsync(line.ToString(), cancellationToken);
    }

    /// <summary>Sends <c>start_session</c>.</summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    public Task StartSessionAsync(CancellationToken cancellationToken = default) =>
        SendLineAsync(MockLiveGateway.StartSession, cancellationToken);

    /// <summary>
    /// Switches record reads to a zstd decoder, for a session the gateway started with
    /// <see cref="MockLiveGateway.StartCompressedAsync"/>.
    /// </summary>
    /// <remarks>
    /// Safe to call at exactly this point and no other: everything before it was read a byte at a
    /// time, so not one byte of the frame has been consumed yet.
    /// </remarks>
    public void BeginCompressed()
    {
        if (_decompressor is not null)
        {
            throw new InvalidOperationException("The record stream is already being decompressed.");
        }

        _decompressor = new ZstdSharp.DecompressionStream(RequireStream(), leaveOpen: true);
    }

    /// <summary>Reads the DBN metadata block that opens the record stream.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The decoded metadata, exactly as it arrived — no version upgrade.</returns>
    public async Task<Metadata> ReadMetadataAsync(CancellationToken cancellationToken = default)
    {
        var prelude = await ReadExactlyAsync(DbnConstants.MetadataPreludeLength, cancellationToken)
            .ConfigureAwait(false);
        MetadataDecoder.DecodePrelude(prelude, out _, out var length);

        var body = await ReadExactlyAsync(length, cancellationToken).ConfigureAwait(false);

        var block = new byte[prelude.Length + body.Length];
        prelude.CopyTo(block, 0);
        body.CopyTo(block, prelude.Length);

        // AsIs, not UpgradeToV3: this stub reports what the gateway sent, and an upgrade would let
        // a mismatch in the harness's own metadata be rewritten into agreement.
        return MetadataDecoder.Decode(block, VersionUpgradePolicy.AsIs);
    }

    /// <summary>
    /// Reads one record: its length byte first, then the rest of it. Works unchanged for a
    /// <c>ts_out</c> stream, because the header length already covers the extra eight bytes.
    /// </summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The record's bytes, header included.</returns>
    public async Task<byte[]> ReadRecordAsync(CancellationToken cancellationToken = default)
    {
        var lengthByte = await ReadExactlyAsync(1, cancellationToken).ConfigureAwait(false);
        var size = lengthByte[0] * DbnConstants.RecordLengthMultiplier;
        if (size < DbnConstants.RecordLengthMultiplier)
        {
            throw new InvalidOperationException($"A record cannot be {size} bytes long.");
        }

        var rest = await ReadExactlyAsync(size - 1, cancellationToken).ConfigureAwait(false);

        var record = new byte[size];
        record[0] = lengthByte[0];
        rest.CopyTo(record, 1);
        return record;
    }

    /// <summary>Reads one control line, without its terminator, one byte at a time.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The line.</returns>
    public async Task<string> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        var stream = RequireStream();
        using var timeout = StartTimeout(cancellationToken);
        var line = new StringBuilder();

        while (true)
        {
            var read = await stream.ReadAsync(_single.AsMemory(0, 1), timeout.Token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"The gateway closed the connection {line.Length} bytes into a control line: '{line}'.");
            }

            if (_single[0] == (byte)'\n')
            {
                return line.ToString();
            }

            line.Append((char)_single[0]);
        }
    }

    /// <summary>Sends one line verbatim, appending the terminator.</summary>
    /// <param name="line">The line, without a terminator.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task SendLineAsync(string line, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(line);

        var stream = RequireStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + '\n'), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Closes the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_decompressor is not null)
        {
            await _decompressor.DisposeAsync().ConfigureAwait(false);
            _decompressor = null;
        }

        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }

        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Stream Input => _decompressor ?? RequireStream();

    private NetworkStream RequireStream() =>
        _stream ?? throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

    private async Task<byte[]> ReadExactlyAsync(int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        if (count == 0)
        {
            return buffer;
        }

        using var timeout = StartTimeout(cancellationToken);
        await Input.ReadExactlyAsync(buffer, timeout.Token).ConfigureAwait(false);
        return buffer;
    }

    private CancellationTokenSource StartTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(checked((int)Timeout.TotalMilliseconds));
        return timeout;
    }

    private static Dictionary<string, string> ParseFields(string line)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in line.Split('|'))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator >= 0)
            {
                fields[pair[..separator]] = pair[(separator + 1)..];
            }
        }

        return fields;
    }
}
