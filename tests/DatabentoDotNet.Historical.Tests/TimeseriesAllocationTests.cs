using System.Runtime.CompilerServices;
using DatabentoDotNet.Dbn;
using NodaTime;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// That decoding a <c>timeseries.get_range</c> download costs nothing per record, measured rather
/// than concluded.
/// </summary>
/// <remarks>
/// <para>
/// The historical twin of <c>LiveAllocationTests</c>, and #38's definition of done: ROADMAP.md §5
/// promises multi-gigabyte downloads, and what makes that work is that per-record cost is flat. A
/// flat per-record cost of <em>zero</em> is the stronger claim and the one asserted here, so
/// nothing needs a multi-gigabyte stream to demonstrate it — a range four times longer allocating
/// four times nothing is still nothing.
/// </para>
/// <para>
/// <b>The measurement is sampled inside each drain, not across the whole download.</b>
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> counts one thread, and an HTTP body arrives
/// over reads that suspend — so a continuation after <c>await</c> may well resume on another
/// thread, and a <c>before</c>/<c>after</c> pair straddling one would be subtracting two different
/// threads' counters. Each pair here brackets a stretch of <c>TryNextRecord</c> calls with no
/// <c>await</c> between them, which cannot hop; the per-stretch deltas are then summed. That
/// measures exactly the zero-copy claim — the record loop — and deliberately excludes the async
/// state machines a suspending read legitimately allocates.
/// </para>
/// <para>
/// <b>The body is far larger than the decode buffer</b> — see <see cref="RecordRuns"/> — so the
/// measured region spans many refills, and therefore many buffer shifts. A test whose stream fitted
/// in one buffer would never exercise the shift, which is the operation most likely to allocate.
/// </para>
/// <para>
/// <b>And the instrument is checked.</b> <see cref="TheMeasurement_NoticesADeliberateAllocation"/>
/// runs the identical loop with one allocation added, because a broken measurement reporting zero
/// would pass every other assertion in this file. <c>LiveAllocationTests</c> and
/// <c>AllocationTests</c> both carry the same companion, for the same reason.
/// </para>
/// </remarks>
public sealed class TimeseriesAllocationTests
{
    /// <summary>
    /// Copies of the fixture's record run to serve. At two 48-byte records each, this is roughly
    /// 1.9 MB of records against a 64 KB decode buffer — about thirty refills.
    /// </summary>
    private const int RecordRuns = 20_000;

    private const string Slug = "timeseries.get_range";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// The steady-state record loop over a multi-megabyte download allocates exactly nothing.
    /// </summary>
    [Fact]
    public async Task TryNextRecord_OverADownloadLargerThanTheBuffer_AllocatesExactlyNothing()
    {
        await using var gateway = await StartGatewayAsync();
        await using var client = ClientFor(gateway);

        var (records, allocated) = await MeasureDownloadAsync(client, allocatePerRecord: false);

        Assert.Equal(RecordRuns * TimeseriesFixture.RecordCount, records);
        Assert.Equal(0L, allocated);
    }

    /// <summary>
    /// The companion that keeps the assertion above honest: the same loop, one small allocation per
    /// record, and the measurement has to see it.
    /// </summary>
    /// <remarks>
    /// Without this, a measurement that had silently stopped measuring — a counter read on the
    /// wrong thread, a loop that decoded nothing — would report zero and be indistinguishable from
    /// success. The bound is deliberately loose: what is being checked is that the instrument
    /// responds at all, not what an allocation costs.
    /// </remarks>
    [Fact]
    public async Task TheMeasurement_NoticesADeliberateAllocation()
    {
        await using var gateway = await StartGatewayAsync();
        await using var client = ClientFor(gateway);

        var (records, allocated) = await MeasureDownloadAsync(client, allocatePerRecord: true);

        Assert.Equal(RecordRuns * TimeseriesFixture.RecordCount, records);
        Assert.True(
            allocated >= records,
            $"An allocation per record should cost at least {records} bytes; the measurement "
            + $"reported {allocated}. Either the loop stopped decoding or the instrument is not "
            + "measuring what it claims to.");
    }

