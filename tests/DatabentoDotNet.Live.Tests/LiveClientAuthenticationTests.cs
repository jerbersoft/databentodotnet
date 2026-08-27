using DatabentoDotNet.Dbn;
using DatabentoDotNet.Dbn.Publishers;
using NodaTime;

namespace DatabentoDotNet.Live.Tests;

/// <summary>
/// Tests for <see cref="LiveClient.AuthenticateAsync"/>: the CRAM digest, the fields of the
/// authentication request, and each way the handshake can fail.
/// </summary>
/// <remarks>
/// <para>
/// Run against <see cref="MockLiveGateway"/>, which recomputes the expected digest rather than
/// checking that the client sent something hex-shaped. That means most of the request is asserted
/// by the gateway rejecting a wrong one, and the tests here assert the things a gateway cannot:
/// the digest against an independently computed constant, which half of the key the bucket suffix
/// comes from, and that the key never reaches an exception.
/// </para>
/// <para>
/// <b>On the digest constant.</b> <see cref="ExpectedDigest"/> was computed outside this codebase
/// — <c>printf '%s' 'challenge|key' | shasum -a 256</c>, cross-checked against Python's
/// <c>hashlib</c> — precisely so that it is not the implementation agreeing with itself. Both the
/// client and the gateway call <c>SHA256.HashData</c>; a constant neither of them produced is the
/// only thing that catches the two of them being wrong together.
/// </para>
/// </remarks>
public class LiveClientAuthenticationTests
{
    private static readonly string DatasetName = Dataset.XnasItch.ToWireString();

