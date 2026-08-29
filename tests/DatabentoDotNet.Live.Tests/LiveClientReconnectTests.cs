using System.Net;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Dbn.Publishers;
using NodaTime;

namespace DatabentoDotNet.Live.Tests;

/// <summary>
/// Tests for <see cref="LiveClient.ReconnectAsync"/> and <see cref="LiveClient.ResubscribeAsync"/>,
/// for heartbeats arriving as ordinary records, and for the read budget a heartbeat interval
/// derives.
/// </summary>
/// <remarks>
/// <para>
/// <b>The load-bearing assertion is that a replayed subscription has lost its <c>start</c>.</b>
/// Everything else here is lifecycle bookkeeping; that one line is the difference between a
/// reconnect that resumes and a reconnect that quietly replays the same intraday history a second
/// time. Its symptom — duplicated records after a reconnect — looks like a gateway fault and is
/// not one, which is why <see cref="MockLiveGateway.ExpectSubscribeAsync"/> is given the same
/// <see cref="ExpectedSubscription"/> the first session used with only its
/// <see cref="ExpectedSubscription.Start"/> dropped. "Identical except for the start" is then
/// literally what the test says.
/// </para>
/// <para>
/// <b>On what "does not re-resolve DNS" can be asserted from inside one process.</b> Nothing here
/// can make a host name stop resolving mid-run, so the claim is pinned from both ends instead:
/// <see cref="ReconnectAsync_ReusesTheAddressTheFirstConnectResolved"/> connects by name and shows
/// the reconnect lands on the <see cref="IPEndPoint"/> that name resolved to, and
/// <see cref="ReconnectAsync_BeforeEverConnecting_DoesNotGoLookingForAHost"/> shows a client with
/// no resolved address fails for the want of one rather than by going back to
/// <see cref="LiveClient.Dataset"/> — which, in that test, would throw a different exception
/// before a socket ever opened.
/// </para>
/// <para>
/// The first of those is also the regression test for a hazard the reconnect introduces on its
/// own: a connect by name goes out on a dual-stack socket and reports an IPv4-mapped IPv6 address,
/// and a socket built for that family is V6ONLY by default. A client that reached the real gateway
/// by host name — which is every client that does not override <see cref="LiveClient.Gateway"/> —
/// could otherwise not reconnect to it at all.
/// </para>
/// </remarks>
public class LiveClientReconnectTests
{
    private static readonly string DatasetName = Dataset.XnasItch.ToWireString();

    /// <summary>The <c>session_id</c> the gateway issues on the second connection.</summary>
    /// <remarks>
    /// Different from <see cref="MockLiveGateway.SessionId"/> on purpose: a gateway that answered
    /// every handshake with the same id could not tell a client that re-read it from one that kept
    /// the old one, and a new session id is the observable half of "this is a new session".
    /// </remarks>
    private const string SecondSessionId = "6";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // --------------------------------------------------------------------------- Reconnecting

