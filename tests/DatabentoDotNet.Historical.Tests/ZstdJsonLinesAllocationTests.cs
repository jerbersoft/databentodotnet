using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// That streaming a zstd-JSONL response costs a <em>constant</em> number of bytes per row, measured
/// rather than concluded.
/// </summary>
/// <remarks>
/// <para>
/// M4's seam property, and #52's definition of done. The reference-data endpoints return one JSON
/// object per line and there can be hundreds of thousands of them, so what makes
/// <c>SendZstdJsonLinesStreamAsync</c> worth having over the buffering reader is that its cost does
/// not grow with the response. Ten rows and ten thousand are measured here and the per-row figures
/// compared.
/// </para>
/// <para>
/// <b>Flat, not zero, and the difference is the point.</b> <c>TimeseriesAllocationTests</c> asserts
/// exactly zero because a DBN record is reinterpreted in place over the read buffer. Nothing of the
/// kind is true here: every row is a JSON document deserialized into a class, so a
/// <see langword="string"/> and an object per row are correct and expected. Chasing zero would mean
/// giving callers spans over a buffer that the next <c>MoveNextAsync</c> overwrites, which is not
/// the API this issue was asked for. What must not happen is per-row cost that rises with the
/// response — a list that grows, a buffer that is copied to make room, a closure captured per row.
/// </para>
/// <para>
/// <b>The measurement is sampled per step, and only the steps that did not suspend.</b>
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> counts one thread, and an
/// <c>await foreach</c> awaits once per row — so a <c>before</c>/<c>after</c> pair straddling a
/// suspension would be subtracting two different threads' counters. A streaming enumerator
/// therefore cannot be bracketed as a whole the way <c>TimeseriesAllocationTests</c> brackets a
/// stretch of <c>TryNextRecord</c> calls. What can be bracketed is one
/// <c>MoveNextAsync</c> that <em>completed synchronously</em>: its
/// <see cref="ValueTask{TResult}.IsCompleted"/> is <see langword="true"/> before anything awaits
/// it, so no continuation ran and no thread hopped, and the row's whole cost — the line, the
/// deserialization, the object — happened between the two reads. Most rows are that: the reader
/// only suspends when <c>StreamReader</c>'s buffer runs dry. The rows that did suspend are counted
/// separately and excluded from the average rather than charged to it, which is why the figure
/// compared across the two sizes is bytes per <em>measured</em> row.
/// </para>
/// <para>
/// <b>The first measured step is discarded</b>, for the reason <c>TimeseriesAllocationTests</c>
/// discards its first buffer: it carries the download's one-time costs — the decompression stream
/// and its context, <c>StreamReader</c>'s buffers, the iterator state machines — which are per
/// response rather than per row and would otherwise be charged to a single row and make the
/// ten-row average meaningless.
/// </para>
/// <para>
/// <b>And the instrument is checked.</b> <see cref="TheMeasurement_NoticesADeliberateAllocation"/>
/// runs the identical drain with one allocation added per measured row, because a measurement that
/// had silently stopped measuring would report a flat figure and pass every other assertion here.
/// <c>TimeseriesAllocationTests</c>, <c>LiveAllocationTests</c> and <c>AllocationTests</c> all carry
/// the same companion.
/// </para>
/// </remarks>
public partial class ZstdJsonLinesAllocationTests
{
    private const string SmallSlug = "reference.get_security_small";
    private const string LargeSlug = "reference.get_security_large";

    private const int SmallRows = 10;
    private const int LargeRows = 10_000;