    /// <summary>
    /// <c>sha256("t7kNhwj4xqR0QYjzFKtBEG2ec2pXJ4FK|32-character-with-lots-of-filler")</c>, in
    /// lowercase hex — <see cref="MockLiveGateway.Challenge"/> and
    /// <see cref="MockLiveGateway.TestApiKey"/> joined by a pipe. Computed by <c>shasum</c> and by
    /// Python, not by this library.
    /// </summary>
    private const string ExpectedDigest =
        "42e2c6c6a874b2f4498bcaf0541be406901a23adef1fb2843d5426f6d2387d14";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AuthenticateAsync_CompletesTheHandshakeAndCapturesTheSession()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);

        var handshake = gateway.AuthenticateAsync(cancellationToken: Cancel);
        await client.ConnectAsync(Cancel);
        await client.AuthenticateAsync(Cancel);
        await handshake;

        Assert.True(client.IsAuthenticated);
        Assert.True(client.IsConnected);
        Assert.Equal(MockLiveGateway.SessionId, client.SessionId);
        Assert.Equal(MockLiveGateway.Greeting, client.Greeting);
    }

    [Fact]
    public async Task AuthenticateAsync_SendsTheDigestOfTheChallengeAndTheKeyInThatOrder()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);

        var handshake = gateway.AuthenticateAsync(cancellationToken: Cancel);
        await client.ConnectAsync(Cancel);
        await client.AuthenticateAsync(Cancel);
        var fields = await handshake;

        var auth = fields["auth"];
        Assert.Equal($"{ExpectedDigest}-{MockLiveGateway.TestBucketId}", auth);
    }

    [Fact]
    public async Task AuthenticateAsync_TakesTheBucketFromTheEndOfTheKeyAndNotTheStart()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);

        var handshake = gateway.AuthenticateAsync(cancellationToken: Cancel);
        await client.ConnectAsync(Cancel);
        await client.AuthenticateAsync(Cancel);
        var fields = await handshake;

        var bucket = fields["auth"].Split('-')[^1];
        var key = MockLiveGateway.TestApiKey;

        // Both are five characters of the same key, and only one of them is the bucket id. A
        // client that sliced from the wrong end would still send something well-formed.
        Assert.Equal(key[^MockLiveGateway.BucketIdLength..], bucket);
        Assert.NotEqual(key[..MockLiveGateway.BucketIdLength], bucket);
    }

    [Fact]
    public async Task AuthenticateAsync_SendsTheSessionOptionsItWasConfiguredWith()
    {
        await using var gateway = new MockLiveGateway(DatasetName, sendTsOut: true)
        {
            ExpectedCompression = Compression.Zstd,
            ExpectedSlowReaderBehavior = "skip",
            ExpectedClient = UserAgent.Value,
        };
        await using var client = new LiveClient
        {
            ApiKey = TestKey(),
            Dataset = gateway.Dataset,
            Gateway = gateway.Address,
            Compression = Compression.Zstd,
            SendTsOut = true,
            SlowReaderBehavior = SlowReaderBehavior.Skip,
            HeartbeatInterval = Duration.FromSeconds(30),
        };

        // Every one of these is checked by the gateway; awaiting the handshake is the assertion.
        var handshake = gateway.AuthenticateAsync(Duration.FromSeconds(30), Cancel);
        await client.ConnectAsync(Cancel);
        await client.AuthenticateAsync(Cancel);
        var fields = await handshake;

        Assert.Equal(gateway.Dataset, fields["dataset"]);
        Assert.Equal("dbn", fields["encoding"]);
        Assert.Equal("zstd", fields["compression"]);
        Assert.Equal("1", fields["ts_out"]);
        Assert.Equal("30", fields["heartbeat_interval_s"]);
        Assert.Equal("skip", fields["slow_reader_behavior"]);
        Assert.Equal(UserAgent.Value, fields["client"]);
    }

    [Theory]
    [InlineData(SlowReaderBehavior.Warn, "warn")]
    [InlineData(SlowReaderBehavior.Skip, "skip")]
    public async Task AuthenticateAsync_SendsSlowReaderBehaviorInEitherSetting(
        SlowReaderBehavior behavior,
        string expected)
    {
        // Both settings, not just the one the options test happens to use. The two mean opposite
        // things to the gateway — keep sending and let the client fall behind, or drop records to
        // bring it back to real time — so a client that sent the same spelling for both would be
        // silently choosing one of them for every caller. #23.
        await using var gateway = new MockLiveGateway(DatasetName)
        {
            ExpectedSlowReaderBehavior = expected,
        };
        await using var client = new LiveClient
        {
            ApiKey = TestKey(),
            Dataset = gateway.Dataset,
            Gateway = gateway.Address,
            SlowReaderBehavior = behavior,
        };

        var handshake = gateway.AuthenticateAsync(cancellationToken: Cancel);
        await client.ConnectAsync(Cancel);
        await client.AuthenticateAsync(Cancel);
        var fields = await handshake;

        Assert.Equal(expected, fields["slow_reader_behavior"]);
    }

    [Fact]
    public async Task AuthenticateAsync_WithNoHeartbeatOrSlowReaderSet_OmitsBothFields()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);

        // The gateway requires both fields to be absent when it is given no interval, so this
        // would fail there too — asserted here as well because "the client sends the gateway's
        // default explicitly" is a silent behaviour change, not a protocol error.
        var handshake = gateway.AuthenticateAsync(cancellationToken: Cancel);
        await client.ConnectAsync(Cancel);
        await client.AuthenticateAsync(Cancel);
        var fields = await handshake;

        Assert.False(fields.ContainsKey("heartbeat_interval_s"));
        Assert.False(fields.ContainsKey("slow_reader_behavior"));
        Assert.Equal("none", fields["compression"]);
        Assert.Equal("0", fields["ts_out"]);
    }

    [Fact]
    public async Task AuthenticateAsync_WhenTheGatewayRejectsTheKey_ThrowsCarryingItsError()
    {
        const string GatewayError = "invalid API key";

        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);

        var handshake = RejectAsync(gateway, $"success=0|error={GatewayError}");
        await client.ConnectAsync(Cancel);

        var error = await Assert.ThrowsAsync<DatabentoAuthenticationException>(
            () => client.AuthenticateAsync(Cancel));
        await handshake;

        Assert.Equal(GatewayError, error.Error);
        Assert.Equal($"success=0|error={GatewayError}", error.Response);
        Assert.Contains(GatewayError, error.Message, StringComparison.Ordinal);
        Assert.False(client.IsAuthenticated);
    }

    [Fact]
    public async Task AuthenticateAsync_WhenTheGatewayRejectsWithoutAnError_KeepsTheWholeResponse()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);

        var handshake = RejectAsync(gateway, "success=0");
        await client.ConnectAsync(Cancel);

        var error = await Assert.ThrowsAsync<DatabentoAuthenticationException>(
            () => client.AuthenticateAsync(Cancel));
        await handshake;

        Assert.Null(error.Error);
        Assert.Equal("success=0", error.Response);
    }

    [Fact]
    public async Task AuthenticateAsync_WhenTheResponseHasNoSuccessField_IsARejection()
    {
        // Upstream treats a missing success key the same as success=0: whatever the gateway meant
        // by it, it is not an authenticated session.
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);

        var handshake = RejectAsync(gateway, "session_id=5");
        await client.ConnectAsync(Cancel);

        await Assert.ThrowsAsync<DatabentoAuthenticationException>(() => client.AuthenticateAsync(Cancel));
        await handshake;

        Assert.Null(client.SessionId);
    }

    [Fact]
    public async Task AuthenticateAsync_WhenTheKeyIsRejected_TheKeyIsInNothingTheExceptionRenders()
    {
        // The one failure whose message is about the key. If any of them leaks it, it is this one.
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);

        var handshake = RejectAsync(gateway, "success=0|error=invalid API key");
        await client.ConnectAsync(Cancel);

        var error = await Assert.ThrowsAsync<DatabentoAuthenticationException>(
            () => client.AuthenticateAsync(Cancel));
        await handshake;

        var key = MockLiveGateway.TestApiKey;
        var rendered = error.ToString();

        Assert.DoesNotContain(key, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(key, rendered, StringComparison.Ordinal);

        // Not just the whole key: the part of it that is not the bucket id must not appear either,
        // which a "…iller" redaction passes and a "3…iller" one does not.
        var secret = key[..^MockLiveGateway.BucketIdLength];
        Assert.DoesNotContain(secret, rendered, StringComparison.Ordinal);

        // And the redacted form is present, so the message still says which key was refused.
        Assert.Contains(MockLiveGateway.TestBucketId, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticateAsync_AChallengeWithoutTheCramPrefix_ThrowsWithoutAuthenticating()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);

        var handshake = Task.Run(
            async () =>
            {
                await gateway.AcceptAsync(Cancel);
                await gateway.SendAsync(MockLiveGateway.Greeting, Cancel);
                await gateway.SendAsync("lsg_version=1.2.3", Cancel);
            },
            Cancel);

        await client.ConnectAsync(Cancel);

        // Not DatabentoAuthenticationException: hashing an empty challenge would produce a digest
        // the gateway rejects, and reporting that as a bad key sends the caller to rotate
        // credentials that were never the problem.
        var error = await Assert.ThrowsAsync<LiveProtocolException>(() => client.AuthenticateAsync(Cancel));
        await handshake;

        Assert.Contains("cram=", error.Message, StringComparison.Ordinal);
        Assert.Contains("lsg_version=1.2.3", error.Message, StringComparison.Ordinal);
        Assert.False(client.IsAuthenticated);
    }

    [Fact]
    public async Task AuthenticateAsync_WhenTheGatewayHangsUpMidHandshake_Throws()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);

        var handshake = Task.Run(
            async () =>
            {
                await gateway.AcceptAsync(Cancel);
                await gateway.SendAsync(MockLiveGateway.Greeting, Cancel);
                await gateway.CloseAsync();
            },
            Cancel);

        await client.ConnectAsync(Cancel);

        var error = await Assert.ThrowsAsync<LiveProtocolException>(() => client.AuthenticateAsync(Cancel));
        await handshake;

        Assert.Contains("the CRAM challenge", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticateAsync_AGreetingThatNeverTerminates_FailsRatherThanBuffering()
    {
        // A gateway that streams without ever sending a terminator is answered by growing a buffer
        // until the process dies — at loopback speed, well inside any handshake budget. The cap is
        // 64 KiB; the longest line the client itself ever sends is a 500-symbol subscription.
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);

        var handshake = Task.Run(
            async () =>
            {
                await gateway.AcceptAsync(Cancel);
                await gateway.SendAsync(new string('x', 70_000), Cancel);
            },
            Cancel);

        await client.ConnectAsync(Cancel);

        var error = await Assert.ThrowsAsync<LiveProtocolException>(() => client.AuthenticateAsync(Cancel));
        await handshake;

        Assert.Contains("without a terminator", error.Message, StringComparison.Ordinal);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task AuthenticateAsync_WhenTheGatewaySaysNothing_TimesOutAndDisconnects()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = new LiveClient
        {
            ApiKey = TestKey(),
            Dataset = gateway.Dataset,
            Gateway = gateway.Address,
            AuthTimeout = Duration.FromMilliseconds(250),
        };

        // Accepted, and then silent — the case upstream's budget-free authenticate waits out until
        // the OS gives up on the socket.
        var accepted = gateway.AcceptAsync(Cancel);
        await client.ConnectAsync(Cancel);
        await accepted;

        var error = await Assert.ThrowsAsync<AuthTimeoutException>(() => client.AuthenticateAsync(Cancel));

        Assert.Equal(Duration.FromMilliseconds(250), error.Timeout);
        Assert.False(client.IsConnected);
        Assert.False(client.IsAuthenticated);
    }

    [Fact]
    public async Task AuthenticateAsync_WithAnAlreadyElapsedBudget_TimesOutWithoutReadingAnything()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = new LiveClient
        {
            ApiKey = TestKey(),
            Dataset = gateway.Dataset,
            Gateway = gateway.Address,
            AuthTimeout = Duration.Zero,
        };

        var accepted = gateway.AcceptAsync(Cancel);
        await client.ConnectAsync(Cancel);
        await accepted;

        await Assert.ThrowsAsync<AuthTimeoutException>(() => client.AuthenticateAsync(Cancel));

        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task AuthenticateAsync_WithACancelledToken_CancelsRatherThanTimingOut()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);

        var accepted = gateway.AcceptAsync(Cancel);
        await client.ConnectAsync(Cancel);
        await accepted;

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        // The same teardown path as the timeout, reported as the caller's cancellation rather than
        // as a budget that never started.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.AuthenticateAsync(cancelled.Token));

        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task AuthenticateAsync_BeforeConnecting_Throws()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.AuthenticateAsync(Cancel));
    }

    [Fact]
    public async Task AuthenticateAsync_Twice_Throws()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);

        var handshake = gateway.AuthenticateAsync(cancellationToken: Cancel);
        await client.ConnectAsync(Cancel);
        await client.AuthenticateAsync(Cancel);
        await handshake;

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.AuthenticateAsync(Cancel));
    }

    [Fact]
    public async Task CloseAsync_AfterAuthenticating_EndsTheSessionButKeepsTheEndpoint()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);

        var handshake = gateway.AuthenticateAsync(cancellationToken: Cancel);
        await client.ConnectAsync(Cancel);
        await client.AuthenticateAsync(Cancel);
        await handshake;

        await client.CloseAsync();

        Assert.False(client.IsAuthenticated);
        Assert.False(client.IsConnected);
        Assert.Equal(gateway.Address, client.Endpoint);
    }

    [Fact]
    public async Task ConnectAsync_AfterAPreviousSession_ClearsTheSessionItReported()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);

        var handshake = gateway.AuthenticateAsync(cancellationToken: Cancel);
        await client.ConnectAsync(Cancel);
        await client.AuthenticateAsync(Cancel);
        await handshake;

        await client.CloseAsync();
        await gateway.CloseAsync();

        var accepted = gateway.AcceptAsync(Cancel);
        await client.ConnectAsync(Cancel);
        await accepted;

        // A stale session id on a connection that has not authenticated is the kind of value that
        // reads as true in a log written just after the reconnect.
        Assert.Null(client.SessionId);
        Assert.Null(client.Greeting);
    }

    /// <summary>
    /// Runs the gateway's half up to the client's request, then answers with
    /// <paramref name="response"/> instead of a success line.
    /// </summary>
    private static Task RejectAsync(MockLiveGateway gateway, string response) => Task.Run(
        async () =>
        {
            await gateway.ExpectAuthenticationAsync(cancellationToken: Cancel);
            await gateway.SendAsync(response, Cancel);
        },
        Cancel);

    private static ApiKey TestKey() => new(MockLiveGateway.TestApiKey);

    private static LiveClient Client(MockLiveGateway gateway) => new()
    {
        ApiKey = TestKey(),
        Dataset = gateway.Dataset,
        Gateway = gateway.Address,
    };
}