    [Fact]
    public async Task ReconnectAsync_AfterTheStreamEnds_RunsAFreshHandshakeAndCanStartAgain()
    {
        var subscription = new Subscription
        {
            Schema = Schema.Mbo,
            Symbols = Symbols.From(["AAPL", "MSFT"]),
            Start = Instant.FromUnixTimeSeconds(1_609_160_400) + Duration.FromNanoseconds(1),
        };

        var expected = new ExpectedSubscription
        {
            Schema = Schema.Mbo,
            StypeIn = SType.RawSymbol,
            Symbols = ["AAPL", "MSFT"],
            Start = subscription.Start,
            Id = 1,
        };

        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        var endpoint = client.Endpoint;
        Assert.Equal(MockLiveGateway.SessionId, client.SessionId);

        var subscribing = gateway.ExpectSubscribeAsync(expected, isLast: true, Cancel);
        await client.SubscribeAsync(subscription, Cancel);
        await subscribing;

        var serving = gateway.StartAsync(Cancel);
        var metadata = await client.StartAsync(Cancel);
        await serving;

        await gateway.SendRecordAsync(SyntheticMbo.Record(1), Cancel);
        Assert.Equal(1u, await ReadSequenceAsync(client));

        // The gateway hangs up cleanly, which is what a real one does at the end of an interval
        // or when it is restarted. The stream ends; the socket on this side does not, which is
        // exactly the state upstream's is_closed() describes.
        await gateway.CloseAsync();
        await DrainToEndAsync(client);
        Assert.True(client.IsClosed);

        var rehandshake = gateway.AuthenticateAsync(SecondSessionId, cancellationToken: Cancel);
        await client.ReconnectAsync(Cancel);
        await rehandshake;

        Assert.True(client.IsConnected);
        Assert.True(client.IsAuthenticated);
        Assert.False(client.IsClosed);

        // A reconnect is a new session and the gateway issues a new id for it. Everything the last
        // session reported is gone with it — including the metadata, which is the only one of the
        // three that survives a mere CloseAsync.
        Assert.Equal(SecondSessionId, client.SessionId);
        Assert.NotNull(client.Greeting);
        Assert.Null(client.Metadata);

        // The session is not started and the subscriptions are not replayed: both are separate
        // calls because both are the caller's decision.
        Assert.False(client.IsSessionStarted);
        Assert.Equal(endpoint, client.Endpoint);

        var replay = gateway.ExpectSubscribeAsync(expected with { Start = null }, isLast: true, Cancel);
        await client.ResubscribeAsync(Cancel);
        await replay;

        var reserving = gateway.StartAsync(Cancel);
        var second = await client.StartAsync(Cancel);
        await reserving;

        Assert.NotSame(metadata, second);
        Assert.Equal(DatasetName, second.Dataset);

        await gateway.SendRecordAsync(SyntheticMbo.Record(2), Cancel);
        Assert.Equal(2u, await ReadSequenceAsync(client));
    }

    [Fact]
    public async Task ReconnectAsync_WhileStillConnected_ReplacesTheConnection()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        Assert.True(client.IsConnected);
        Assert.False(client.IsClosed);

        // Nothing has gone wrong here: reconnecting is legal on a healthy connection, and closing
        // the old one is the reconnect's job rather than the caller's. The gateway lets go of its
        // side once the client has, and then accepts the new connection off the listener's backlog.
        var reconnecting = client.ReconnectAsync(Cancel);
        await gateway.CloseAsync();
        await gateway.AuthenticateAsync(SecondSessionId, cancellationToken: Cancel);
        await reconnecting;