    /// <summary>
    /// How far the ten-thousand-row per-row figure may sit from the ten-row one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Within a small constant", made a number. A per-row cost proportional to the response would
    /// miss this by three orders of magnitude — ten thousand rows appended to a growing list is
    /// tens of bytes per row of copying on its own, and any buffer that is re-copied to make room
    /// is far worse — so the bound does not need to be tight to catch what it is for.
    /// </para>
    /// <para>
    /// It is not zero only because the two responses are not identical work: the larger one crosses
    /// far more network reads, so a different set of rows falls on a <c>StreamReader</c> refill
    /// boundary. In practice that costs nothing measurable — over five runs the ten-row body came
    /// to 224.0 bytes per row and the ten-thousand-row one to 224.0 or 224.1, a difference of at
    /// most a tenth of a byte. The margin here is for a machine that is not this one, not for
    /// noise that was observed.
    /// </para>
    /// </remarks>
    private const double PerRowTolerance = 16.0;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// Ten thousand rows cost the same per row as ten.
    /// </summary>
    [Fact]
    public async Task PerRowAllocation_DoesNotGrowWithTheNumberOfRows()
    {
        await using var gateway = await StartGatewayAsync();
        await using var client = ClientFor(gateway);

        var small = await MeasureAsync(client, SmallSlug, allocatePerRow: false);
        var large = await MeasureAsync(client, LargeSlug, allocatePerRow: false);

        gateway.ThrowIfRejected();
        Assert.Equal(SmallRows, small.Rows);
        Assert.Equal(LargeRows, large.Rows);

        // A drain that measured nothing would report a per-row figure of zero for both and sail
        // through the comparison below, so the sample sizes are asserted before the averages are.
        Assert.True(small.MeasuredRows > 1, $"Only {small.MeasuredRows} of {SmallRows} rows were measurable.");
        Assert.True(large.MeasuredRows > 1, $"Only {large.MeasuredRows} of {LargeRows} rows were measurable.");

        Assert.True(
            Math.Abs(large.BytesPerRow - small.BytesPerRow) <= PerRowTolerance,
            $"Per-row allocation should not grow with the response. {SmallRows} rows cost "
            + $"{small.BytesPerRow:F1} bytes/row over {small.MeasuredRows} measured rows; "
            + $"{LargeRows} rows cost {large.BytesPerRow:F1} bytes/row over {large.MeasuredRows}. "
            + $"That is a difference of {large.BytesPerRow - small.BytesPerRow:F1} bytes against a "
            + $"tolerance of {PerRowTolerance:F1}.");
    }

    /// <summary>
    /// The companion that keeps the assertion above honest: the same drain, one small allocation per
    /// measured row, and the measurement has to see it.
    /// </summary>
    /// <remarks>
    /// Without this, a measurement that had quietly stopped measuring — a counter read on the wrong
    /// thread, a drain that yielded nothing, a bracket that no longer spans the deserialization —
    /// would report two equal per-row figures and be indistinguishable from success. The bound is
    /// deliberately loose: what is checked is that the instrument responds at all, not what a
    /// <c>byte[1]</c> costs.
    /// </remarks>
    [Fact]
    public async Task TheMeasurement_NoticesADeliberateAllocation()
    {
        await using var gateway = await StartGatewayAsync();
        await using var client = ClientFor(gateway);

        var honest = await MeasureAsync(client, LargeSlug, allocatePerRow: false);
        var noisy = await MeasureAsync(client, LargeSlug, allocatePerRow: true);

        gateway.ThrowIfRejected();
        Assert.Equal(LargeRows, honest.Rows);
        Assert.Equal(LargeRows, noisy.Rows);

        Assert.True(
            noisy.BytesPerRow >= honest.BytesPerRow + 8,
            $"An allocation per row should cost at least eight bytes more per row than none; the "
            + $"measurement reported {honest.BytesPerRow:F1} bytes/row without it and "
            + $"{noisy.BytesPerRow:F1} with it. Either the drain stopped decoding or the instrument "
            + "is not measuring what it claims to.");
    }

