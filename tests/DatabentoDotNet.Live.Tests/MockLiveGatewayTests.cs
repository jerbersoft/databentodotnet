using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Dbn.Publishers;
using NodaTime;

namespace DatabentoDotNet.Live.Tests;

/// <summary>
/// Tests for <see cref="MockLiveGateway"/> — the harness the rest of M2 is tested against.
/// </summary>
/// <remarks>
/// <para>
/// A test double that silently accepts a wrong client is worse than no double at all: every issue
/// downstream of <see href="https://github.com/jerbersoft/databentodotnet/issues/18">#18</see>
/// would go green against it. So roughly half of what follows drives the gateway with a
/// deliberately malformed client and asserts that it <em>rejects</em> — a non-hex digest, an
/// uppercased one, the right digest under the wrong key, a missing <c>is_last</c>, a heartbeat
/// interval that was never requested.
/// </para>
/// <para>
/// The other half is the whole session end to end, in both plain and zstd modes, against
/// <see cref="StubLiveClient"/> — a client written from <c>live/protocol.rs</c> rather than from
/// this gateway, so the two agree only if both match the protocol.
/// </para>
/// <para>
/// Every awaited call carries <see cref="TestContext"/>'s cancellation token. Both sides already
/// bound themselves with a ten-second timeout, but a cancelled run should stop at once rather
/// than wait one of those out per test.
/// </para>
/// </remarks>
public class MockLiveGatewayTests
{
    private static readonly string DatasetName = Dataset.XnasItch.ToWireString();

    private static readonly string[] FirstChunk = ["MSFT", "TSLA", "QQQ"];
    private static readonly string[] LastChunk = ["AAPL", "NVDA"];

    /// <summary>
    /// A key whose bucket id is the same five characters as <see cref="MockLiveGateway.TestApiKey"/>'s,
    /// so a digest computed from it fails on the digest and on nothing else.
    /// </summary>
    private const string DecoyApiKey = "another-32-char-key-ending-iller";

    /// <summary>
    /// <c>sha256("t7kNhwj4xqR0QYjzFKtBEG2ec2pXJ4FK|32-character-with-lots-of-filler")</c>, computed
    /// outside this codebase. Pinning it is what stops the gateway and the stub from agreeing on
    /// the same wrong CRAM construction: they share a specification, not an implementation, and
    /// this is the third opinion.
    /// </summary>
    private const string KnownCramResponse =
        "42e2c6c6a874b2f4498bcaf0541be406901a23adef1fb2843d5426f6d2387d14";

    /// <summary>
    /// A <c>ts_out</c> with non-zero nanoseconds. A BCL <c>DateTime</c> tick is 100 ns, so anything
    /// that routed this through one would hand back <c>…100</c> instead of <c>…123</c>.
    /// </summary>
    private const ulong FixedTsOut = 1_609_160_400_000_000_123UL;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void TestApiKey_IsThirtyTwoCharactersEndingInTheTestBucketId()
    {
        Assert.Equal(32, MockLiveGateway.TestApiKey.Length);
        Assert.Equal(
            MockLiveGateway.TestBucketId,
            MockLiveGateway.TestApiKey[^MockLiveGateway.BucketIdLength..]);

        // The decoy differs from the test key everywhere but the bucket id.
        Assert.Equal(32, DecoyApiKey.Length);
        Assert.Equal(MockLiveGateway.TestBucketId, DecoyApiKey[^MockLiveGateway.BucketIdLength..]);
        Assert.NotEqual(MockLiveGateway.TestApiKey, DecoyApiKey);
    }

    [Fact]
    public void CramResponse_MatchesAKnownAnswer()
    {
        Assert.Equal(
            KnownCramResponse,
            StubLiveClient.CramResponse(MockLiveGateway.Challenge, MockLiveGateway.TestApiKey));
    }

    [Fact]
    public async Task Address_IsAnEphemeralLoopbackPort()
    {
        await using var gateway = new MockLiveGateway(DatasetName);

        Assert.Equal(IPAddress.Loopback, gateway.Address.Address);
        Assert.NotEqual(0, gateway.Address.Port);
    }

    [Fact]
    public async Task Session_PlainMode_RunsTheWholeProtocolAgainstAHandWrittenClient()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = new StubLiveClient();