        Assert.True(client.IsAuthenticated);
        Assert.Equal(SecondSessionId, client.SessionId);
    }

    [Fact]
    public async Task ReconnectAsync_AfterAHeartbeatTimeout_IsWhatTheClientIsFor()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = new LiveClient
        {
            ApiKey = TestKey(),
            Dataset = gateway.Dataset,
            Gateway = gateway.Address,
            ReadTimeout = Duration.FromMilliseconds(250),
        };

        await HandshakeAsync(gateway, client);

        var serving = gateway.StartAsync(Cancel);
        await client.StartAsync(Cancel);
        await serving;

        await Assert.ThrowsAsync<HeartbeatTimeoutException>(
            async () => await client.FillBufferAsync(Cancel));

        // A heartbeat timeout tears the socket down, which is the state upstream leaves itself in
        // and the reason it says a reconnect is required rather than a retry. This is the path
        // that requirement points at, so it has to work from here and not only from a clean close.
        Assert.True(client.IsClosed);
        Assert.False(client.IsConnected);

        await gateway.CloseAsync();

        var rehandshake = gateway.AuthenticateAsync(SecondSessionId, cancellationToken: Cancel);
        await client.ReconnectAsync(Cancel);
        await rehandshake;

        Assert.False(client.IsClosed);
        Assert.True(client.IsAuthenticated);
        Assert.Equal(SecondSessionId, client.SessionId);

        var reserving = gateway.StartAsync(Cancel);
        await client.StartAsync(Cancel);
        await reserving;

        await gateway.SendRecordAsync(SyntheticMbo.Record(7), Cancel);
        Assert.Equal(7u, await ReadSequenceAsync(client));
    }

    [Fact]
    public async Task ReconnectAsync_ReusesTheAddressTheFirstConnectResolved()
    {
        await using var gateway = new MockLiveGateway(DatasetName);

        // A host name rather than an address, so the first connect has to resolve something and
        // Endpoint records the result of that resolution rather than what was configured. This is
        // the shape every client that does not override Gateway is in, since LiveGateway.For
        // returns a DnsEndPoint.
        await using var client = new LiveClient
        {
            ApiKey = new ApiKey(MockLiveGateway.TestApiKey),
            Dataset = gateway.Dataset,
            Gateway = new DnsEndPoint("localhost", gateway.Address.Port),
        };

        await HandshakeAsync(gateway, client);

        var resolved = Assert.IsType<IPEndPoint>(client.Endpoint);
        Assert.Equal(gateway.Address.Port, resolved.Port);

        await gateway.CloseAsync();

        var rehandshake = gateway.AuthenticateAsync(SecondSessionId, cancellationToken: Cancel);
        await client.ReconnectAsync(Cancel);
        await rehandshake;

        // The same address, reached again. A reconnect that went back through the name would be
        // indistinguishable here — a process cannot make a name stop resolving — which is why the
        // implementation takes Endpoint explicitly and the test below pins the other end of it.
        Assert.Equal(resolved, client.Endpoint);
        Assert.Equal(SecondSessionId, client.SessionId);
    }

    [Fact]
    public async Task ReconnectAsync_BeforeEverConnecting_DoesNotGoLookingForAHost()
    {
        // A dataset LiveGateway.For refuses outright, and no Gateway override. ConnectAsync on
        // this client throws ArgumentException before it opens a socket — so a ReconnectAsync that
        // re-derived its endpoint would throw that too. Throwing for the want of a resolved
        // address instead is what shows it never went near the dataset.
        await using var client = new LiveClient { ApiKey = TestKey(), Dataset = "NOT A DATASET" };

        Assert.Null(client.Endpoint);
        await Assert.ThrowsAsync<ArgumentException>(() => client.ConnectAsync(Cancel));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ReconnectAsync(Cancel));

        Assert.Contains("never connected", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(LiveClient.ConnectAsync), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReconnectAsync_KeepsTheSubscriptionsItDeliberatelyDoesNotReplay()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        var subscribing = gateway.ExpectSubscribeAsync(Expect(["AAPL"], id: 1), isLast: true, Cancel);
        var sent = await client.SubscribeAsync(Subscribe(["AAPL"]), Cancel);
        await subscribing;

        await gateway.CloseAsync();

        var rehandshake = gateway.AuthenticateAsync(SecondSessionId, cancellationToken: Cancel);
        await client.ReconnectAsync(Cancel);
        await rehandshake;

        // Kept, and kept intact: the start is cleared by ResubscribeAsync, which is what actually
        // sends them, not by the reconnect that merely preserves them.
        Assert.Same(sent, Assert.Single(client.Subscriptions));

        // And nothing was sent. The gateway reading an ordinary subscription as the first line of
        // the new connection is the assertion — had the reconnect replayed anything, this read
        // would have found the replay instead.
        var next = gateway.ExpectSubscribeAsync(Expect(["MSFT"], id: 2), isLast: true, Cancel);
        await client.SubscribeAsync(Subscribe(["MSFT"]), Cancel);
        await next;
    }

    // -------------------------------------------------------------------------- Resubscribing

    [Fact]
    public async Task ResubscribeAsync_ReplaysEverySubscriptionExactlyAsSentExceptForItsStart()
    {
        var replayed = new Subscription
        {
            Schema = Schema.Trades,
            Symbols = Symbols.From(["AAPL", "MSFT"]),
            Start = Instant.FromUnixTimeSeconds(1_609_160_400) + Duration.FromNanoseconds(1),
        };

        var live = new Subscription
        {
            Schema = Schema.Mbo,
            Symbols = Symbols.From("NVDA"),
            StypeIn = SType.RawSymbol,
            UseSnapshot = true,
        };

        var firstExpectation = new ExpectedSubscription
        {
            Schema = Schema.Trades,
            StypeIn = SType.RawSymbol,
            Symbols = ["AAPL", "MSFT"],
            Start = replayed.Start,
            Id = 1,
        };

        var secondExpectation = new ExpectedSubscription
        {
            Schema = Schema.Mbo,
            StypeIn = SType.RawSymbol,
            Symbols = ["NVDA"],
            UseSnapshot = true,
            Id = 2,
        };

        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        var subscribing = ExpectInOrderAsync(gateway, firstExpectation, secondExpectation);
        await client.SubscribeAsync(replayed, Cancel);
        await client.SubscribeAsync(live, Cancel);
        await subscribing;

        await gateway.CloseAsync();
        var rehandshake = gateway.AuthenticateAsync(SecondSessionId, cancellationToken: Cancel);
        await client.ReconnectAsync(Cancel);
        await rehandshake;

        // The whole issue, in one line each: the same expectations, with only the start dropped.
        // Every other field — schema, stype_in, symbols, snapshot, is_last, id — is required to be
        // what the first session sent, and an ExpectedSubscription with a null Start makes the
        // gateway require the field to be *absent* rather than zero.
        var replay = ExpectInOrderAsync(
            gateway,
            firstExpectation with { Start = null },
            secondExpectation);

        await client.ResubscribeAsync(Cancel);
        await replay;
    }

    [Fact]
    public async Task ResubscribeAsync_ClearsTheRetainedStartsSoASecondReplayCannotResendThem()
    {
        var start = Instant.FromUnixTimeSeconds(1_609_160_400);
        var expected = new ExpectedSubscription
        {
            Schema = Schema.Trades,
            StypeIn = SType.RawSymbol,
            Symbols = ["AAPL"],
            Start = start,
            Id = 1,
        };

        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        var subscribing = gateway.ExpectSubscribeAsync(expected, isLast: true, Cancel);
        await client.SubscribeAsync(
            new Subscription { Schema = Schema.Trades, Symbols = Symbols.From("AAPL"), Start = start },
            Cancel);
        await subscribing;

        Assert.Equal(start, Assert.Single(client.Subscriptions).Start);

        var replay = gateway.ExpectSubscribeAsync(expected with { Start = null }, isLast: true, Cancel);
        await client.ResubscribeAsync(Cancel);
        await replay;

        // Subscriptions reports what was last sent, which is also what stops a *second* reconnect
        // from replaying a start this one already dropped. Upstream mutates its stored
        // subscriptions in place for the same reason.
        Assert.Null(Assert.Single(client.Subscriptions).Start);

        var again = gateway.ExpectSubscribeAsync(expected with { Start = null }, isLast: true, Cancel);
        await client.ResubscribeAsync(Cancel);
        await again;
    }

    [Fact]
    public async Task ResubscribeAsync_WithNothingSubscribed_WritesNothing()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        await client.ResubscribeAsync(Cancel);

        Assert.Empty(client.Subscriptions);

        // The gateway reading an ordinary subscription as the first line it ever saw is what shows
        // the resubscribe wrote nothing at all rather than something the gateway ignored.
        var expectation = gateway.ExpectSubscribeAsync(Expect(["AAPL"], id: 1), isLast: true, Cancel);
        await client.SubscribeAsync(Subscribe(["AAPL"]), Cancel);
        await expectation;
    }

    [Fact]
    public async Task ResubscribeAsync_ChunksAtFiveHundredSymbolsExactlyAsSubscribeDoes()
    {
        var symbols = Enumerable.Range(0, 501).Select(i => $"SYM{i}").ToArray();

        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        var subscribing = ExpectChunksAsync(gateway, symbols, id: 1);
        await client.SubscribeAsync(Subscribe(symbols), Cancel);
        await subscribing;

        var replay = ExpectChunksAsync(gateway, symbols, id: 1);
        await client.ResubscribeAsync(Cancel);
        await replay;
    }

    [Fact]
    public async Task ResubscribeAsync_RaisesTheIdCounterPastTheIdsItReplayed()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        // An id the caller chose, which SubscribeAsync records without counting. Upstream's
        // resubscribe raises its counter to cover exactly this case.
        var chosen = gateway.ExpectSubscribeAsync(Expect(["AAPL"], id: 100), isLast: true, Cancel);
        await client.SubscribeAsync(Subscribe(["AAPL"]) with { Id = 100 }, Cancel);
        await chosen;

        var replay = gateway.ExpectSubscribeAsync(Expect(["AAPL"], id: 100), isLast: true, Cancel);
        await client.ResubscribeAsync(Cancel);
        await replay;

        var next = gateway.ExpectSubscribeAsync(Expect(["MSFT"], id: 101), isLast: true, Cancel);
        var assigned = await client.SubscribeAsync(Subscribe(["MSFT"]), Cancel);
        await next;

        Assert.Equal(101u, assigned.Id);
    }

    [Fact]
    public async Task ResubscribeAsync_WhenNotConnected_Throws()
    {
        await using var client = new LiveClient { ApiKey = TestKey(), Dataset = DatasetName };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ResubscribeAsync(Cancel));

        Assert.Contains("not connected", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResubscribeAsync_WhenConnectedButNotAuthenticated_Throws()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);

        var accepted = gateway.AcceptAsync(Cancel);
        await client.ConnectAsync(Cancel);
        await accepted;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ResubscribeAsync(Cancel));

        Assert.Contains("has not authenticated", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------ Heartbeats

    [Fact]
    public async Task Heartbeats_ArriveAsOrdinaryRecordsInTheStream()
    {
        const ulong TsEvent = 1_688_428_800_000_000_007UL;

        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        var serving = gateway.StartAsync(Cancel);
        await client.StartAsync(Cancel);
        await serving;

        // Interleaved with real records, because that is how they arrive: a heartbeat is framed
        // like every other record and shares the stream with them. A client that expected a
        // separate control channel for it would desynchronise here rather than skip it.
        await gateway.SendRecordAsync(SyntheticMbo.Record(1), Cancel);
        await gateway.SendRecordAsync(SyntheticSystemMsg.Heartbeat(TsEvent), Cancel);
        await gateway.SendRecordAsync(SyntheticMbo.Record(2), Cancel);

        Assert.Equal(1u, await ReadSequenceAsync(client));

        var heartbeat = await ReadRecordAsync(client);
        Assert.True(heartbeat.Has<SystemMsg>());

        ref readonly var system = ref heartbeat.Get<SystemMsg>();
        Assert.Equal(SystemCode.Heartbeat, system.Code);
        Assert.Equal(TsEvent, system.Header.TsEvent);
        Assert.Equal(TsEvent, system.IndexTs);
        Assert.Equal(SyntheticSystemMsg.HeartbeatText, system.Msg.ToString());

        // And the stream is still in step afterwards, which is the half that a heartbeat of the
        // wrong length would break.
        Assert.Equal(2u, await ReadSequenceAsync(client));
    }

    [Fact]
    public async Task FillBufferAsync_WithOnlyAHeartbeatIntervalConfigured_UsesTheDerivedBudget()
    {
        // Ten seconds of wall clock, and it cannot be fewer: the gateway's shortest legal heartbeat
        // interval is five seconds and upstream's margin is another five. Asserting the arithmetic
        // is cheap and LiveClientRecordLoopTests already does it; this asserts that the number the
        // arithmetic produces is the one the read actually runs on, which is the number a real
        // deployment lives or dies by. One test pays for that, deliberately.
        //
        // Those ten seconds are also the longest window in the repo in which a test process emits
        // nothing at all, and that is a second cost worth knowing about. The VSTest adapter calls a
        // run crashed when the process exits and the newest message it has delivered is older than
        // its crash-detection idle timeout; this test is the last thing running, so the timeout is
        // measured against this silence and nothing else. Sixty seconds is the budget
        // Directory.Packages.props buys, and #62 is what five bought. A test that goes quiet for
        // longer than this one has to be weighed against that number, not against the clock.
        var interval = LiveClient.MinHeartbeatInterval;
        var expected = interval + LiveClient.ReadTimeoutHeartbeatMargin;

        await using var gateway = new MockLiveGateway(DatasetName) { Timeout = Duration.FromSeconds(30) };
        await using var client = new LiveClient
        {
            ApiKey = new ApiKey(MockLiveGateway.TestApiKey),
            Dataset = gateway.Dataset,
            Gateway = gateway.Address,
            HeartbeatInterval = interval,
        };

        Assert.Equal(expected, client.EffectiveReadTimeout);

        var handshake = gateway.AuthenticateAsync(interval, Cancel);
        await client.ConnectAsync(Cancel);
        await client.AuthenticateAsync(Cancel);
        await handshake;

        var serving = gateway.StartAsync(Cancel);
        await client.StartAsync(Cancel);
        await serving;

        var started = SystemClock.Instance.GetCurrentInstant();

        var error = await Assert.ThrowsAsync<HeartbeatTimeoutException>(
            async () => await client.FillBufferAsync(Cancel));

        var elapsed = SystemClock.Instance.GetCurrentInstant() - started;

        Assert.Equal(expected, error.Timeout);

        // The equality above is what catches a client running on the wrong budget; this is what
        // catches one that *reports* the derived budget while waiting on some other number, which
        // no assertion about the exception could ever see. A lower bound only: an upper one would
        // be asserting how promptly a timer fires on a loaded CI runner, which is not a property
        // of this library.
        Assert.True(
            elapsed >= expected - Duration.FromMilliseconds(250),
            $"The read budget derived from a {interval} heartbeat interval should be {expected}; "
            + $"the read gave up after {elapsed}.");
    }

    // ------------------------------------------------------------------------------- Helpers

    /// <summary>
    /// Reads one record, filling the buffer as often as it takes.
    /// </summary>
    private static async Task<OwnedRecord> ReadRecordAsync(LiveClient client)
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

    /// <summary>Reads one record and returns the <see cref="MboMsg.Sequence"/> it carries.</summary>
    private static async Task<uint> ReadSequenceAsync(LiveClient client)
    {
        var record = await ReadRecordAsync(client);
        Assert.True(record.TryGet<MboMsg>(out var mbo));
        return mbo.Sequence;
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

    /// <summary>Reads until the gateway's end of stream, discarding whatever is left.</summary>
    private static async Task DrainToEndAsync(LiveClient client)
    {
        while (await client.FillBufferAsync(Cancel) != 0)
        {
            while (client.TryNextRecord(out _))
            {
            }
        }
    }

    /// <summary>
    /// Reads one subscription line per expectation, in order, each marked <c>is_last=1</c>.
    /// </summary>
    private static async Task ExpectInOrderAsync(
        MockLiveGateway gateway,
        params ExpectedSubscription[] expectations)
    {
        foreach (var expectation in expectations)
        {
            await gateway.ExpectSubscribeAsync(expectation, isLast: true, Cancel);
        }
    }

    /// <summary>
    /// Reads the two lines a 501-symbol subscription is split into, only the second of which is
    /// marked last.
    /// </summary>
    private static async Task ExpectChunksAsync(MockLiveGateway gateway, string[] symbols, uint id)
    {
        await gateway.ExpectSubscribeAsync(Expect(symbols[..500], id), isLast: false, Cancel);
        await gateway.ExpectSubscribeAsync(Expect(symbols[500..], id), isLast: true, Cancel);
    }

    private static Subscription Subscribe(IReadOnlyList<string> symbols) => new()
    {
        Schema = Schema.Trades,
        Symbols = Symbols.From(symbols),
    };

    private static ExpectedSubscription Expect(IReadOnlyList<string> symbols, uint? id = null) => new()
    {
        Schema = Schema.Trades,
        StypeIn = SType.RawSymbol,
        Symbols = symbols,
        Id = id,
    };

    private static async Task HandshakeAsync(MockLiveGateway gateway, LiveClient client)
    {
        var handshake = gateway.AuthenticateAsync(client.HeartbeatInterval, Cancel);
        await client.ConnectAsync(Cancel);
        await client.AuthenticateAsync(Cancel);
        await handshake;
    }

    private static ApiKey TestKey() => new(MockLiveGateway.TestApiKey);

    private static LiveClient Client(MockLiveGateway gateway) => new()
    {
        ApiKey = TestKey(),
        Dataset = gateway.Dataset,
        Gateway = gateway.Address,
    };
}
