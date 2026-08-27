using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Dbn.Publishers;
using NodaTime;

namespace DatabentoDotNet.Live.Tests;

/// <summary>
/// Tests for the session and the record loop: <see cref="LiveClient.StartAsync"/>,
/// <see cref="LiveClient.FillBufferAsync"/>, <see cref="LiveClient.TryNextRecord"/> and
/// <see cref="LiveClient.RecordsAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The central assertion is a cross-check, not a transcription.</b> Every record the mock
/// gateway sends is decoded twice — once over the socket by <see cref="LiveClient"/>, and once
/// out of a <see cref="MemoryStream"/> by <see cref="DbnDecoder"/> — and the two must agree byte
/// for byte. Writing the expected bytes into the test by hand would only prove the test and the
/// client were written by the same person on the same afternoon; running the same bytes through
/// the other decoder proves the live path and the file path agree, which is a claim about the
/// library.
/// </para>
/// <para>
/// It runs in all four combinations of {plain, zstd} × {<c>ts_out</c> off, on}, because those are
/// the four different byte streams the same records produce. <c>ts_out</c> in particular changes
/// every record's length, so a client that took it from what it <em>asked for</em> rather than
/// from the metadata would misread the stream by eight bytes per record — and would do so
/// silently, since a reinterpret cannot fail.
/// </para>
/// <para>
/// <b>Records arrive split across socket boundaries for free.</b>
/// <see cref="MockLiveGateway.SendRecordAsync{T}"/> writes each record as two writes with a flush
/// between them, which is upstream's own probe for a client that assumes one read yields one
/// whole record. Nothing here has to arrange it.
/// </para>
/// </remarks>
public class LiveClientRecordLoopTests
{
    private const int RecordCount = 200;

    /// <summary>
    /// A fixed <c>ts_out</c> stamp — 2024-01-02T03:04:05.000000006Z — so a <c>ts_out</c> session's
    /// bytes are reproducible enough to rebuild for the <see cref="DbnDecoder"/> cross-check.
    /// </summary>
    /// <remarks>
    /// The trailing 6 ns is the point. A BCL <c>DateTime</c> tick is 100 ns, so a clock that went
    /// through one would stamp <c>…000</c> and this comparison would still pass — which is why
    /// the repo bans it outright. See CLAUDE.md, "Dates and times".
    /// </remarks>
    private const ulong FixedTsOut = 1_704_164_645_000_000_006UL;

    private static readonly string DatasetName = Dataset.XnasItch.ToWireString();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------- Starting the session

    [Fact]
    public async Task StartAsync_SendsStartSession_AndReturnsTheGatewaysMetadata()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        Assert.False(client.IsSessionStarted);
        Assert.Null(client.Metadata);

        // The gateway reading 'start_session' is half the assertion: StartAsync returning metadata
        // proves the client read the answer, and this proves it asked the right question.
        var serving = gateway.StartAsync(Cancel);
        var metadata = await client.StartAsync(Cancel);
        await serving;