        var clientTask = client.ConnectAndAuthenticateAsync(
            gateway.Address, MockLiveGateway.TestApiKey, DatasetName, cancellationToken: Cancel);
        var authFields = await gateway.AuthenticateAsync(cancellationToken: Cancel);
        var authResponse = await clientTask;

        Assert.Equal(MockLiveGateway.Greeting, client.Greeting);
        Assert.Equal(MockLiveGateway.Challenge, client.Challenge);
        Assert.Equal("1", authResponse["success"]);
        Assert.Equal(MockLiveGateway.SessionId, authResponse["session_id"]);
        Assert.Equal(KnownCramResponse + "-" + MockLiveGateway.TestBucketId, authFields["auth"]);
        Assert.Equal(DatasetName, authFields["dataset"]);
        Assert.Equal("0", authFields["ts_out"]);
        Assert.Equal(StubLiveClient.UserAgent, authFields["client"]);

        // One subscription over two lines, which is what chunking looks like on the wire: only the
        // last carries is_last=1.
        await client.SubscribeAsync(
            Schema.Trades, SType.RawSymbol, FirstChunk, isLast: false, id: 1, cancellationToken: Cancel);
        await gateway.ExpectSubscribeAsync(Expect(FirstChunk, id: 1), isLast: false, Cancel);

        await client.SubscribeAsync(
            Schema.Trades, SType.RawSymbol, LastChunk, isLast: true, id: 1, cancellationToken: Cancel);
        await gateway.ExpectSubscribeAsync(Expect(LastChunk, id: 1), isLast: true, Cancel);

        await client.StartSessionAsync(Cancel);
        await gateway.StartAsync(Cancel);

        AssertSessionMetadata(gateway, await client.ReadMetadataAsync(Cancel));

        var first = Trade(instrumentId: 17, tsRecv: 1_688_428_800_000_000_000UL, price: 123_456_789);
        var second = Trade(instrumentId: 18, tsRecv: 1_688_428_800_000_000_001UL, price: 987_654_321);
        await gateway.SendRecordAsync(first, Cancel);
        await gateway.SendRecordAsync(second, Cancel);

