using DatabentoDotNet.Dbn;
using DatabentoDotNet.Dbn.Publishers;

namespace DatabentoDotNet.Live.Tests;

/// <summary>
/// The live half of
/// <see href="https://github.com/jerbersoft/databentodotnet/issues/28">#28</see>: reading records
/// off a socket through <see cref="LiveClient.FillBufferAsync"/> and
/// <see cref="LiveClient.TryNextRecord"/> allocates nothing per record.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not covered by the codec's own allocation tests.</b> Those drive
/// <see cref="DbnFsm"/> and <see cref="DbnDecoder"/> over bytes already in memory. The live path
/// adds the socket read seam, the <see cref="Memory{T}"/> projection it needs, and an
/// <c>async</c> method around both — and an <c>async</c> method is exactly where a per-call
/// allocation hides, because a state machine box, a
/// <see cref="CancellationTokenSource"/> and a cancellation registration are all invisible in the
/// source. M2's definition of done names this path specifically, not the codec's.
/// </para>
/// <para>
/// <b>Synthetic MBO, replayed by the mock gateway.</b> ROADMAP.md §4 records the reasoning: no
/// dataset this account licenses offers <c>mbo</c>, and allocation is a property of the code path
/// rather than of the data source — nothing between the socket and <see cref="RecordRef"/> can
/// tell a real gateway from <see cref="MockLiveGateway"/> replaying
/// <see cref="SyntheticMbo"/>. What the real gateway buys is protocol confidence, which is
/// <see cref="RealGatewaySessionTests"/>'s job.
/// </para>
/// <para>
/// <b>The batch is sized to fit the socket's receive buffer, and that is load-bearing twice
/// over.</b> It lets the gateway write the whole batch before the client reads any of it, so
/// every read the measurement covers is satisfied from bytes already in the kernel — which is
/// both the busy-stream case worth measuring and the case where nothing suspends. Nothing
/// suspending is what keeps the whole measured region on one thread, and
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> counts one thread. The thread id is
/// asserted afterwards rather than assumed, so a continuation that did hop fails the test instead
/// of quietly measuring an idle thread.
/// </para>
/// </remarks>
public class LiveAllocationTests
{
    /// <summary>
    /// Records replayed before measuring: enough to grow the decoder's buffer, decode the
    /// metadata, prime the socket's cached event args, and warm the JIT.
    /// </summary>
    private const int WarmupRecords = 256;

    /// <summary>
    /// Records measured. Sized to sit inside a loopback socket's receive buffer at
    /// <see cref="MboMsg.WireSize"/> bytes each — around 28 KB — so the gateway can write the lot
    /// before the client reads any of it.
    /// </summary>
    private const int MeasuredRecords = 512;