    /// <summary>
    /// Streams <paramref name="slug"/> twice — once to warm up, once to measure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The warm-up is not optional.</b> Tier-0 code is promoted to tier 1 on a call count plus a
    /// delay and the transition is paid for on the calling thread, so it lands inside whichever
    /// measured step happens to be running, once, with a size that differs run to run.
    /// <see cref="TryStepSynchronously"/> is taken out of tiering by its own attribute; the library
    /// methods it calls cannot be attributed from a test, and a whole discarded stream is what puts
    /// their promotion behind the measurement. <c>TimeseriesAllocationTests</c> and
    /// <c>LiveAllocationTests</c> both warm up for the same reason and say so.
    /// </para>
    /// <para>
    /// It warms up on the same route, so the second stream differs from the first in nothing at
    /// all — not the row count, not the body, not the number of network reads it takes.
    /// </para>
    /// </remarks>
    /// <param name="client">The client to stream with.</param>
    /// <param name="slug">The route to stream.</param>
    /// <param name="allocatePerRow">Passed through to <see cref="TryStepSynchronously"/>.</param>
    /// <returns>The measured stream's figures.</returns>
    private static async Task<Measurement> MeasureAsync(
        HistoricalClient client,
        string slug,
        bool allocatePerRow)
    {
        _ = await DrainAsync(client, slug, allocatePerRow);
        return await DrainAsync(client, slug, allocatePerRow);
    }

    /// <summary>
    /// Streams <paramref name="slug"/> to the end, summing allocations over the steps that did not
    /// suspend.
    /// </summary>
    /// <param name="client">The client to stream with.</param>
    /// <param name="slug">The route to stream.</param>
    /// <param name="allocatePerRow">
    /// Whether to allocate one small object per measured row, which is the only difference between
    /// the two tests above.
    /// </param>
    /// <returns>How many rows arrived, how many were measurable, and what they cost.</returns>
    private static async Task<Measurement> DrainAsync(
        HistoricalClient client,
        string slug,
        bool allocatePerRow)
    {
        var enumerator = client
            .SendZstdJsonLinesStreamAsync(
                HttpMethod.Get, slug, parameters: null, StreamAllocationJson.Default.DatasetRow, Cancel)
            .GetAsyncEnumerator(Cancel);

        await using (enumerator.ConfigureAwait(false))
        {
            var rows = 0;
            var measuredRows = 0;
            var allocated = 0L;
            var checksum = 0L;

            while (true)
            {
                bool more;
                if (TryStepSynchronously(enumerator, allocatePerRow, out var step, out var delta, out var sink))
                {
                    // step is completed, so reading its result neither awaits nor hops threads.
                    more = step.Result;
                    if (more)
                    {
                        // The first measured step carries the response's one-time costs — see the
                        // class remarks — and is counted as a row without being charged as one.
                        if (measuredRows > 0)
                        {
                            allocated += delta;
                        }

                        measuredRows++;
                    }

                    // Keeps the deliberate allocation from being optimised away, and costs nothing
                    // in the measured case where there was nothing to keep.
                    GC.KeepAlive(sink);
                }
                else
                {
                    more = await step.ConfigureAwait(false);
                }

                if (!more)
                {
                    break;
                }

                rows++;

                // Touch the row so neither the loop nor the deserialization can be optimised down
                // to a counter. Reading a property off a reference allocates nothing.
                checksum += enumerator.Current.Dataset!.Length;
            }

            // Every row's dataset is "GLBX.MDP" plus five digits. Asserting the sum is what makes
            // the property read above load-bearing rather than dead code a JIT may drop.
            Assert.Equal(rows * 13L, checksum);

            return new Measurement(rows, Math.Max(measuredRows - 1, 0), allocated);
        }
    }

