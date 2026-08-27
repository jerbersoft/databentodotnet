using DatabentoDotNet.Dbn;
using DatabentoDotNet.Dbn.Publishers;
using NodaTime;

namespace DatabentoDotNet.Live.Tests;

/// <summary>
/// Tests for <see cref="LiveClient.SubscribeAsync"/>: the message the client sends, the
/// 500-symbol chunking rule, and the two combinations it refuses to send at all.
/// </summary>
/// <remarks>
/// <para>
/// Run against <see cref="MockLiveGateway.ExpectSubscribeAsync"/>, which checks every field of
/// every line it reads against an <see cref="ExpectedSubscription"/> the test states in the
/// harness's own terms. The client's <see cref="Subscription"/> never crosses that boundary, so a
/// test is not handing the expectation and the implementation the same object — see
/// <see cref="ExpectedSubscription"/> for why the duplication is deliberate.
/// </para>
/// <para>
/// <b>The rejection tests assert on the gateway, not only on the throw.</b> An
/// <see cref="ArgumentException"/> proves the client noticed; the gateway having read nothing
/// proves it noticed <em>before</em> writing, which is the part that matters — a half-written
/// line desynchronises the gateway even when the exception looks the same from the caller's side.
/// </para>
/// </remarks>
public class LiveClientSubscriptionTests
{
    private static readonly string DatasetName = Dataset.XnasItch.ToWireString();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------------ The message

    [Fact]
    public async Task SubscribeAsync_SendsOneMessageWithEveryFieldTheGatewayExpects()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        var expectation = gateway.ExpectSubscribeAsync(
            new ExpectedSubscription
            {
                Schema = Schema.Trades,
                StypeIn = SType.RawSymbol,
                Symbols = ["AAPL", "MSFT"],
                Id = 1,
            },
            isLast: true,
            Cancel);

        var sent = await client.SubscribeAsync(
            new Subscription { Schema = Schema.Trades, Symbols = Symbols.From(["AAPL", "MSFT"]) },
            Cancel);

