using DatabentoDotNet.Dbn;
using DatabentoDotNet.Dbn.Publishers;
using NodaTime;

namespace DatabentoDotNet.Live.Tests;

/// <summary>
/// The one test in this repository that starts a live session, and therefore the one that moves
/// billable data.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it has to exist, when <see cref="MockLiveGateway"/> exists.</b> The mock and the client
/// were written from the same reading of upstream's <c>live/protocol.rs</c>. A misreading of the
/// metadata block or of the record framing would therefore be present in both, and they would
/// agree with each other — <see cref="StubLiveClient"/> included, which is a second opinion from
/// the same source rather than a second source. Only a real gateway settles whether our reading
/// of the wire is the gateway's, and only after <c>start_session</c>: everything before that line
/// is a conversation about what to send later. ROADMAP.md §4, surface (2).
/// </para>
/// <para>
/// <b>Two gates, not one.</b> <c>Category=Live</c> keeps it out of CI, the same as
/// <see cref="RealGatewaySmokeTests"/>; <see cref="LiveCredentials.SessionVariable"/> keeps it out
/// of an ordinary local <c>dotnet test</c> even on a machine that has a key. Having a key
/// configured means a developer <em>can</em> reach the gateway, which is all the free smoke tests
/// need. It is not consent to spend money on every run.
/// </para>
/// <para>
/// <b>It is bounded three ways</b>, because an unbounded live subscription is a bill: two named
/// symbols rather than <c>ALL_SYMBOLS</c>, <see cref="MaxRecords"/> records, and a wall clock. The
/// first of the three to trip ends the session.
/// </para>
/// <para>
/// <b>It asks for heartbeats so it passes at 3am.</b> A five-second
/// <see cref="LiveClient.HeartbeatInterval"/> means the gateway sends a <c>SystemMsg</c> whenever
/// nothing else is due, so the test decodes records whether or not the market is open. That is
/// deliberate: a test that only passes during trading hours is a test nobody runs.
/// </para>
/// </remarks>
[Trait("Category", "Live")]
public class RealGatewaySessionTests
{
    /// <summary>How many records to take before closing. A handful, not a stream.</summary>
    private const int MaxRecords = 8;

    /// <summary>
    /// The <c>publisher_id</c> a record carries when the gateway generated it rather than relaying
    /// it from a venue. Not a valid <see cref="Publisher"/> — that enum starts at one.
    /// </summary>
    private const ushort NoPublisher = 0;

