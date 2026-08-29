using DatabentoDotNet.Dbn;
using DatabentoDotNet.Dbn.Publishers;
using DatabentoDotNet.Live;
using DatabentoDotNet.Live.Tests;

namespace DatabentoDotNet.AotProbe;

/// <summary>
/// A whole live session — connect, CRAM authentication, subscribe, start, record loop — against
/// <see cref="MockLiveGateway"/> over a loopback socket, inside the native binary. Once plain, once
/// with the session compressed.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gateway is compiled from the Live test project, not rewritten here.</b> A smaller
/// loopback gateway written for this program would be a third implementation of the live protocol
/// after the mock and the real one, and CLAUDE.md's argument against that is not about effort: two
/// implementations written from the same reading agree with each other, and a third only widens the
/// surface a misreading can hide in. Compiling the same file costs a line in the project file and
/// cannot drift.
/// </para>
/// <para>
/// <b>This is the most AOT-exotic path in the repository.</b> The async read seam projects
/// <c>AlignedBuffer</c>'s <c>ulong[]</c> as a <see cref="Memory{T}"/> through a
/// <see cref="System.Buffers.MemoryManager{T}"/>, because there is no <c>ReadAsync(Span&lt;byte&gt;)</c>
/// and no <c>Memory&lt;T&gt;</c> reinterpret cast (#15). Records are then reinterpreted in place over
/// that buffer. Nothing about it is unsafe for ILC in principle, and nothing had ever run it under
/// ILC in practice, which is the gap this closes.
/// </para>
/// <para>
/// <b>The zstd session is the second half.</b> <c>DatabentoDotNet.Live</c> reaches the decompressor
/// by compiling <c>Internal/ZstdDecompressor.cs</c> from the codec rather than through an
/// <c>InternalsVisibleTo</c>, so a session that negotiated <c>compression=zstd</c> runs through a
/// copy of that seam the corpus decode never touches.
/// </para>
/// </remarks>
internal static class LiveSessionProbe
{
    private const int RecordCount = 64;

    private static readonly string[] ProbeSymbols = ["AAPL", "MSFT"];

    public static async Task RunAsync(ProbeReport report, CancellationToken cancellationToken)
    {
        ProbeReport.Section("live: a session over the mock gateway");
        await SessionAsync(report, Compression.None, cancellationToken).ConfigureAwait(false);
        await SessionAsync(report, Compression.Zstd, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SessionAsync(ProbeReport report, Compression compression, CancellationToken cancellationToken)
    {
        var label = compression == Compression.Zstd ? "zstd session" : "plain session";
        var dataset = Dataset.XnasItch.ToWireString();

        await using var gateway = new MockLiveGateway(dataset) { ExpectedCompression = compression };
        await using var client = new LiveClient
        {
            ApiKey = new ApiKey(MockLiveGateway.TestApiKey),
            Dataset = dataset,
            Gateway = gateway.Address,
            Compression = compression,
        };

        var handshake = gateway.AuthenticateAsync(client.HeartbeatInterval, cancellationToken);
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await client.AuthenticateAsync(cancellationToken).ConfigureAwait(false);
        await handshake.ConfigureAwait(false);

        report.Require(client.IsAuthenticated, $"{label}: the CRAM handshake completed");
        report.RequireEqual(MockLiveGateway.SessionId, client.SessionId ?? "<null>", $"{label}: the session id came back");

        var subscribing = gateway.ExpectSubscribeAsync(
            new ExpectedSubscription
            {
                Schema = Schema.Mbo,
                StypeIn = SType.RawSymbol,
                Symbols = ProbeSymbols,
            },
            isLast: true,
            cancellationToken);

        var subscription = await client
            .SubscribeAsync(
                new Subscription
                {
                    Schema = Schema.Mbo,
                    StypeIn = SType.RawSymbol,
                    Symbols = Symbols.From(ProbeSymbols),
                },
                cancellationToken)
            .ConfigureAwait(false);

        await subscribing.ConfigureAwait(false);
        report.Require(subscription.Id is not null, $"{label}: the subscription was given an id");

        var serving = compression == Compression.Zstd
            ? gateway.StartCompressedAsync(cancellationToken)
            : gateway.StartAsync(cancellationToken);
        var metadata = await client.StartAsync(cancellationToken).ConfigureAwait(false);
        await serving.ConfigureAwait(false);

        report.RequireEqual(dataset, metadata.Dataset, $"{label}: the metadata block names the dataset");
        report.Require(!metadata.TsOut, $"{label}: the session carries no ts_out");

        var sent = SyntheticMbo.Records(RecordCount);
        foreach (var record in sent)
        {
            await gateway.SendRecordAsync(record, cancellationToken).ConfigureAwait(false);
        }

        await gateway.CloseAsync().ConfigureAwait(false);

        var (decoded, sequences, bytes) = await DrainAsync(client, cancellationToken).ConfigureAwait(false);

        report.RequireEqual(RecordCount, decoded, $"{label}: every record sent came back");
        report.RequireEqual(RecordCount, sequences.Count, $"{label}: every record read back as an MboMsg");
        report.Require(
            sequences.SequenceEqual(sent.Select(record => record.Sequence)),
            $"{label}: the sequence numbers arrived in order and intact");
        report.Require(client.IsClosed, $"{label}: the client noticed the gateway closing");

        ProbeReport.Note($"{label}: {decoded} records, {bytes} bytes, over a loopback socket.");
    }

    /// <summary>
    /// Reads the stream to its end through the zero-copy pair, reinterpreting each record.
    /// </summary>
    /// <remarks>
    /// Drain, then fill, then drain once more: the fill that returns zero comes after records the
    /// loop has only drained <em>before</em> it, so without the last drain the tail is dropped. Same
    /// shape as <c>LiveClientRecordLoopTests.DrainAsync</c>, and it is the shape because
    /// <c>TryNextRecord</c> hands back a <c>ref struct</c> that cannot survive an <c>await</c> —
    /// which is why there is no <c>Task&lt;RecordRef&gt;</c> and never can be.
    /// </remarks>
    private static async Task<(int Decoded, List<uint> Sequences, long Bytes)> DrainAsync(
        LiveClient client,
        CancellationToken cancellationToken)
    {
        var decoded = 0;
        var sequences = new List<uint>();
        var bytes = 0L;

        while (true)
        {
            Buffered(client, ref decoded, sequences, ref bytes);
            if (await client.FillBufferAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                break;
            }
        }

        Buffered(client, ref decoded, sequences, ref bytes);
        return (decoded, sequences, bytes);
    }

    private static void Buffered(LiveClient client, ref int decoded, List<uint> sequences, ref long bytes)
    {
        while (client.TryNextRecord(out var record))
        {
            decoded++;
            bytes += record.SizeInBytes;

            if (record.TryGet<MboMsg>(out var mbo))
            {
                sequences.Add(mbo.Sequence);
            }
        }
    }
}