        await expectation;
        Assert.Equal(1u, sent.Id);
    }

    [Fact]
    public async Task SubscribeAsync_DefaultsStypeInToRawSymbol()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        var subscription = new Subscription { Schema = Schema.Mbp1, Symbols = Symbols.All };
        Assert.Equal(SType.RawSymbol, subscription.StypeIn);

        var expectation = gateway.ExpectSubscribeAsync(
            new ExpectedSubscription
            {
                Schema = Schema.Mbp1,
                StypeIn = SType.RawSymbol,
                Symbols = ["ALL_SYMBOLS"],
            },
            isLast: true,
            Cancel);

        await client.SubscribeAsync(subscription, Cancel);
        await expectation;
    }

    [Fact]
    public async Task SubscribeAsync_SendsInstrumentIdsUnderTheSymbologyThatReadsThem()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        var expectation = gateway.ExpectSubscribeAsync(
            new ExpectedSubscription
            {
                Schema = Schema.Trades,
                StypeIn = SType.InstrumentId,
                Symbols = ["101", "202"],
            },
            isLast: true,
            Cancel);

        await client.SubscribeAsync(
            new Subscription
            {
                Schema = Schema.Trades,
                StypeIn = SType.InstrumentId,
                Symbols = Symbols.FromIds([101u, 202u]),
            },
            Cancel);

        await expectation;
    }

    [Fact]
    public async Task SubscribeAsync_SendsAReplayStartAsExactUnixNanoseconds()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        // One nanosecond past a whole second. A DateTime tick is 100 ns, so a client that went
        // through the BCL would send …000 and the gateway would replay from a different moment.
        var start = Instant.FromUnixTimeSeconds(1_609_160_400) + Duration.FromNanoseconds(1);

        var expectation = gateway.ExpectSubscribeAsync(
            new ExpectedSubscription
            {
                Schema = Schema.Trades,
                StypeIn = SType.RawSymbol,
                Symbols = ["AAPL"],
                Start = start,
            },
            isLast: true,
            Cancel);

        await client.SubscribeAsync(
            new Subscription { Schema = Schema.Trades, Symbols = Symbols.From("AAPL"), Start = start },
            Cancel);

        var fields = await expectation;
        Assert.Equal("1609160400000000001", fields["start"]);
    }

    [Fact]
    public async Task SubscribeAsync_WithoutAStart_OmitsTheFieldEntirely()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        // ExpectedSubscription.Start of null makes the gateway require the field to be absent, so
        // sending start=0 — a plausible way to spell "no replay" — fails this rather than passing.
        var expectation = gateway.ExpectSubscribeAsync(
            new ExpectedSubscription
            {
                Schema = Schema.Trades,
                StypeIn = SType.RawSymbol,
                Symbols = ["AAPL"],
            },
            isLast: true,
            Cancel);

        await client.SubscribeAsync(
            new Subscription { Schema = Schema.Trades, Symbols = Symbols.From("AAPL") },
            Cancel);

        await expectation;
    }

    [Fact]
    public async Task SubscribeAsync_SendsASnapshotRequestOnMbo()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        var expectation = gateway.ExpectSubscribeAsync(
            new ExpectedSubscription
            {
                Schema = Schema.Mbo,
                StypeIn = SType.RawSymbol,
                Symbols = ["AAPL"],
                UseSnapshot = true,
            },
            isLast: true,
            Cancel);

        await client.SubscribeAsync(
            new Subscription
            {
                Schema = Schema.Mbo,
                Symbols = Symbols.From("AAPL"),
                UseSnapshot = true,
            },
            Cancel);

        await expectation;
    }

    // -------------------------------------------------------------------- The chunking rule

    [Fact]
    public async Task SubscribeAsync_FiveHundredSymbols_IsOneMessageMarkedLast()
    {
        var symbols = Series("SYM", 500);

        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        var expectation = gateway.ExpectSubscribeAsync(
            Expect(symbols),
            isLast: true,
            Cancel);

        await client.SubscribeAsync(Subscribe(symbols), Cancel);
        await expectation;
    }

    [Fact]
    public async Task SubscribeAsync_FiveHundredAndOneSymbols_IsTwoMessagesAndOnlyTheSecondIsLast()
    {
        var symbols = Series("SYM", 501);

        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        var expectation = ExpectChunksAsync(gateway, symbols, [500, 1]);

        await client.SubscribeAsync(Subscribe(symbols), Cancel);
        await expectation;
    }

    [Fact]
    public async Task SubscribeAsync_AThousandSymbols_IsTwoFullMessages()
    {
        var symbols = Series("SYM", 1000);

        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        var expectation = ExpectChunksAsync(gateway, symbols, [500, 500]);

        await client.SubscribeAsync(Subscribe(symbols), Cancel);
        await expectation;
    }

    [Fact]
    public async Task SubscribeAsync_EveryChunkOfOneSubscriptionCarriesTheSameId()
    {
        var symbols = Series("SYM", 1001);

        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        var expectation = ExpectChunksAsync(gateway, symbols, [500, 500, 1], id: 1);

        var sent = await client.SubscribeAsync(Subscribe(symbols), Cancel);

        await expectation;

        // Three lines, one subscription: an id assigned per chunk would make the gateway treat
        // them as three separate, mostly-incomplete subscriptions.
        Assert.Equal(1u, sent.Id);
        Assert.Single(client.Subscriptions);
    }

    // ------------------------------------------------------------------- Ids and bookkeeping

    [Fact]
    public async Task SubscribeAsync_AssignsIdsFromOneUpwards()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        for (var expected = 1u; expected <= 3u; expected++)
        {
            var expectation = gateway.ExpectSubscribeAsync(
                new ExpectedSubscription
                {
                    Schema = Schema.Trades,
                    StypeIn = SType.RawSymbol,
                    Symbols = ["AAPL"],
                    Id = expected,
                },
                isLast: true,
                Cancel);

            var sent = await client.SubscribeAsync(
                new Subscription { Schema = Schema.Trades, Symbols = Symbols.From("AAPL") },
                Cancel);

            await expectation;
            Assert.Equal(expected, sent.Id);
        }

        Assert.Equal(3, client.Subscriptions.Count);
        Assert.Equal([1u, 2u, 3u], client.Subscriptions.Select(s => s.Id));
    }

    [Fact]
    public async Task SubscribeAsync_KeepsAnIdTheCallerChose()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        var expectation = gateway.ExpectSubscribeAsync(
            new ExpectedSubscription
            {
                Schema = Schema.Trades,
                StypeIn = SType.RawSymbol,
                Symbols = ["AAPL"],
                Id = 42,
            },
            isLast: true,
            Cancel);

        var sent = await client.SubscribeAsync(
            new Subscription { Schema = Schema.Trades, Symbols = Symbols.From("AAPL"), Id = 42 },
            Cancel);

        await expectation;
        Assert.Equal(42u, sent.Id);
    }

    [Fact]
    public async Task Subscriptions_SurviveCloseAsync_SoAReconnectHasSomethingToReplay()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        var expectation = gateway.ExpectSubscribeAsync(
            new ExpectedSubscription
            {
                Schema = Schema.Trades,
                StypeIn = SType.RawSymbol,
                Symbols = ["AAPL"],
            },
            isLast: true,
            Cancel);

        await client.SubscribeAsync(
            new Subscription { Schema = Schema.Trades, Symbols = Symbols.From("AAPL") },
            Cancel);
        await expectation;

        await client.CloseAsync();

        Assert.Single(client.Subscriptions);
        Assert.Equal(Schema.Trades, client.Subscriptions[0].Schema);
    }

    // --------------------------------------------------------- Rejected before the first byte

    [Fact]
    public async Task SubscribeAsync_ASnapshotWithAReplayStart_WritesNothingToTheSocket()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        var subscription = new Subscription
        {
            Schema = Schema.Mbo,
            Symbols = Symbols.From("AAPL"),
            UseSnapshot = true,
            Start = NodaConstants.UnixEpoch,
        };

        await AssertRejectedWithoutWritingAsync(gateway, client, subscription, "snapshot and an intraday-replay start");
    }

    [Theory]
    [InlineData(Schema.Mbp1)]
    [InlineData(Schema.Trades)]
    [InlineData(Schema.Ohlcv1S)]
    [InlineData(Schema.Definition)]
    public async Task SubscribeAsync_ASnapshotOnAnySchemaButMbo_WritesNothingToTheSocket(Schema schema)
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        var subscription = new Subscription
        {
            Schema = schema,
            Symbols = Symbols.From("AAPL"),
            UseSnapshot = true,
        };

        await AssertRejectedWithoutWritingAsync(gateway, client, subscription, "supports snapshots");
    }

    [Fact]
    public async Task SubscribeAsync_EverySchemaOtherThanMbo_RejectsASnapshot()
    {
        // The theory above covers four; this covers the other fifteen, so a schema added in a
        // future dbn release cannot quietly acquire snapshot support by being forgotten here.
        foreach (var schema in Enum.GetValues<Schema>().Where(s => s != Schema.Mbo))
        {
            var subscription = new Subscription
            {
                Schema = schema,
                Symbols = Symbols.From("AAPL"),
                UseSnapshot = true,
            };

            var exception = await Assert.ThrowsAsync<ArgumentException>(
                () => DisconnectedClient().SubscribeAsync(subscription, Cancel));

            Assert.Contains("supports snapshots", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SubscribeAsync_ADefaultSymbolsValue_WritesNothingToTheSocket()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);
        await HandshakeAsync(gateway, client);

        var subscription = new Subscription { Schema = Schema.Trades, Symbols = default };

        await AssertRejectedWithoutWritingAsync(gateway, client, subscription, "names nothing");
    }

    [Fact]
    public async Task SubscribeAsync_ValidatesBeforeItChecksTheConnection()
    {
        // A subscription this client would never send should be rejected the same way whether or
        // not a socket happens to be open, so the caller's bug does not depend on their timing.
        var subscription = new Subscription
        {
            Schema = Schema.Trades,
            Symbols = Symbols.From("AAPL"),
            UseSnapshot = true,
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => DisconnectedClient().SubscribeAsync(subscription, Cancel));
    }

    // ------------------------------------------------------------------------- State checks

    [Fact]
    public async Task SubscribeAsync_Null_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => DisconnectedClient().SubscribeAsync(null!, Cancel));
    }

    [Fact]
    public async Task SubscribeAsync_WhenNotConnected_Throws()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DisconnectedClient().SubscribeAsync(
                new Subscription { Schema = Schema.Trades, Symbols = Symbols.All },
                Cancel));

        Assert.Contains("not connected", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubscribeAsync_WhenConnectedButNotAuthenticated_Throws()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);

        var accepted = gateway.AcceptAsync(Cancel);
        await client.ConnectAsync(Cancel);
        await accepted;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SubscribeAsync(
                new Subscription { Schema = Schema.Trades, Symbols = Symbols.All },
                Cancel));

        Assert.Contains("has not authenticated", exception.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------ Helpers

    private static string[] Series(string prefix, int count) =>
        Enumerable.Range(0, count).Select(i => $"{prefix}{i}").ToArray();

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

    /// <summary>
    /// Expects one <see cref="MockLiveGateway.ExpectSubscribeAsync"/> per entry in
    /// <paramref name="chunkSizes"/>, each carrying the matching slice of
    /// <paramref name="symbols"/> and only the last marked <c>is_last=1</c>.
    /// </summary>
    private static async Task ExpectChunksAsync(
        MockLiveGateway gateway,
        string[] symbols,
        int[] chunkSizes,
        uint? id = null)
    {
        var offset = 0;
        for (var i = 0; i < chunkSizes.Length; i++)
        {
            var chunk = symbols.Skip(offset).Take(chunkSizes[i]).ToArray();
            offset += chunkSizes[i];

            await gateway.ExpectSubscribeAsync(
                Expect(chunk, id),
                isLast: i == chunkSizes.Length - 1,
                Cancel);
        }

        Assert.Equal(symbols.Length, offset);
    }

    /// <summary>
    /// Asserts that <paramref name="subscription"/> throws and that the gateway never sees a byte
    /// of it — the half of the contract a throw alone does not establish.
    /// </summary>
    private static async Task AssertRejectedWithoutWritingAsync(
        MockLiveGateway gateway,
        LiveClient client,
        Subscription subscription,
        string expectedMessageFragment)
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => client.SubscribeAsync(subscription, Cancel));

        Assert.Contains(expectedMessageFragment, exception.Message, StringComparison.Ordinal);

        // The client must still be usable: a rejection before the write leaves the socket
        // synchronised, so an ordinary subscription goes through on the same connection.
        var expectation = gateway.ExpectSubscribeAsync(
            new ExpectedSubscription
            {
                Schema = Schema.Trades,
                StypeIn = SType.RawSymbol,
                Symbols = ["AAPL"],
                Id = 1,
            },
            isLast: true,
            Cancel);

        await client.SubscribeAsync(
            new Subscription { Schema = Schema.Trades, Symbols = Symbols.From("AAPL") },
            Cancel);

        // The gateway reading this as the *first* line it ever saw is the assertion: had the
        // rejected subscription written anything, this read would have found that instead.
        await expectation;
        Assert.True(client.IsConnected);
    }

    private static async Task HandshakeAsync(MockLiveGateway gateway, LiveClient client)
    {
        var handshake = gateway.AuthenticateAsync(cancellationToken: Cancel);
        await client.ConnectAsync(Cancel);
        await client.AuthenticateAsync(Cancel);
        await handshake;
    }

    private static LiveClient DisconnectedClient() => new()
    {
        ApiKey = new ApiKey(MockLiveGateway.TestApiKey),
        Dataset = DatasetName,
    };

    private static LiveClient Client(MockLiveGateway gateway) => new()
    {
        ApiKey = new ApiKey(MockLiveGateway.TestApiKey),
        Dataset = gateway.Dataset,
        Gateway = gateway.Address,
    };
}
