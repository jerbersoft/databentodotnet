using System.Net;
using System.Net.Sockets;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Dbn.Publishers;
using NodaTime;

namespace DatabentoDotNet.Live.Tests;

/// <summary>
/// Tests for <see cref="LiveClient"/>'s construction surface and its connect.
/// </summary>
/// <remarks>
/// <para>
/// The connect tests run against <see cref="MockLiveGateway"/> from #18 rather than against a
/// real gateway, which is the whole reason that issue landed first.
/// </para>
/// <para>
/// <b>On the failure modes.</b> #19's definition of done asked that "connecting to a closed port
/// raises <c>ConnectTimeoutException</c> rather than hanging". A closed port does not do that:
/// TCP answers a SYN to a closed port with a RST, so the attempt fails immediately and is not a
/// timeout at all. Both halves of the intent are kept and separated —
/// <see cref="ConnectAsync_AClosedPort_FailsAtOnceAndNamesTheEndpoint"/> covers the refused case,
/// and the two timeout tests cover a budget that actually elapses.
/// </para>
/// </remarks>
public class LiveClientTests
{
    private static readonly string DatasetName = Dataset.XnasItch.ToWireString();

    /// <summary>
    /// TEST-NET-1 (RFC 5737 §3), reserved for documentation and guaranteed not to be routed. A
    /// SYN to it is dropped rather than refused, which is the only way to make a connect attempt
    /// stay outstanding long enough for a budget to run out.
    /// </summary>
    private const string BlackHoledAddress = "192.0.2.1";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConnectAsync_ReachesTheGatewayAndRecordsTheAddressItReached()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);

        var accepted = gateway.AcceptAsync(Cancel);
        await client.ConnectAsync(Cancel);
        await accepted;

        Assert.True(client.IsConnected);
        Assert.Equal(gateway.Address, client.Endpoint);
    }

    [Fact]
    public async Task ConnectAsync_WhileAlreadyConnected_Throws()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);

        var accepted = gateway.AcceptAsync(Cancel);
        await client.ConnectAsync(Cancel);
        await accepted;

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(Cancel));
    }

    [Fact]
    public async Task CloseAsync_KeepsTheResolvedAddressForAReconnectToReuse()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);

        var accepted = gateway.AcceptAsync(Cancel);
        await client.ConnectAsync(Cancel);
        await accepted;
        var reached = client.Endpoint;

        await client.CloseAsync();

        Assert.False(client.IsConnected);

        // Upstream's reconnect() reuses peer_addr rather than re-resolving DNS (PORTING.md §4),
        // so the address has to outlive the socket it came from. #23 is what consumes this.
        Assert.Equal(reached, client.Endpoint);
        Assert.NotNull(client.Endpoint);
    }

    [Fact]
    public async Task CloseAsync_ThenConnectAsync_OpensASecondConnection()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);

        var accepted = gateway.AcceptAsync(Cancel);
        await client.ConnectAsync(Cancel);
        await accepted;
        await client.CloseAsync();
        await gateway.CloseAsync();

        var reaccepted = gateway.AcceptAsync(Cancel);
        await client.ConnectAsync(Cancel);
        await reaccepted;

        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task CloseAsync_WithNothingOpen_IsANoOp()
    {
        await using var client = new LiveClient { ApiKey = TestKey(), Dataset = DatasetName };

        await client.CloseAsync();
        await client.CloseAsync();

        Assert.False(client.IsConnected);
        Assert.Null(client.Endpoint);
    }

    [Fact]
    public async Task ConnectAsync_AClosedPort_FailsAtOnceAndNamesTheEndpoint()
    {
        var closed = ClosedLoopbackEndPoint();
        await using var client = new LiveClient
        {
            ApiKey = TestKey(),
            Dataset = DatasetName,
            Gateway = closed,
        };

        var error = await Assert.ThrowsAsync<LiveConnectException>(() => client.ConnectAsync(Cancel));

        // Not a ConnectTimeoutException: a RST comes back immediately, and calling that a timeout
        // would send a reader looking at their network for a problem that is a wrong port.
        Assert.IsNotType<ConnectTimeoutException>(error);
        Assert.Equal(closed, error.EndPoint);
        Assert.IsType<SocketException>(error.InnerException);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task ConnectAsync_ABudgetThatHasAlreadyRunOut_TimesOutWithoutOpeningASocket()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = new LiveClient
        {
            ApiKey = TestKey(),
            Dataset = DatasetName,
            Gateway = gateway.Address,
            ConnectTimeout = Duration.Zero,
        };

        var error = await Assert.ThrowsAsync<ConnectTimeoutException>(() => client.ConnectAsync(Cancel));

        Assert.Equal(Duration.Zero, error.Timeout);
        Assert.Equal(gateway.Address, error.EndPoint);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task ConnectAsync_AnAddressThatSwallowsTheSyn_TimesOut()
    {
        await using var client = new LiveClient
        {
            ApiKey = TestKey(),
            Dataset = DatasetName,
            Gateway = new IPEndPoint(IPAddress.Parse(BlackHoledAddress), LiveGateway.DefaultPort),
            ConnectTimeout = Duration.FromMilliseconds(250),
        };

        var error = await Assert.ThrowsAsync<ConnectTimeoutException>(() => client.ConnectAsync(Cancel));

        Assert.Equal(Duration.FromMilliseconds(250), error.Timeout);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task ConnectAsync_CancelledByTheCaller_IsNotReportedAsATimeout()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await using var client = new LiveClient
        {
            ApiKey = TestKey(),
            Dataset = DatasetName,
            Gateway = new IPEndPoint(IPAddress.Parse(BlackHoledAddress), LiveGateway.DefaultPort),
        };

        // The caller's own cancellation is not a gateway problem and must not be dressed up as
        // one; the two arrive on the same token and are told apart by which one asked.
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ConnectAsync(cancelled.Token));

        Assert.IsNotType<ConnectTimeoutException>(error);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task ConnectAsync_WithNoGatewayOverride_DerivesTheHostFromTheDataset()
    {
        // A dataset that cannot become a host label fails in LiveGateway before any socket opens,
        // which is what shows ConnectAsync goes through it rather than around it. Asserting the
        // successful path would mean a real DNS lookup in a unit test.
        await using var client = new LiveClient { ApiKey = TestKey(), Dataset = "NOT A DATASET" };

        var error = await Assert.ThrowsAsync<ArgumentException>(() => client.ConnectAsync(Cancel));

        Assert.Contains("DNS label", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Construction_DefaultsMatchUpstream()
    {
        var client = new LiveClient { ApiKey = TestKey(), Dataset = DatasetName };

        Assert.False(client.SendTsOut);
        Assert.Null(client.HeartbeatInterval);
        Assert.Null(client.SlowReaderBehavior);
        Assert.Null(client.Gateway);
        Assert.Equal(VersionUpgradePolicy.UpgradeToV3, client.UpgradePolicy);
        Assert.Equal(Duration.FromSeconds(10), client.ConnectTimeout);
        Assert.False(client.IsConnected);
        Assert.Null(client.Endpoint);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(60)]
    [InlineData(1800)]
    public void HeartbeatInterval_InsideTheGatewaysRange_IsAccepted(int seconds)
    {
        var client = new LiveClient
        {
            ApiKey = TestKey(),
            Dataset = DatasetName,
            HeartbeatInterval = Duration.FromSeconds(seconds),
        };

        Assert.Equal(Duration.FromSeconds(seconds), client.HeartbeatInterval);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(1801)]
    [InlineData(-5)]
    public void HeartbeatInterval_OutsideTheGatewaysRange_Throws(int seconds)
    {
        // Upstream documents 5-1800 and leaves enforcement to the gateway, which costs a round
        // trip and a closed connection to discover.
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiveClient
        {
            ApiKey = TestKey(),
            Dataset = DatasetName,
            HeartbeatInterval = Duration.FromSeconds(seconds),
        });
    }

    [Fact]
    public void HeartbeatInterval_WithSubSecondPrecision_Throws()
    {
        // Upstream warns and then silently discards the fraction, so the interval in the caller's
        // code is not the interval on the wire.
        var error = Assert.Throws<ArgumentException>(() => new LiveClient
        {
            ApiKey = TestKey(),
            Dataset = DatasetName,
            HeartbeatInterval = Duration.FromSeconds(30) + Duration.FromNanoseconds(1),
        });

        Assert.Contains("whole seconds", error.Message, StringComparison.Ordinal);
    }

    private static ApiKey TestKey() => new(MockLiveGateway.TestApiKey);

    private static LiveClient Client(MockLiveGateway gateway) => new()
    {
        ApiKey = TestKey(),
        Dataset = gateway.Dataset,
        Gateway = gateway.Address,
    };

    /// <summary>
    /// Binds a loopback port, reads back the number, and releases it — so the endpoint is one
    /// nothing is listening on, without guessing a port number that something might be.
    /// </summary>
    private static IPEndPoint ClosedLoopbackEndPoint()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return (IPEndPoint)probe.LocalEndPoint!;
    }
}