    /// <summary>Gate for the <c>SkipUnless</c> below. Both halves must be satisfied.</summary>
    public static bool IsAllowed => LiveCredentials.IsSessionAllowed;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact(SkipUnless = nameof(IsAllowed), Skip = LiveCredentials.SessionSkipReason)]
    public async Task Lifecycle_AgainstTheRealGateway_StreamsRecordsAndClosesCleanly()
    {
        await using var client = new LiveClient
        {
            ApiKey = LiveCredentials.ApiKey,
            Dataset = LiveCredentials.Dataset,
            ConnectTimeout = Duration.FromSeconds(15),
            AuthTimeout = Duration.FromSeconds(15),

            // Whole seconds and at the documented floor: the client rejects a fractional interval
            // rather than truncating it, so this is also the smallest legal value.
            HeartbeatInterval = LiveClient.MinHeartbeatInterval,
            ReadTimeout = Duration.FromSeconds(20),
        };

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        deadline.CancelAfter(checked((int)Duration.FromSeconds(60).TotalMilliseconds));

        await client.ConnectAsync(deadline.Token);

        try
        {
            await client.AuthenticateAsync(deadline.Token);
        }
        catch (DatabentoAuthenticationException rejected)
            when (rejected.Error?.Contains("live data license", StringComparison.OrdinalIgnoreCase) == true)
        {
            Assert.Fail(
                $"The account has no *live* data license for '{LiveCredentials.Dataset}'. Set "
                + $"{LiveCredentials.DatasetVariable} and {LiveCredentials.SchemaVariable} in .env to a "
                + $"dataset and schema this account is licensed for live. The gateway said: {rejected.Error}");
        }

        if (!WireStrings.TryParseSchema(LiveCredentials.Schema, out var schema))
        {
            Assert.Fail(
                $"{LiveCredentials.SchemaVariable}='{LiveCredentials.Schema}' is not a DBN schema this "
                + "build knows. Use a wire spelling such as 'trades' or 'ohlcv-1d'.");
        }

        await client.SubscribeAsync(
            new Subscription { Schema = schema, Symbols = Symbols.From(["AAPL", "MSFT"]) },
            deadline.Token);

        // ------------------------------------------------------------------ The billable line

        var metadata = await client.StartAsync(deadline.Token);

        // What the metadata block has to say for our reading of it to be right. The dataset is
        // the one field we can check against something we chose; the rest are checked for being
        // possible at all, which is what catches a block read at the wrong offset — a version of
        // 0 or 97, or a symbol length that is not one of the two DBN defines.
        Assert.Equal(LiveCredentials.Dataset, metadata.Dataset);
        Assert.InRange(metadata.Version, (byte)1, DbnConstants.Version);
        Assert.False(metadata.TsOut);
        Assert.Equal(Metadata.SymbolCstrLengthForVersion(metadata.Version), metadata.SymbolCstrLength);
        Assert.True(client.IsSessionStarted);
        Assert.False(client.IsClosed);

        var decoded = new List<OwnedRecord>();
        while (decoded.Count < MaxRecords)
        {
            DrainBuffered(client, decoded);
            if (decoded.Count >= MaxRecords || await client.FillBufferAsync(deadline.Token) == 0)
            {
                break;
            }
        }

        await client.CloseAsync();
        Assert.False(client.IsConnected);
        Assert.False(client.IsSessionStarted);

        // ------------------------------------------------------------ What the records prove

        Assert.NotEmpty(decoded);

        foreach (var record in decoded)
        {
            // Framing: the header's declared length is the number of bytes the decoder handed
            // back. A client that framed records one word out would have desynchronised long
            // before eight of them decoded, but this states the property rather than relying on
            // the crash.
            Assert.Equal(
                record.Header.Length * DbnConstants.RecordLengthMultiplier,
                record.SizeInBytes);
            Assert.False(record.HasTsOut);

            // Publisher zero is not a publisher. Records the gateway *generates* rather than
            // relays carry no publisher, and `Publisher` starts at one, so there is deliberately
            // no name for zero. Asserting every id is nameable would fail on the first heartbeat —
            // which this test asks for explicitly so that it passes outside trading hours — and on
            // the symbol mappings the gateway sends at the head of every session.
            //
            // Upstream builds all three that way (dbn `record/methods.rs`, and the same in
            // `v1/methods.rs`):
            //
            //     ErrorMsg::new          RecordHeader::new(rtype::ERROR,          0, 0,             ts)
            //     SystemMsg::heartbeat   RecordHeader::new(rtype::SYSTEM,         0, 0,             ts)
            //     SymbolMappingMsg::new  RecordHeader::new(rtype::SYMBOL_MAPPING, 0, instrument_id, ts)
            //
            // Note the third: no publisher, but a real instrument — it is naming an instrument the
            // session will stream, so the id is the point of the record.
            //
            // The cases are asserted apart rather than the check being dropped: zero means one of
            // those three, anything else has to be a publisher this build declares.
            if (record.Header.PublisherId == NoPublisher)
            {
                Assert.True(
                    record.Has<SystemMsg>() || record.Has<ErrorMsg>() || record.Has<SymbolMappingMsg>(),
                    $"Record of rtype {record.Header.RType} carries publisher {NoPublisher}, which "
                    + "only records the gateway generates are supposed to do.");

                // The two that carry no instrument either, kept distinct from the one that does.
                if (!record.Has<SymbolMappingMsg>())
                {
                    Assert.Equal(0u, record.Header.InstrumentId);
                }

                continue;
            }

            // A publisher id this build can name. If the gateway ever streams one the generated
            // table does not know about, this is where it surfaces.
            Assert.True(
                PublisherValues.TryFromPublisher(record.Header.PublisherId, out var publisher),
                $"Publisher id {record.Header.PublisherId} is not one this build declares.");
            _ = publisher.ToVenue();
        }

        // At least one record has to be something we can name: a heartbeat, or market data of the
        // schema we subscribed to. A stream of records that decoded structurally but matched no
        // known rtype would mean the framing happened to work and the contents did not.
        Assert.Contains(decoded, record => IsHeartbeat(record) || RTypeSchemaMapping.TryIntoSchema(record.Header.RType, out _));
    }

    private static bool IsHeartbeat(OwnedRecord record) =>
        record.Has<SystemMsg>() && record.Get<SystemMsg>().Code == SystemCode.Heartbeat;

    /// <summary>
    /// Copies out every record already buffered. Non-<c>async</c> because a
    /// <see cref="RecordRef"/> cannot be in scope across an <c>await</c>.
    /// </summary>
    private static void DrainBuffered(LiveClient client, List<OwnedRecord> decoded)
    {
        while (decoded.Count < MaxRecords && client.TryNextRecord(out var record))
        {
            decoded.Add(OwnedRecord.CopyOf(record));
        }
    }
}