    /// <summary>
    /// Downloads the range twice — once to warm up, once to measure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The warm-up is not optional, and both halves of this file's defence against tiered
    /// compilation were established by measurement rather than assumed.</b> Tier-0 code is
    /// promoted to tier 1 on a call count plus a delay, and the transition is paid for on the
    /// calling thread — so it lands inside whichever measured region happens to be running, once,
    /// with a size that differs run to run.
    /// </para>
    /// <para>
    /// <see cref="DrainBuffered"/> is taken out of tiering by its own attribute. That leaves the
    /// library methods it calls, which cannot be attributed from a test and are promoted on the
    /// same schedule: with this warm-up removed and the attribute kept, ten runs failed four
    /// times, every failure reporting 5,720 bytes. A whole discarded download puts that promotion
    /// behind the measurement. <c>LiveAllocationTests</c> warms up for the same reason and says so.
    /// </remarks>
    /// <param name="client">The client to download with.</param>
    /// <param name="allocatePerRecord">Passed through to <see cref="DrainBuffered"/>.</param>
    /// <returns>The measured download's record count and allocation.</returns>
    private static async Task<(int Records, long Allocated)> MeasureDownloadAsync(
        HistoricalClient client,
        bool allocatePerRecord)
    {
        await using (var warmUp = await client.Timeseries.GetRangeAsync(Params(), Cancel))
        {
            await DrainAsync(warmUp, allocatePerRecord);
        }

        await using var reader = await client.Timeseries.GetRangeAsync(Params(), Cancel);
        return await DrainAsync(reader, allocatePerRecord);
    }

    /// <summary>
    /// Drains <paramref name="reader"/>, summing allocations over the synchronous stretches only.
    /// </summary>
    /// <param name="reader">The reader to drain.</param>
    /// <param name="allocatePerRecord">
    /// Whether to allocate one small object per record, which is what the instrument-check test
    /// varies and the only difference between the two tests above.
    /// </param>
    /// <returns>How many records were decoded, and how many bytes the drains allocated.</returns>
    private static async Task<(int Records, long Allocated)> DrainAsync(
        TimeseriesReader reader,
        bool allocatePerRecord)
    {
        var records = 0;
        var allocated = 0L;

        // The first buffer is decoded outside the measurement: it carries the one-time costs — JIT,
        // the decompressor's first allocations, the buffer growing to its working size — which are
        // per-download rather than per-record and would otherwise be charged to the record loop.
        records += DrainBuffered(reader, allocate: false, out _);

        while (await reader.FillBufferAsync(Cancel) != 0)
        {
            // before and after bracket a region with no await in it, so both counter reads happen
            // on one thread. Whether the thread changed between iterations does not matter, because
            // each delta is self-contained.
            var before = GC.GetAllocatedBytesForCurrentThread();
            records += DrainBuffered(reader, allocatePerRecord, out var sink);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;

            // Keeps the deliberate allocation from being optimised away, and costs nothing in the
            // measured case where nothing was allocated to keep.
            GC.KeepAlive(sink);
        }

        return (records, allocated);
    }

    /// <summary>
    /// Decodes every record already in the buffer. No <c>await</c>, by design — see this class's
    /// remarks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="MethodImplOptions.AggressiveOptimization"/> is what makes the measurement
    /// repeatable, and it was added in response to a measurement rather than in anticipation of
    /// one.</b> Without it this file failed roughly one run in four, always the same way: a single
    /// early iteration reporting four to six kilobytes — 4,640 and 5,176 and 5,696 across runs —
    /// on an unchanged thread with no garbage collection inside the region.
    /// </para>
    /// <para>
    /// A cost that appears once, lands in whichever iteration happens to be running, and differs in
    /// size each time is not the record loop allocating. It is the runtime promoting this method
    /// from tier 0 to tier 1 and paying for the transition on the calling thread. Promotion fires
    /// on a call count <em>and</em> a delay, so warming up by calling more cannot rule it out —
    /// only taking the method out of tiering can, which is exactly what this attribute does.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int DrainBuffered(TimeseriesReader reader, bool allocate, out object? sink)
    {
        var decoded = 0;
        sink = null;

        while (reader.TryNextRecord(out var record))
        {
            decoded++;

            if (allocate)
            {
                sink = new byte[1];
            }
            else
            {
                // Touch the record so the loop cannot be optimised down to a counter. Reading a
                // field off the ref struct allocates nothing.
                _ = record.Header.InstrumentId;
            }
        }

        return decoded;
    }

    private static async Task<MockHistoricalGateway> StartGatewayAsync()
    {
        var body = TimeseriesFixture.Compress(TimeseriesFixture.Repeating(RecordRuns).Bytes);
        var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(Slug, MockHistoricalResponse.Binary(body));
        return gateway;
    }

    private static GetRangeParams Params() => new()
    {
        Dataset = "GLBX.MDP3",
        Symbols = Symbols.From("ESH4"),
        Schema = Schema.Trades,
        DateTimeRange = DateTimeRange.Between(
            Instant.FromUtc(2023, 7, 4, 0, 0, 0),
            Instant.FromUtc(2023, 7, 5, 0, 0, 0)),
    };

    private static HistoricalClient ClientFor(MockHistoricalGateway gateway) => new()
    {
        ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey),
        BaseUrl = gateway.BaseUrl,
    };
}