    /// <summary>
    /// Advances <paramref name="enumerator"/> once and, if it did not suspend, says what the step
    /// allocated. No <c>await</c>, by design — see this class's remarks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="MethodImplOptions.AggressiveOptimization"/> is what makes the measurement
    /// repeatable</b>, and <c>TimeseriesAllocationTests</c> earned that attribute by measurement
    /// rather than by anticipation: without it, that file failed roughly one run in four with a
    /// single early iteration reporting four to six kilobytes on an unchanged thread with no
    /// collection inside the region — the runtime promoting the method from tier 0 to tier 1 and
    /// charging the transition to the calling thread. Promotion fires on a call count
    /// <em>and</em> a delay, so calling more cannot rule it out; only taking the method out of
    /// tiering can.
    /// </para>
    /// <para>
    /// The two counter reads bracket exactly one <c>MoveNextAsync</c>. Everything a row costs
    /// happens inside it — the line <c>StreamReader</c> returns, the transcode
    /// <see cref="System.Text.Json.JsonSerializer"/> does, the object it produces — and nothing else
    /// does.
    /// </para>
    /// </remarks>
    /// <param name="enumerator">The enumerator to advance.</param>
    /// <param name="allocate">Whether to allocate one small object inside the measured region.</param>
    /// <param name="step">The step, completed or not. The caller consumes it exactly once.</param>
    /// <param name="allocated">What the step allocated, or zero if it suspended.</param>
    /// <param name="sink">The deliberate allocation, for the caller to keep alive.</param>
    /// <returns>Whether the step completed synchronously and is therefore measurable.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool TryStepSynchronously(
        IAsyncEnumerator<DatasetRow> enumerator,
        bool allocate,
        out ValueTask<bool> step,
        out long allocated,
        out object? sink)
    {
        sink = null;

        var before = GC.GetAllocatedBytesForCurrentThread();
        step = enumerator.MoveNextAsync();

        if (!step.IsCompleted)
        {
            allocated = 0;
            return false;
        }

        if (allocate)
        {
            sink = new byte[1];
        }

        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return true;
    }

    private static async Task<MockHistoricalGateway> StartGatewayAsync()
    {
        var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(SmallSlug, MockHistoricalResponse.ZstdJsonLines(RowsFor(SmallRows)));
        gateway.Get(LargeSlug, MockHistoricalResponse.ZstdJsonLines(RowsFor(LargeRows)));
        return gateway;
    }

    /// <summary>
    /// <paramref name="count"/> JSON documents of identical length.
    /// </summary>
    /// <remarks>
    /// The width is fixed at five digits so a row of the ten-row body and a row of the ten-thousand
    /// one are the same number of characters. Otherwise the larger response's rows would be longer
    /// on average, the strings they deserialize from would cost more, and the per-row figures would
    /// differ for a reason that has nothing to do with whether the cost is flat.
    /// </remarks>
    /// <param name="count">How many rows.</param>
    /// <returns>The rows.</returns>
    private static string[] RowsFor(int count)
    {
        var rows = new string[count];
        for (var i = 0; i < count; i++)
        {
            rows[i] = $$"""{"dataset":"GLBX.MDP{{i.ToString("D5", CultureInfo.InvariantCulture)}}"}""";
        }

        return rows;
    }

    private static HistoricalClient ClientFor(MockHistoricalGateway gateway) => new()
    {
        ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey),
        BaseUrl = gateway.BaseUrl,
    };

    /// <summary>One drain's figures.</summary>
    /// <param name="Rows">How many rows the stream yielded.</param>
    /// <param name="MeasuredRows">
    /// How many of them were charged — the steps that completed synchronously, less the first.
    /// </param>
    /// <param name="Allocated">What those rows allocated, in bytes.</param>
    private sealed record Measurement(int Rows, int MeasuredRows, long Allocated)
    {
        /// <summary>Bytes allocated per measured row.</summary>
        public double BytesPerRow => MeasuredRows == 0 ? 0 : (double)Allocated / MeasuredRows;
    }

    /// <summary>One row of the JSONL body.</summary>
    private sealed class DatasetRow
    {
        /// <summary>The dataset's code.</summary>
        public string? Dataset { get; set; }
    }

    /// <summary>This file's own serialization context; see <c>ZstdJsonLinesStreamTests</c>.</summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(DatasetRow))]
    private sealed partial class StreamAllocationJson : JsonSerializerContext
    {
    }
}