    private static readonly string DatasetName = Dataset.XnasItch.ToWireString();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FillAndTryNextRecord_OverASteadyMboStream_AllocateExactlyNothingPerRecord()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);

        await StartSessionAsync(gateway, client);

        // Warm-up batch. Everything that allocates once per session is paid here: the FSM's buffer
        // growth for the metadata block, the socket's cached SocketAsyncEventArgs, and the JIT.
        await ReplayAsync(gateway, WarmupRecords, firstSequence: 1);
        Assert.Equal(WarmupRecords, await DrainExactlyAsync(client, WarmupRecords));

        // Measured batch, on the same connection and the same decoder — steady state, not a second
        // cold start wearing a warm-up's name.
        await ReplayAsync(gateway, MeasuredRecords, firstSequence: WarmupRecords + 1);

        // The loop is written out here rather than called through DrainExactlyAsync, and that is
        // not style. An `async Task<int>` helper allocates its own Task<int> to return — 72 bytes,
        // once, measured — even when every await inside it completes synchronously. Charging the
        // test harness's return value to the library would have been the exact confusion this
        // file exists to prevent, in the direction that fails rather than the one that passes.
        // This method's own state machine was boxed during the handshake, long before `before`.
        var cancel = Cancel;

        Settle();
        var thread = Environment.CurrentManagedThreadId;
        var before = GC.GetAllocatedBytesForCurrentThread();

        var decoded = 0;
        while (decoded < MeasuredRecords)
        {
            decoded += DrainBuffered(client, MeasuredRecords - decoded);
            if (decoded >= MeasuredRecords || await client.FillBufferAsync(cancel) == 0)
            {
                break;
            }
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(
            thread,
            Environment.CurrentManagedThreadId);
        Assert.Equal(MeasuredRecords, decoded);
        Assert.Equal(0L, allocated);
    }

    [Fact]
    public async Task RecordsAsync_Allocates_AndSaysSo()
    {
        // The counterpart assertion, and the reason the zero above means something. The convenient
        // surface cannot be free — `yield return` has the same restriction `await` does, so a
        // ref struct cannot leave an iterator and every record has to be copied. Measuring it
        // here states the price rather than leaving a reader to assume the two surfaces cost the
        // same.
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var client = Client(gateway);

        await StartSessionAsync(gateway, client);

        await ReplayAsync(gateway, WarmupRecords, firstSequence: 1);
        Assert.Equal(WarmupRecords, await DrainExactlyAsync(client, WarmupRecords));

        await ReplayAsync(gateway, MeasuredRecords, firstSequence: WarmupRecords + 1);

        Settle();
        var before = GC.GetAllocatedBytesForCurrentThread();

        var yielded = 0;
        await foreach (var record in client.RecordsAsync(Cancel))
        {
            Assert.Equal(MboMsg.WireSize, record.SizeInBytes);
            if (++yielded == MeasuredRecords)
            {
                break;
            }
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(MeasuredRecords, yielded);
        Assert.True(
            allocated >= (long)MeasuredRecords * MboMsg.WireSize,
            $"An owned copy per record should cost at least {(long)MeasuredRecords * MboMsg.WireSize} "
            + $"bytes; the measurement reported {allocated}. Either the copy stopped happening — in "
            + "which case the records handed out no longer outlive the decoder's buffer — or this "
            + "measurement is not measuring the loop it claims to.");
    }

    // ----------------------------------------------------------------------------- Helpers

    private static async Task StartSessionAsync(MockLiveGateway gateway, LiveClient client)
    {
        var handshake = gateway.AuthenticateAsync(cancellationToken: Cancel);
        await client.ConnectAsync(Cancel);
        await client.AuthenticateAsync(Cancel);
        await handshake;

        var serving = gateway.StartAsync(Cancel);
        await client.StartAsync(Cancel);
        await serving;
    }

    private static async Task ReplayAsync(MockLiveGateway gateway, int count, int firstSequence)
    {
        for (var i = 0; i < count; i++)
        {
            await gateway.SendRecordAsync(SyntheticMbo.Record((uint)(firstSequence + i)), Cancel);
        }
    }

    /// <summary>
    /// Reads exactly <paramref name="records"/> records and stops, leaving the socket alone.
    /// </summary>
    /// <remarks>
    /// Bounded by count rather than by the stream ending, which matters for the measurement: a
    /// loop that read until <see cref="LiveClient.FillBufferAsync"/> returned zero would end with
    /// one read that had to wait for a gateway with nothing left to send — the one read that
    /// suspends, allocates a state machine, and moves the continuation to another thread.
    /// </remarks>
    private static async Task<int> DrainExactlyAsync(LiveClient client, int records)
    {
        var decoded = 0;

        while (decoded < records)
        {
            decoded += DrainBuffered(client, records - decoded);
            if (decoded >= records)
            {
                break;
            }

            if (await client.FillBufferAsync(Cancel) == 0)
            {
                break;
            }
        }

        return decoded;
    }

    /// <summary>
    /// Decodes up to <paramref name="limit"/> already-buffered records, touching each one so the
    /// loop cannot be optimised into nothing.
    /// </summary>
    /// <remarks>
    /// Non-<c>async</c> because a <see cref="RecordRef"/> cannot be in scope across an
    /// <c>await</c>, and free of delegates because a lambda in the measured loop would be the very
    /// allocation these tests exist to rule out.
    /// </remarks>
    private static int DrainBuffered(LiveClient client, int limit)
    {
        var decoded = 0;

        while (decoded < limit && client.TryNextRecord(out var record))
        {
            // Read through the zero-copy accessors rather than merely counting: Has, Get and the
            // field read off the ref readonly are the calls a caller makes, and they are where a
            // defensive copy of the record would appear if one crept back in.
            if (record.Has<MboMsg>() && record.Get<MboMsg>().Sequence != 0)
            {
                decoded++;
            }
        }

        return decoded;
    }

    private static void Settle()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static LiveClient Client(MockLiveGateway gateway) => new()
    {
        ApiKey = new ApiKey(MockLiveGateway.TestApiKey),
        Dataset = gateway.Dataset,
        Gateway = gateway.Address,
    };
}