        Assert.Equal(Bytes(first), await client.ReadRecordAsync(Cancel));
        Assert.Equal(Bytes(second), await client.ReadRecordAsync(Cancel));
    }

    [Fact]
    public async Task Session_ZstdMode_RunsTheWholeProtocolThroughACompressedStream()
    {
        await using var gateway = new MockLiveGateway(DatasetName)
        {
            ExpectedCompression = Compression.Zstd,
        };
        await using var client = new StubLiveClient();

        var clientTask = client.ConnectAndAuthenticateAsync(
            gateway.Address,
            MockLiveGateway.TestApiKey,
            DatasetName,
            Compression.Zstd,
            cancellationToken: Cancel);
        var authFields = await gateway.AuthenticateAsync(cancellationToken: Cancel);
        await clientTask;

        Assert.Equal(Compression.Zstd.ToWireString(), authFields["compression"]);

        // Chunked here too: the whole session runs the same way in both modes, and compression
        // starts only after start_session, so a subscription that worked in plain mode failing
        // here would mean the framing had leaked backwards into the control lines.
        await client.SubscribeAsync(
            Schema.Mbo, SType.RawSymbol, FirstChunk, isLast: false, id: 1, cancellationToken: Cancel);
        await gateway.ExpectSubscribeAsync(
            Expect(FirstChunk, id: 1) with { Schema = Schema.Mbo }, isLast: false, Cancel);

        await client.SubscribeAsync(
            Schema.Mbo, SType.RawSymbol, LastChunk, isLast: true, id: 1, cancellationToken: Cancel);
        await gateway.ExpectSubscribeAsync(
            Expect(LastChunk, id: 1) with { Schema = Schema.Mbo }, isLast: true, Cancel);

        await client.StartSessionAsync(Cancel);
        await gateway.StartCompressedAsync(Cancel);
        client.BeginCompressed();

        AssertSessionMetadata(gateway, await client.ReadMetadataAsync(Cancel));

        var first = Trade(instrumentId: 17, tsRecv: 1_688_428_800_000_000_000UL, price: 123_456_789);
        var second = Trade(instrumentId: 18, tsRecv: 1_688_428_800_000_000_001UL, price: 987_654_321);
        await gateway.SendRecordAsync(first, Cancel);
        await gateway.SendRecordAsync(second, Cancel);

        Assert.Equal(Bytes(first), await client.ReadRecordAsync(Cancel));
        Assert.Equal(Bytes(second), await client.ReadRecordAsync(Cancel));
    }

    [Fact]
    public async Task SendRecordAsync_TsOutMode_StampsTheRecordAndGrowsItsHeaderLength()
    {
        var clock = new FixedClock(DbnTime.ToInstant(FixedTsOut));
        await using var gateway = new MockLiveGateway(DatasetName, sendTsOut: true, clock: clock);
        await using var client = await HandshakeAsync(gateway, sendTsOut: true);

        await client.StartSessionAsync(Cancel);
        await gateway.StartAsync(Cancel);

        var metadata = await client.ReadMetadataAsync(Cancel);
        Assert.True(metadata.TsOut);

        var record = Trade(instrumentId: 17, tsRecv: 1_688_428_800_000_000_000UL, price: 42);
        await gateway.SendRecordAsync(record, Cancel);

        var received = await client.ReadRecordAsync(Cancel);

        Assert.Equal(TradeMsg.WireSize + sizeof(ulong), received.Length);

        // The length byte has to grow with the record, or the client desynchronises on the record
        // after this one rather than failing here.
        Assert.Equal(
            (byte)((TradeMsg.WireSize + sizeof(ulong)) / DbnConstants.RecordLengthMultiplier),
            received[0]);

        Assert.Equal(
            FixedTsOut,
            BinaryPrimitives.ReadUInt64LittleEndian(received.AsSpan(TradeMsg.WireSize)));
        Assert.Equal(FixedTsOut, gateway.LastTsOut);

        // Everything but that length byte is the record it was handed, untouched.
        Assert.Equal(Bytes(record)[1..], received[1..TradeMsg.WireSize]);
    }

    [Fact]
    public async Task SendRecordAsync_WithoutTsOut_LeavesTheRecordExactlyAsItWasHanded()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = await HandshakeAsync(gateway);

        await client.StartSessionAsync(Cancel);
        await gateway.StartAsync(Cancel);
        await client.ReadMetadataAsync(Cancel);

        var record = Trade(instrumentId: 17, tsRecv: 1_688_428_800_000_000_000UL, price: 42);
        await gateway.SendRecordAsync(record, Cancel);

        Assert.Equal(Bytes(record), await client.ReadRecordAsync(Cancel));
        Assert.Equal(0UL, gateway.LastTsOut);
    }

    // ------------------------------------------------------------------ authentication rejects

    [Fact]
    public async Task AuthenticateAsync_NonHexDigest_Fails()
    {
        var error = await ExpectAuthRejectedAsync(
            new MockLiveGateway(DatasetName),
            line => "auth=z" + line["auth=z".Length..]);

        Assert.Contains("lowercase hex", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticateAsync_UppercasedDigest_Fails()
    {
        // A gateway comparing case-insensitively would take this, and a real one does not.
        var error = await ExpectAuthRejectedAsync(
            new MockLiveGateway(DatasetName),
            line => line.Replace(
                KnownCramResponse, KnownCramResponse.ToUpperInvariant(), StringComparison.Ordinal));

        Assert.Contains("lowercase hex", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticateAsync_DigestOfADifferentKey_Fails()
    {
        var error = await ExpectAuthRejectedAsync(
            new MockLiveGateway(DatasetName),
            line => line.Replace(
                KnownCramResponse,
                StubLiveClient.CramResponse(MockLiveGateway.Challenge, DecoyApiKey),
                StringComparison.Ordinal));

        Assert.Contains("Wrong CRAM response", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticateAsync_WrongBucketSuffix_Fails()
    {
        var error = await ExpectAuthRejectedAsync(
            new MockLiveGateway(DatasetName),
            line => line.Replace(
                "-" + MockLiveGateway.TestBucketId + "|", "-abcde|", StringComparison.Ordinal));

        Assert.Contains("bucket id", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticateAsync_NoBucketSuffixAtAll_Fails()
    {
        var error = await ExpectAuthRejectedAsync(
            new MockLiveGateway(DatasetName),
            line => line.Replace(
                "-" + MockLiveGateway.TestBucketId + "|", "|", StringComparison.Ordinal));

        Assert.Contains("bucket_id", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticateAsync_MissingAuthField_Fails()
    {
        var error = await ExpectAuthRejectedAsync(
            new MockLiveGateway(DatasetName),
            line => line[(line.IndexOf('|', StringComparison.Ordinal) + 1)..]);

        Assert.Contains("Missing 'auth='", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticateAsync_WrongDataset_Fails()
    {
        var error = await ExpectAuthRejectedAsync(
            new MockLiveGateway(DatasetName),
            line => line.Replace(
                "dataset=" + DatasetName,
                "dataset=" + Dataset.GlbxMdp3.ToWireString(),
                StringComparison.Ordinal));

        Assert.Contains("dataset=" + DatasetName, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticateAsync_WrongTsOut_Fails()
    {
        var error = await ExpectAuthRejectedAsync(
            new MockLiveGateway(DatasetName),
            line => line.Replace("ts_out=0", "ts_out=1", StringComparison.Ordinal));

        Assert.Contains("ts_out=0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticateAsync_WrongCompression_Fails()
    {
        var error = await ExpectAuthRejectedAsync(
            new MockLiveGateway(DatasetName),
            line => line.Replace("compression=none", "compression=zstd", StringComparison.Ordinal));

        Assert.Contains("compression=none", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticateAsync_EmptyClient_Fails()
    {
        var error = await ExpectAuthRejectedAsync(
            new MockLiveGateway(DatasetName),
            line => line.Replace(
                "client=" + StubLiveClient.UserAgent, "client=", StringComparison.Ordinal));

        Assert.Contains("client= is empty", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticateAsync_HeartbeatIntervalThatWasNotAskedFor_Fails()
    {
        var error = await ExpectAuthRejectedAsync(
            new MockLiveGateway(DatasetName),
            line => line + "|heartbeat_interval_s=60");

        Assert.Contains("Expected no 'heartbeat_interval_s='", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticateAsync_MissingTheHeartbeatIntervalItAskedFor_Fails()
    {
        var error = await ExpectAuthRejectedAsync(
            new MockLiveGateway(DatasetName),
            line => line,
            heartbeatInterval: Duration.FromMinutes(1));

        Assert.Contains("Missing 'heartbeat_interval_s='", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticateAsync_HeartbeatIntervalWithTheWrongSeconds_Fails()
    {
        var error = await ExpectAuthRejectedAsync(
            new MockLiveGateway(DatasetName),
            line => line + "|heartbeat_interval_s=30",
            heartbeatInterval: Duration.FromMinutes(1));

        Assert.Contains("heartbeat_interval_s=60", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticateAsync_SlowReaderBehaviorThatWasNotAskedFor_Fails()
    {
        var error = await ExpectAuthRejectedAsync(
            new MockLiveGateway(DatasetName),
            line => line + "|slow_reader_behavior=skip");

        Assert.Contains("Expected no 'slow_reader_behavior='", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticateAsync_ClientDisconnectsBeforeAuthenticating_Fails()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        var cancel = Cancel;

        var clientTask = Task.Run(
            async () =>
            {
                await using var client = new StubLiveClient();
                await client.ConnectAsync(gateway.Address, cancel);
                await client.ReadLineAsync(cancel);
                await client.ReadLineAsync(cancel);
            },
            cancel);

        var error = await Assert.ThrowsAsync<MockGatewayException>(
            () => gateway.AuthenticateAsync(cancellationToken: cancel));
        await clientTask;

        Assert.Contains("closed the connection", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ authentication accepts

    [Fact]
    public async Task AuthenticateAsync_HeartbeatInterval_IsAcceptedWhenItMatches()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = await HandshakeAsync(gateway, heartbeatInterval: Duration.FromMinutes(1));

        Assert.Equal(MockLiveGateway.Challenge, client.Challenge);
    }

    [Fact]
    public async Task AuthenticateAsync_SlowReaderBehavior_IsAcceptedWhenItMatches()
    {
        await using var gateway = new MockLiveGateway(DatasetName)
        {
            ExpectedSlowReaderBehavior = "skip",
        };
        await using var client = new StubLiveClient();
        var cancel = Cancel;

        var clientTask = Task.Run(
            async () =>
            {
                await client.ConnectAsync(gateway.Address, cancel);
                await client.ReadLineAsync(cancel);
                var challenge = (await client.ReadLineAsync(cancel))["cram=".Length..];
                await client.SendLineAsync(
                    StubLiveClient.BuildAuthLine(challenge, MockLiveGateway.TestApiKey, DatasetName)
                    + "|slow_reader_behavior=skip",
                    cancel);
                await client.ReadLineAsync(cancel);
            },
            cancel);

        var fields = await gateway.AuthenticateAsync(cancellationToken: cancel);
        await clientTask;

        Assert.Equal("skip", fields["slow_reader_behavior"]);
    }

    [Fact]
    public async Task AuthenticateAsync_AKeyOtherThanTheDefault_IsAcceptedWhenExpectedApiKeyMatches()
    {
        await using var gateway = new MockLiveGateway(DatasetName)
        {
            ExpectedApiKey = DecoyApiKey,
        };
        await using var client = new StubLiveClient();

        var clientTask = client.ConnectAndAuthenticateAsync(
            gateway.Address, DecoyApiKey, DatasetName, cancellationToken: Cancel);
        var fields = await gateway.AuthenticateAsync(cancellationToken: Cancel);
        await clientTask;

        Assert.Equal(
            StubLiveClient.CramResponse(MockLiveGateway.Challenge, DecoyApiKey)
            + "-" + MockLiveGateway.TestBucketId,
            fields["auth"]);
    }

    [Fact]
    public void ExpectedApiKey_ShorterThanABucketId_IsRejectedAtSetup()
    {
        Assert.Throws<ArgumentException>(
            () => new MockLiveGateway(DatasetName) { ExpectedApiKey = "abc" });
    }

    // ------------------------------------------------------------------- subscription rejects

    [Fact]
    public async Task ExpectSubscribeAsync_MissingIsLast_Fails()
    {
        var error = await ExpectSubscribeRejectedAsync(
            "schema=trades|stype_in=raw_symbol|symbols=MSFT,TSLA,QQQ|snapshot=0|id=1");

        Assert.Contains("Missing 'is_last='", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpectSubscribeAsync_IsLastOnAChunkThatIsNotTheLast_Fails()
    {
        var error = await ExpectSubscribeRejectedAsync(
            "schema=trades|stype_in=raw_symbol|symbols=MSFT,TSLA,QQQ|snapshot=0|is_last=0|id=1",
            isLast: true);

        Assert.Contains("Expected 'is_last=1'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpectSubscribeAsync_MissingId_Fails()
    {
        var error = await ExpectSubscribeRejectedAsync(
            "schema=trades|stype_in=raw_symbol|symbols=MSFT,TSLA,QQQ|snapshot=0|is_last=1",
            isLast: true);

        Assert.Contains("Missing 'id='", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpectSubscribeAsync_WrongSymbols_Fails()
    {
        var error = await ExpectSubscribeRejectedAsync(
            "schema=trades|stype_in=raw_symbol|symbols=MSFT,TSLA|snapshot=0|is_last=1|id=1",
            isLast: true);

        Assert.Contains("symbols=MSFT,TSLA,QQQ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpectSubscribeAsync_WrongSchema_Fails()
    {
        var error = await ExpectSubscribeRejectedAsync(
            "schema=mbo|stype_in=raw_symbol|symbols=MSFT,TSLA,QQQ|snapshot=0|is_last=1|id=1",
            isLast: true);

        Assert.Contains("schema=trades", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpectSubscribeAsync_WrongStypeIn_Fails()
    {
        var error = await ExpectSubscribeRejectedAsync(
            "schema=trades|stype_in=instrument_id|symbols=MSFT,TSLA,QQQ|snapshot=0|is_last=1|id=1",
            isLast: true);

        Assert.Contains("stype_in=raw_symbol", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpectSubscribeAsync_SnapshotThatWasNotExpected_Fails()
    {
        var error = await ExpectSubscribeRejectedAsync(
            "schema=trades|stype_in=raw_symbol|symbols=MSFT,TSLA,QQQ|snapshot=1|is_last=1|id=1",
            isLast: true);

        Assert.Contains("snapshot=0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpectSubscribeAsync_StartThatWasNotExpected_Fails()
    {
        var error = await ExpectSubscribeRejectedAsync(
            "schema=trades|stype_in=raw_symbol|symbols=MSFT,TSLA,QQQ|snapshot=0|is_last=1|start=1|id=1",
            isLast: true);

        Assert.Contains("Expected no 'start='", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpectSubscribeAsync_UnparseableId_Fails()
    {
        var error = await ExpectSubscribeRejectedAsync(
            "schema=trades|stype_in=raw_symbol|symbols=MSFT,TSLA,QQQ|snapshot=0|is_last=1|id=-1",
            isLast: true,
            expectedId: null);

        Assert.Contains("is not a subscription id", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------- subscription accepts

    [Fact]
    public async Task ExpectSubscribeAsync_IntradayReplay_MatchesOnTheStartTimestampInNanoseconds()
    {
        var start = DbnTime.ToInstant(1_688_428_800_000_000_123UL);

        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = await HandshakeAsync(gateway);

        await client.SubscribeAsync(
            Schema.Trades,
            SType.RawSymbol,
            FirstChunk,
            isLast: true,
            id: 7,
            start: start,
            cancellationToken: Cancel);

        var fields = await gateway.ExpectSubscribeAsync(
            Expect(FirstChunk, id: 7) with { Start = start }, isLast: true, Cancel);

        // Nanoseconds, not milliseconds or ticks: the whole point of Instant over DateTime.
        Assert.Equal("1688428800000000123", fields["start"]);
    }

    [Fact]
    public async Task ExpectSubscribeAsync_Snapshot_MatchesWhenBothSidesAskForIt()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = await HandshakeAsync(gateway);

        await client.SubscribeAsync(
            Schema.Mbo,
            SType.RawSymbol,
            FirstChunk,
            isLast: true,
            id: 3,
            useSnapshot: true,
            cancellationToken: Cancel);

        var fields = await gateway.ExpectSubscribeAsync(
            Expect(FirstChunk, id: 3) with { Schema = Schema.Mbo, UseSnapshot = true },
            isLast: true,
            Cancel);

        Assert.Equal("1", fields["snapshot"]);
    }

    [Fact]
    public async Task ExpectSubscribeAsync_AnyId_AcceptsWhateverTheClientAutoAssigned()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = await HandshakeAsync(gateway);

        await client.SubscribeAsync(
            Schema.Trades, SType.RawSymbol, FirstChunk, isLast: true, id: 41, cancellationToken: Cancel);
        var fields = await gateway.ExpectSubscribeAsync(Expect(FirstChunk, id: null), isLast: true, Cancel);

        Assert.Equal("41", fields["id"]);
    }

    // ----------------------------------------------------------------------- session lifecycle

    [Fact]
    public async Task StartAsync_SomethingOtherThanStartSession_Fails()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = await HandshakeAsync(gateway);

        await client.SendLineAsync("start_sesion", Cancel);

        var error = await Assert.ThrowsAsync<MockGatewayException>(() => gateway.StartAsync(Cancel));
        Assert.Contains("start_sesion", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartCompressedAsync_WhenCompressionWasNeverNegotiated_Throws()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = await HandshakeAsync(gateway);

        await client.StartSessionAsync(Cancel);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.StartCompressedAsync(Cancel));
        Assert.Contains("ExpectedCompression", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptAsync_WithAConnectionAlreadyOpen_Throws()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = await HandshakeAsync(gateway);

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.AcceptAsync(Cancel));
        Assert.Equal(MockLiveGateway.Greeting, client.Greeting);
    }

    [Fact]
    public async Task CloseAsync_LeavesTheListenerBound_SoAClientCanReconnectToTheSameAddress()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        var address = gateway.Address;

        var first = await HandshakeAsync(gateway);
        await first.DisposeAsync();
        await gateway.CloseAsync();

        // The address has to survive the disconnect: upstream's reconnect() reuses the already
        // resolved peer address rather than re-resolving, and #23's tests depend on being able to
        // exercise that.
        await using var second = await HandshakeAsync(gateway);

        Assert.Equal(MockLiveGateway.Greeting, second.Greeting);
        Assert.Equal(address, gateway.Address);
    }

    [Fact]
    public async Task CloseAsync_WithNothingConnected_IsANoOp()
    {
        await using var gateway = new MockLiveGateway(DatasetName);

        await gateway.CloseAsync();
        await gateway.CloseAsync();
    }

    [Fact]
    public async Task SendAsync_ALineThatAlreadyHasATerminator_Throws()
    {
        await using var gateway = new MockLiveGateway(DatasetName);

        await Assert.ThrowsAsync<ArgumentException>(() => gateway.SendAsync("one\ntwo", Cancel));
    }

    [Fact]
    public async Task SendAsync_WithNothingConnected_Throws()
    {
        await using var gateway = new MockLiveGateway(DatasetName);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.SendAsync(MockLiveGateway.Greeting, Cancel));
    }

    // ------------------------------------------------------------------------------- helpers

    private static ExpectedSubscription Expect(IReadOnlyList<string> symbols, uint? id) => new()
    {
        Schema = Schema.Trades,
        StypeIn = SType.RawSymbol,
        Symbols = symbols,
        Id = id,
    };

    private static void AssertSessionMetadata(MockLiveGateway gateway, Metadata received)
    {
        Assert.Equal(gateway.SessionMetadata.Version, received.Version);
        Assert.Equal(gateway.SessionMetadata.Dataset, received.Dataset);
        Assert.Equal(gateway.SessionMetadata.Start, received.Start);
        Assert.Equal(gateway.SessionMetadata.StypeOut, received.StypeOut);
        Assert.Equal(gateway.SessionMetadata.TsOut, received.TsOut);

        // A live session's metadata names no single schema and no input symbology, because a
        // session can carry several subscriptions.
        Assert.Null(received.Schema);
        Assert.Null(received.StypeIn);
    }

    private static async Task<StubLiveClient> HandshakeAsync(
        MockLiveGateway gateway,
        Compression compression = Compression.None,
        bool sendTsOut = false,
        Duration? heartbeatInterval = null)
    {
        var client = new StubLiveClient();
        var clientTask = client.ConnectAndAuthenticateAsync(
            gateway.Address,
            MockLiveGateway.TestApiKey,
            gateway.Dataset,
            compression,
            sendTsOut,
            heartbeatInterval,
            Cancel);

        await gateway.AuthenticateAsync(heartbeatInterval, Cancel);
        var response = await clientTask;

        Assert.Equal("1", response["success"]);
        return client;
    }

    private static async Task<MockGatewayException> ExpectAuthRejectedAsync(
        MockLiveGateway gateway,
        Func<string, string> corrupt,
        Duration? heartbeatInterval = null)
    {
        // The caller constructs the gateway so it can set init properties; this owns it from here.
        await using var owned = gateway;
        await using var client = new StubLiveClient();
        var cancel = Cancel;

        var clientTask = Task.Run(
            async () =>
            {
                await client.ConnectAsync(gateway.Address, cancel);
                await client.ReadLineAsync(cancel);
                var challenge = (await client.ReadLineAsync(cancel))["cram=".Length..];
                await client.SendLineAsync(
                    corrupt(StubLiveClient.BuildAuthLine(
                        challenge, MockLiveGateway.TestApiKey, DatasetName)),
                    cancel);
            },
            cancel);

        var error = await Assert.ThrowsAsync<MockGatewayException>(
            () => gateway.AuthenticateAsync(heartbeatInterval, cancel));
        await clientTask;
        return error;
    }

    private static async Task<MockGatewayException> ExpectSubscribeRejectedAsync(
        string subscriptionLine,
        bool isLast = false,
        uint? expectedId = 1)
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = await HandshakeAsync(gateway);

        await client.SendLineAsync(subscriptionLine, Cancel);

        return await Assert.ThrowsAsync<MockGatewayException>(
            () => gateway.ExpectSubscribeAsync(Expect(FirstChunk, expectedId), isLast, Cancel));
    }

    private static byte[] Bytes<T>(T record)
        where T : unmanaged, IRecord<T>
        => MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in record)).ToArray();

    // Records have no public constructor — their fields are readonly and only ever come from a
    // reinterpret over a buffer — so one gets built here the way the wire builds it. Storage is
    // ulong[] so the bytes start 8-byte aligned, matching every other record-building test in this
    // repo.
    private static TradeMsg Trade(uint instrumentId, ulong tsRecv, long price)
    {
        var storage = new ulong[TradeMsg.WireSize / sizeof(ulong)];
        var bytes = MemoryMarshal.AsBytes(storage.AsSpan());

        bytes[0] = (byte)(TradeMsg.WireSize / DbnConstants.RecordLengthMultiplier);
        bytes[1] = (byte)RType.Mbp0;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[2..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], instrumentId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], tsRecv - 1);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[16..], price);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[24..], 5);
        bytes[28] = (byte)'T';
        bytes[29] = (byte)'A';
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], tsRecv);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[40..], 11);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[44..], 13);

        return MemoryMarshal.Read<TradeMsg>(bytes);
    }

    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }
}