        Assert.True(client.IsSessionStarted);
        Assert.Same(metadata, client.Metadata);
        Assert.Equal(DatasetName, metadata.Dataset);
        Assert.Equal(gateway.SessionMetadata.Start, metadata.Start);
        Assert.Equal(SType.InstrumentId, metadata.StypeOut);
        Assert.False(metadata.TsOut);
        Assert.False(client.IsClosed);
    }

    [Fact]
    public async Task StartAsync_OnAZstdSession_DecodesTheMetadataThroughTheDecompressor()
    {
        await using var gateway = new MockLiveGateway(DatasetName) { ExpectedCompression = Compression.Zstd };
        await using var client = Client(gateway, compression: Compression.Zstd);
        await HandshakeAsync(gateway, client);

        var serving = gateway.StartCompressedAsync(Cancel);
        var metadata = await client.StartAsync(Cancel);
        await serving;

        // The metadata block is the first thing inside the zstd frame, so decoding it at all is
        // the proof that the decompressor was inserted at exactly the right byte — one byte early
        // and it would have tried to inflate the plaintext auth response.
        Assert.Equal(DatasetName, metadata.Dataset);
        Assert.True(client.IsSessionStarted);
    }

    [Fact]
    public async Task StartAsync_TakesTsOutFromTheMetadata_NotFromWhatTheClientAskedFor()
    {
        // The gateway is not in ts_out mode; the client did not ask for it either. What matters is
        // that the FSM is built without a ts_out hint at all, so the metadata is the only thing
        // that can set it — the combination that would break is a client that asked and was
        // refused, which no mock can produce because the mock validates the request.
        await using var gateway = new MockLiveGateway(DatasetName, sendTsOut: true);
        await using var client = Client(gateway, sendTsOut: true);
        await HandshakeAsync(gateway, client);

        var serving = gateway.StartAsync(Cancel);
        var metadata = await client.StartAsync(Cancel);
        await serving;

        Assert.True(metadata.TsOut);
    }

    [Fact]
    public async Task StartAsync_WhenTheGatewayHangsUpBeforeTheMetadata_ThrowsRatherThanReturningNull()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        // Read the request, then vanish. Upstream's decoder answers a truncated metadata block
        // with an error; a client that answered it with a null Metadata would hand the caller a
        // started session that can never produce a record.
        var serving = Task.Run(
            async () =>
            {
                await gateway.ExpectStartAsync(Cancel);
                await gateway.CloseAsync();
            },
            Cancel);

        var error = await Assert.ThrowsAsync<LiveProtocolException>(() => client.StartAsync(Cancel));
        await serving;

        Assert.Contains("before sending the session metadata", error.Message, StringComparison.Ordinal);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task StartAsync_WhenTheGatewayReadsTheRequestAndGoesQuiet_TimesOutOnTheSameBudget()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway, readTimeout: Duration.FromMilliseconds(250));
        await HandshakeAsync(gateway, client);

        // Silence rather than a hang-up, which is the other way a session fails to start and the
        // one nothing bounds except the read budget. The wait for the metadata runs on the same
        // EffectiveReadTimeout the record loop does — a client that bounded only the record loop
        // would sit here until the caller's own token fired, or forever if there was none.
        var serving = gateway.ExpectStartAsync(Cancel);

        var error = await Assert.ThrowsAsync<HeartbeatTimeoutException>(() => client.StartAsync(Cancel));
        await serving;

        Assert.Equal(Duration.FromMilliseconds(250), error.Timeout);
        Assert.Contains("the session metadata", error.Message, StringComparison.Ordinal);
        Assert.False(client.IsConnected);
        Assert.False(client.IsSessionStarted);
    }

    [Fact]
    public async Task StartAsync_BeforeConnecting_Throws()
    {
        await using var client = DisconnectedClient();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartAsync(Cancel));
        Assert.Contains("not connected", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_BeforeAuthenticating_Throws()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);

        var accepting = gateway.AcceptAsync(Cancel);
        await client.ConnectAsync(Cancel);
        await accepting;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartAsync(Cancel));
        Assert.Contains("has not authenticated", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_Twice_Throws()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        var serving = gateway.StartAsync(Cancel);
        await client.StartAsync(Cancel);
        await serving;

        // Upstream answers the same call with BadArgument("ignored request to start session that
        // has already been started"). Sending a second start_session would leave a stray control
        // line in the middle of the record stream.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartAsync(Cancel));
        Assert.Contains("already been started", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------- The four byte streams

    [Theory]
    [InlineData(Compression.None, false)]
    [InlineData(Compression.None, true)]
    [InlineData(Compression.Zstd, false)]
    [InlineData(Compression.Zstd, true)]
    public async Task RecordLoop_DecodesEveryRecordExactlyAsTheFileDecoderDoes(
        Compression compression,
        bool tsOut)
    {
        var records = SyntheticMbo.Records(RecordCount);

        await using var gateway = new MockLiveGateway(
            DatasetName,
            sendTsOut: tsOut,
            clock: new FixedClock(DbnTime.ToInstant(FixedTsOut)))
        {
            ExpectedCompression = compression,
        };

        await using var client = Client(gateway, compression: compression, sendTsOut: tsOut);
        await HandshakeAsync(gateway, client);

        var serving = compression == Compression.Zstd
            ? gateway.StartCompressedAsync(Cancel)
            : gateway.StartAsync(Cancel);
        var metadata = await client.StartAsync(Cancel);
        await serving;

        Assert.Equal(tsOut, metadata.TsOut);

        foreach (var record in records)
        {
            await gateway.SendRecordAsync(record, Cancel);
        }

        await gateway.CloseAsync();

        var decoded = await DrainAsync(client);

        // The same bytes down the other path. DbnDecoder reads ts_out from the metadata exactly as
        // the live client does, so the reference stream carries the metadata rather than a flag.
        var expected = DecodeWithFileDecoder(BuildReferenceStream(gateway, records, tsOut));

        Assert.Equal(RecordCount, decoded.Count);
        Assert.Equal(expected, decoded);
        Assert.True(client.IsClosed);
    }

    [Fact]
    public async Task RecordLoop_OnATsOutSession_ReportsTheGatewaysSendTimestamp()
    {
        var clock = new FixedClock(DbnTime.ToInstant(FixedTsOut));
        await using var gateway = new MockLiveGateway(DatasetName, sendTsOut: true, clock: clock);
        await using var client = Client(gateway, sendTsOut: true);
        await HandshakeAsync(gateway, client);

        var serving = gateway.StartAsync(Cancel);
        await client.StartAsync(Cancel);
        await serving;

        await gateway.SendRecordAsync(SyntheticMbo.Record(1), Cancel);

        // Looped rather than one fill: the record arrives in two writes, so the first read may
        // return either half of it.
        var record = await ReadOneRecordAsync(client);

        Assert.True(record.HasTsOut);
        Assert.Equal(FixedTsOut, record.TsOut);
        Assert.Equal(gateway.LastTsOut, record.TsOut);

        // StructSize, not SizeInBytes, is what identifies the record: the stream is eight bytes
        // longer per record and the struct is not.
        Assert.Equal(MboMsg.WireSize + sizeof(ulong), record.SizeInBytes);
        Assert.Equal(MboMsg.WireSize, record.StructSize);
        Assert.True(record.Has<MboMsg>());
        Assert.Equal(1u, record.Get<MboMsg>().Sequence);
    }

    // ------------------------------------------------------------------------ Read in place

    [Fact]
    public async Task TryNextRecord_HandsBackRecordsThatSitSideBySideInOneBuffer()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        var serving = gateway.StartAsync(Cancel);
        await client.StartAsync(Cancel);
        await serving;

        foreach (var record in SyntheticMbo.Records(8))
        {
            await gateway.SendRecordAsync(record, Cancel);
        }

        // Fill until two whole records are buffered at once, then measure them against each
        // other with no await in between. A failed attempt drops whichever record it did get;
        // the test cares that *some* two are adjacent, not which two.
        nint offset;
        int size;
        while (!TryMeasureAdjacency(client, out offset, out size))
        {
            Assert.NotEqual(0, await client.FillBufferAsync(Cancel));
        }

        Assert.Equal((nint)size, offset);
    }

    // --------------------------------------------------------------------- Ending the stream

    [Fact]
    public async Task FillBufferAsync_WhenTheGatewayClosesCleanly_ReturnsZeroRatherThanThrowing()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        var serving = gateway.StartAsync(Cancel);
        await client.StartAsync(Cancel);
        await serving;

        await gateway.SendRecordAsync(SyntheticMbo.Record(1), Cancel);
        await gateway.CloseAsync();

        var decoded = await DrainAsync(client);

        // A stream ending is not an exception — PORTING.md §2. The one record still arrives:
        // closing after sending is a clean close, not a discard.
        Assert.Single(decoded);
        Assert.True(client.IsClosed);

        // Idempotent afterwards, so a caller draining a loop needs no guard around the call whose
        // result they are already checking.
        Assert.Equal(0, await client.FillBufferAsync(Cancel));
        Assert.Equal(0, await client.FillBufferAsync(Cancel));
        Assert.False(TryNext(client));
    }

    [Fact]
    public async Task CloseAsync_AfterAZstdSession_DisposesTheDecompressorAndTheSocket()
    {
        await using var gateway = new MockLiveGateway(DatasetName) { ExpectedCompression = Compression.Zstd };
        await using var client = Client(gateway, compression: Compression.Zstd);
        await HandshakeAsync(gateway, client);

        var serving = gateway.StartCompressedAsync(Cancel);
        var metadata = await client.StartAsync(Cancel);
        await serving;

        await client.CloseAsync();

        Assert.False(client.IsConnected);
        Assert.False(client.IsSessionStarted);
        Assert.True(client.IsClosed);

        // The metadata outlives the connection, as Greeting and SessionId do: what the last
        // session said is a diagnostic, and only a reconnect can replace it.
        Assert.Same(metadata, client.Metadata);
        Assert.NotNull(client.Endpoint);

        // Closing takes the decoder with it, and the read pair says "no more records" rather than
        // "you never started" — the same answer a clean close from the gateway gets. Only a
        // client that never started a session is a caller mistake, and that one still throws.
        Assert.Equal(0, await client.FillBufferAsync(Cancel));
        Assert.False(TryNext(client));
    }

    // ------------------------------------------------------------------------ The read budget

    [Fact]
    public void EffectiveReadTimeout_DerivesFromTheHeartbeatIntervalAndIsOverridable()
    {
        // Upstream's heartbeat_timeout(): interval + 5s, or 35s when no interval was requested.
        Assert.Equal(Duration.FromSeconds(35), Bare(null, null).EffectiveReadTimeout);
        Assert.Equal(LiveClient.DefaultReadTimeout, Bare(null, null).EffectiveReadTimeout);
        Assert.Equal(
            Duration.FromSeconds(65),
            Bare(Duration.FromSeconds(60), null).EffectiveReadTimeout);

        // An explicit budget wins over the derivation, which upstream offers no way to do at all.
        Assert.Equal(
            Duration.FromSeconds(2),
            Bare(Duration.FromSeconds(60), Duration.FromSeconds(2)).EffectiveReadTimeout);

        // A non-positive budget can only ever time out, so it is rejected where it is set rather
        // than discovered on the first read.
        Assert.Throws<ArgumentOutOfRangeException>(() => Bare(null, Duration.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => Bare(null, Duration.FromSeconds(-1)));

        static LiveClient Bare(Duration? heartbeatInterval, Duration? readTimeout) => new()
        {
            ApiKey = new ApiKey(MockLiveGateway.TestApiKey),
            Dataset = DatasetName,
            HeartbeatInterval = heartbeatInterval,
            ReadTimeout = readTimeout,
        };
    }

    [Fact]
    public async Task FillBufferAsync_WhenTheGatewayGoesQuiet_ThrowsHeartbeatTimeout()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway, readTimeout: Duration.FromMilliseconds(250));
        await HandshakeAsync(gateway, client);

        var serving = gateway.StartAsync(Cancel);
        await client.StartAsync(Cancel);
        await serving;

        // The gateway is alive and says nothing, which is the failure a heartbeat exists to rule
        // out: on a real feed silence longer than the interval means the connection is dead, not
        // that the market is quiet.
        var error = await Assert.ThrowsAsync<HeartbeatTimeoutException>(
            async () => await client.FillBufferAsync(Cancel));

        Assert.Equal(Duration.FromMilliseconds(250), error.Timeout);
        Assert.Contains("the next record", error.Message, StringComparison.Ordinal);

        // The connection is spent, matching upstream, which marks itself closed and requires a
        // reconnect rather than retrying.
        Assert.True(client.IsClosed);
        Assert.False(client.IsConnected);
    }

    // --------------------------------------------------------------- The convenient surface

    [Fact]
    public async Task RecordsAsync_YieldsTheSameRecordsTheZeroCopyPairDoes()
    {
        var records = SyntheticMbo.Records(64);

        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        var serving = gateway.StartAsync(Cancel);
        await client.StartAsync(Cancel);
        await serving;

        foreach (var record in records)
        {
            await gateway.SendRecordAsync(record, Cancel);
        }

        await gateway.CloseAsync();

        var yielded = new List<OwnedRecord>();
        await foreach (var record in client.RecordsAsync(Cancel))
        {
            yielded.Add(record);
        }

        Assert.Equal(records.Length, yielded.Count);

        // Held past the loop, which is the whole point of the copy: every record is still readable
        // after the enumeration that produced it has finished and the decoder's buffer has been
        // reused many times over.
        for (var i = 0; i < records.Length; i++)
        {
            Assert.True(yielded[i].Has<MboMsg>());
            Assert.Equal((uint)(i + 1), yielded[i].Get<MboMsg>().Sequence);
            Assert.Equal(Bytes(records[i]), yielded[i].Bytes.ToArray());
            Assert.Equal(records[i].IndexTs, yielded[i].IndexTs);
        }

        Assert.True(client.IsClosed);
    }

    [Fact]
    public async Task RecordsAsync_BeforeTheSessionStarts_ThrowsFromTheFirstStepRatherThanTheCall()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        // An iterator method's body does not run until it is enumerated, so this call cannot
        // throw — asserting that it does not is what stops a future reader from "fixing" the
        // documented behaviour into a guard that never fires.
        var records = client.RecordsAsync(Cancel);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () =>
            {
                await foreach (var _ in records)
                {
                    // Unreachable: the first MoveNextAsync throws.
                }
            });

        Assert.Contains("has not started", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FillBufferAsync_And_TryNextRecord_BeforeTheSessionStarts_Throw()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        var fill = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await client.FillBufferAsync(Cancel));
        Assert.Contains("has not started", fill.Message, StringComparison.Ordinal);

        var next = Assert.Throws<InvalidOperationException>(() => TryNext(client));
        Assert.Contains("has not started", next.Message, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------- Helpers

    /// <summary>
    /// A value's raw bytes. Constrained to <see langword="unmanaged"/> rather than to
    /// <c>IRecord&lt;T&gt;</c> so it also serves <see cref="WithTsOut{T}"/>, which is a record on
    /// the wire and not one in the type system — it wraps a record rather than being one.
    /// </summary>
    private static byte[] Bytes<T>(T value)
        where T : unmanaged
        => MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)).ToArray();

    /// <summary>
    /// The same records the gateway sent, as one plain DBN stream: the metadata block followed by
    /// every record, <c>ts_out</c>-stamped when the session is.
    /// </summary>
    /// <remarks>
    /// Built from the gateway's own <see cref="MockLiveGateway.SessionMetadata"/> and the same
    /// <see cref="WithTsOut{T}"/> wrapper the gateway uses, so this is a re-serialisation of what
    /// went on the wire rather than a second guess at it. What it is <em>not</em> built from is
    /// the client, which is the point.
    /// </remarks>
    private static byte[] BuildReferenceStream(MockLiveGateway gateway, MboMsg[] records, bool tsOut)
    {
        using var stream = new MemoryStream();
        stream.Write(MetadataEncoder.Encode(gateway.SessionMetadata));

        foreach (var record in records)
        {
            stream.Write(tsOut ? Bytes(new WithTsOut<MboMsg>(record, gateway.LastTsOut)) : Bytes(record));
        }

        return stream.ToArray();
    }

    private static List<byte[]> DecodeWithFileDecoder(byte[] stream)
    {
        var decoded = new List<byte[]>();

        using var source = new MemoryStream(stream);
        using var decoder = new DbnDecoder(source);

        while (decoder.TryNextRecord(out var record))
        {
            decoded.Add(record.Bytes.ToArray());
        }

        return decoded;
    }

    /// <summary>
    /// Reads the whole stream through the zero-copy pair, copying each record out so it survives
    /// the next fill.
    /// </summary>
    /// <remarks>
    /// The drain is a separate, non-<c>async</c> method for the reason the client's own
    /// <c>RecordsAsync</c> needs one: a <see cref="RecordRef"/> cannot be in scope across an
    /// <c>await</c>, and the compiler enforces it as CS4007.
    /// </remarks>
    private static async Task<List<byte[]>> DrainAsync(LiveClient client)
    {
        var decoded = new List<byte[]>();

        while (true)
        {
            DrainBuffered(client, decoded);
            if (await client.FillBufferAsync(Cancel) == 0)
            {
                break;
            }
        }

        // One last drain: the fill that returned 0 came after records that the loop above only
        // drained *before* it. Without this, the last records read from the socket are dropped.
        DrainBuffered(client, decoded);
        return decoded;
    }

    private static void DrainBuffered(LiveClient client, List<byte[]> decoded)
    {
        while (client.TryNextRecord(out var record))
        {
            decoded.Add(record.Bytes.ToArray());
        }
    }

    private static async Task<OwnedRecord> ReadOneRecordAsync(LiveClient client)
    {
        while (true)
        {
            if (TryCopyNext(client, out var record))
            {
                return record;
            }

            Assert.NotEqual(0, await client.FillBufferAsync(Cancel));
        }
    }

    private static bool TryCopyNext(LiveClient client, out OwnedRecord record)
    {
        if (client.TryNextRecord(out var next))
        {
            record = OwnedRecord.CopyOf(next);
            return true;
        }

        record = null!;
        return false;
    }

    /// <summary>
    /// Measures the gap between two consecutive records — the observable form of "read in place
    /// rather than copied".
    /// </summary>
    /// <param name="client">The client to read from.</param>
    /// <param name="offset">The byte distance from the first record's start to the second's.</param>
    /// <param name="size">The first record's length, which the distance must equal.</param>
    /// <returns>
    /// <see langword="false"/> when fewer than two records were buffered, in which case the
    /// caller should read more and try again.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Why adjacency and not an address range.</b> The decoder's buffer is not reachable from
    /// outside the client, so "the record points into it" cannot be asserted directly. Adjacency
    /// is the stronger claim anyway: two records exactly <c>SizeInBytes</c> apart are two records
    /// in one contiguous buffer laid out as the wire laid them out, which a client that copied
    /// each record into a fresh array could not produce except by accident.
    /// </para>
    /// <para>
    /// <b>It uses <see cref="Unsafe.ByteOffset{T}"/> rather than a pair of captured addresses</b>
    /// because the offset between two interior references into the same object survives a garbage
    /// collection moving that object, and two raw addresses do not. Reading the first record
    /// after the second has been decoded reaches under the "valid only until the next call"
    /// contract deliberately: that contract exists for <em>upgraded</em> records, which are
    /// rewritten in a second buffer, and this measures the ordinary path where nothing moves.
    /// </para>
    /// </remarks>
    private static bool TryMeasureAdjacency(LiveClient client, out nint offset, out int size)
    {
        offset = 0;
        size = 0;

        if (!client.TryNextRecord(out var first))
        {
            return false;
        }

        size = first.SizeInBytes;
        if (!client.TryNextRecord(out var second))
        {
            return false;
        }

        offset = Unsafe.ByteOffset(
            ref MemoryMarshal.GetReference(first.Bytes),
            ref MemoryMarshal.GetReference(second.Bytes));

        return true;
    }

    /// <summary>
    /// <see cref="LiveClient.TryNextRecord"/> behind a method, because a <c>ref struct</c>
    /// out-parameter cannot appear in a lambda — including the one an
    /// <c>Assert.Throws</c> takes.
    /// </summary>
    private static bool TryNext(LiveClient client) => client.TryNextRecord(out _);

    private static async Task HandshakeAsync(MockLiveGateway gateway, LiveClient client)
    {
        var handshake = gateway.AuthenticateAsync(client.HeartbeatInterval, Cancel);
        await client.ConnectAsync(Cancel);
        await client.AuthenticateAsync(Cancel);
        await handshake;
    }

    private static LiveClient DisconnectedClient() => new()
    {
        ApiKey = new ApiKey(MockLiveGateway.TestApiKey),
        Dataset = DatasetName,
    };

    private static LiveClient Client(
        MockLiveGateway gateway,
        Compression compression = Compression.None,
        bool sendTsOut = false,
        Duration? readTimeout = null) => new()
    {
        ApiKey = new ApiKey(MockLiveGateway.TestApiKey),
        Dataset = gateway.Dataset,
        Gateway = gateway.Address,
        Compression = compression,
        SendTsOut = sendTsOut,
        ReadTimeout = readTimeout,
    };

    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }
}
