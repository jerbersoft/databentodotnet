using System.Diagnostics.Metrics;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Dbn.Publishers;
using DatabentoDotNet.Live;
using DatabentoDotNet.Live.Tests;

namespace DatabentoDotNet.Extensions.Hosting.Tests;

/// <summary>
/// #91: reading records through <see cref="LiveSessionRunner"/> — not just through
/// <see cref="LiveClient.FillBufferAsync"/> and <see cref="LiveClient.TryNextRecord"/>, which
/// <c>LiveAllocationTests</c> already covers — allocates nothing per record.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this adds over <c>LiveAllocationTests</c>.</b> That file holds the codec's socket read
/// seam. This file holds everything the runner puts around it: <see cref="ILiveRecordHandler.OnRecord"/>
/// dispatch, <see cref="ILiveRecordHandler.OnFlushAsync"/>, the metrics publish, and the
/// <c>async</c> state machine that is <see cref="LiveSessionRunner.RunAsync"/> itself — an
/// <c>async</c> method is exactly where a per-call allocation hides, because a state machine box,
/// a <see cref="CancellationTokenSource"/> and a cancellation registration are all invisible in
/// the source.
/// </para>
/// <para>
/// <b>Why the measurement lives inside the handler, not in the test method's own body.</b>
/// <c>LiveAllocationTests</c> can bracket its measurement in the test method directly because it
/// calls <see cref="LiveClient.FillBufferAsync"/> itself, in a loop it owns, bounded by a record
/// count. <see cref="LiveSessionRunner"/> exposes no equivalent bounded primitive — its only
/// public entry point to the loop is <see cref="LiveSessionRunner.RunAsync"/>, which runs until
/// the stream ends or the caller cancels, and either exit tears the underlying
/// <see cref="LiveClient"/> down (<c>CloseAsync</c>, in both the clean-close and the cancellation
/// branch). Calling it twice — once to bound the warm-up, once to bound the measured batch — would
/// close the connection after the first call and leave nothing for the second to read: the
/// two-call, closed-in-between shape this file's first draft tried does not exist for this type.
/// So <see cref="LiveSessionRunner.RunAsync"/> is started exactly <b>once</b>, left running for the
/// whole test, and <see cref="MeasuringHandler"/> takes its own snapshots from inside
/// <see cref="ILiveRecordHandler.OnFlushAsync"/> — the runner's own call stack, at the two points
/// in its loop that correspond to "before" and "after" in the lower-level test.
/// </para>
/// <para>
/// <b>The measured batch is sent from inside that same flush call, not from the test method
/// racing it.</b> The first version of this file sent it from the test's own <c>async</c> flow,
/// after awaiting a "warm-up done" signal from the handler — and that lost the race it depends on
/// visibly: signalling the test and letting the runner's own loop proceed to its next
/// <c>FillBufferAsync</c> are two independent continuations with no ordering between them, so the
/// runner could reach — and suspend on — that read before the test had sent anything for it to
/// read. A genuine suspend there hands the resumption to whichever thread pool worker is free,
/// which is not always the one that captured "before", and even when it happens to be the same
/// managed thread id, that worker may have served *other* queued work in between, and
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> would count that too. The very first run of
/// this file measured exactly that: a large, spurious non-zero count, and a negative one in the
/// counter-test, that had nothing to do with <see cref="LiveSessionRunner"/>. Sending the batch
/// from inside <see cref="MeasuringHandler.OnFlushAsync"/> instead removes the race rather than
/// hoping to win it: the send is awaited to completion, on the runner's own call stack, before
/// that flush returns, so by the time <see cref="LiveSessionRunner"/> issues its next
/// <c>FillBufferAsync</c> the bytes are already sitting in the kernel's receive buffer — the same
/// "already there when the read is attempted" guarantee <c>LiveAllocationTests</c> gets for free
/// by driving its whole loop from one <c>await</c>-ordered method. A write that small, to a
/// loopback socket with room in its send buffer, does not itself need to suspend.
/// </para>
/// <para>
/// <b>The thread-identity check is what makes the zero trustworthy rather than assumed.</b> It
/// does not prove nothing else ever touched that thread — no assertion can prove that — but a
/// <see langword="false"/> here means the two snapshots do not describe one continuous stretch of
/// work, and the byte count is not evidence of anything. Failing loudly on that rather than
/// reporting whatever the mismeasurement happened to produce is the entire point of carrying the
/// flag alongside the count.
/// </para>
/// <para>
/// <b>The meter needs a listener attached and enabled, or the whole measurement is meaningless.</b>
/// <see cref="Counter{T}.Add(T)"/> short-circuits when no <see cref="MeterListener"/> is
/// subscribed to the instrument, so a run with metrics configured but unobserved would report
/// zero bytes for a publish path that starts allocating the moment anyone actually collects it.
/// The callback itself does nothing — not even copy the tag span — because a callback that copies
/// <c>ReadOnlySpan&lt;KeyValuePair&lt;string, object?&gt;&gt;</c> into an array allocates around
/// 106 bytes per publish on its own, which would be the measuring harness's allocation charged to
/// the runner.
/// </para>
/// <para>
/// <b>This was a real hazard, found and closed in this same commit — not a risk left open.</b>
/// <c>LiveSessionMetrics.MeterName</c> is one constant shared by every
/// <see cref="LiveSessionMetrics"/> in the process, so a listener that enabled measurement events
/// by matching that name would see — and invoke its callback for — any other test's session
/// running concurrently, on that test's own thread. <c>ObservabilityTests</c> filtered its
/// <c>MeasurementRecorder</c> that way; its callback copies the tag span; this file measured the
/// consequence directly, as a small, non-zero, run-to-run-varying count that vanished under
/// <c>metrics: null</c>. <c>ObservabilityTests</c> now scopes its recorder to
/// <c>ReferenceEquals(instrument.Meter.Scope, factory)</c> against a per-test <c>IMeterFactory</c>
/// instead of the shared name, so its listener cannot be invoked for an instrument it does not own.
/// What remains true, and is why <see cref="AttachedListener"/> above filters the way it does: any
/// listener anywhere in the assembly that filters by name instead of by scope would reopen this
/// exact hazard.
/// </para>
/// <para>
/// <b>The two tests in this file are given a collection of their own, with parallelisation off, for
/// a narrower, purely within-file reason.</b> xUnit v3 runs <c>[Fact]</c> methods within one class
/// in parallel with each other by default — unlike the class-level granularity earlier xUnit
/// versions used — so without this, <see cref="TheMeasurementItself_NoticesADeliberateAllocation"/>
/// could still be mid-flight, publishing metrics of its own, while this file's other test is inside
/// its exact-zero window. (That test no longer attaches a listener at all — see its own remarks —
/// which already closes the likeliest source of that particular overlap; the collection is kept as
/// a second, structural guard against whatever this file grows next.) This collection only orders
/// tests *within it* and has nothing to do with the cross-file hazard the previous paragraph
/// describes; that one is closed by scope, independent of what runs concurrently with this file.
/// </para>
/// </remarks>
[Collection(CollectionName)]
public class ExtensionsAllocationTests
{
    /// <summary>
    /// The name of the non-parallel collection this file's two tests share — see the type-level
    /// remarks for why they cannot run concurrently with each other.
    /// </summary>
    internal const string CollectionName = "ExtensionsAllocationTests (metrics, non-parallel)";

    /// <summary>
    /// Records replayed before measuring: enough to grow the decoder's buffer, decode the
    /// metadata, prime the socket's cached event args, and warm the JIT. Matches
    /// <c>LiveAllocationTests.WarmupRecords</c>.
    /// </summary>
    private const int WarmupRecords = 256;

    /// <summary>
    /// Records measured. Sized to sit inside a loopback socket's receive buffer at
    /// <see cref="MboMsg.WireSize"/> bytes each — around 28 KB — so the gateway can write the lot
    /// before the runner reads any of it. Matches <c>LiveAllocationTests.MeasuredRecords</c>.
    /// </summary>
    private const int MeasuredRecords = 512;

    private static readonly string DatasetName = Dataset.XnasItch.ToWireString();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RunAsync_OverASteadyMboStream_AllocatesExactlyNothingPerRecord()
    {
        await using var gateway = new MockLiveGateway(DatasetName);

        var handler = new MeasuringHandler(
            WarmupRecords,
            MeasuredRecords,
            sendMeasuredBatchAsync: token =>
                ReplayAsync(gateway, MeasuredRecords, firstSequence: WarmupRecords + 1, token));

        using var metrics = new LiveSessionMetrics();
        using var listener = AttachedListener();

        await using var runner = new LiveSessionRunner(
            Session(gateway), handler, Supervisor(), logger: null, metrics: metrics);

        await StartSessionAsync(gateway, runner);

        // Started once and left running for the rest of the test — see the type-level remarks for
        // why this cannot be two bounded calls the way the lower-level test's helper is.
        var running = runner.RunAsync(Cancel);

        await ReplayAsync(gateway, WarmupRecords, firstSequence: 1, Cancel);

        // Resolves once MeasuringHandler has captured both snapshots — see the type-level remarks
        // for why the measured batch is sent from inside the handler rather than from here.
        var sample = await handler.Measured;

        await gateway.CloseAsync();
        await running;

        Assert.True(
            sample.SameThread,
            "The measured batch's processing hopped threads mid-flight, so "
            + "GC.GetAllocatedBytesForCurrentThread() before and after did not count the same "
            + "thread's work. Either the batch did not arrive in one read as sized, or something "
            + "in the loop now awaits genuinely instead of completing synchronously.");
        Assert.Equal(1, sample.Flushes);
        Assert.Equal(MeasuredRecords, sample.Decoded);
        Assert.Equal(0L, sample.AllocatedBytes);
    }

    [Fact]
    public async Task TheMeasurementItself_NoticesADeliberateAllocation()
    {
        // Without this, a broken instrument reporting zero would pass every other assertion in
        // this file. Both existing allocation files (LiveAllocationTests and AllocationTests)
        // carry the same test for the same reason: it is the same MeasuringHandler and the same
        // bracket, applied to a handler that allocates on every OnRecord instead of one that does
        // not — proof that the harness would have noticed the guarantee above breaking.
        //
        // No metrics here, deliberately. This test's own bracket is a threshold ("at least"), not
        // an equality, so a metrics listener's presence could only ever help it pass — it proves
        // nothing extra to attach one. What attaching one *would* do is put a second
        // LiveSessionMetrics/MeterListener pair live in the process, which is precisely the
        // shared-meter-name cross-talk the type-level remarks describe; keeping this test's own
        // publish path silent is what keeps that risk out of the other test's exact-zero window.
        await using var gateway = new MockLiveGateway(DatasetName);

        var handler = new MeasuringHandler(
            WarmupRecords,
            MeasuredRecords,
            sendMeasuredBatchAsync: token =>
                ReplayAsync(gateway, MeasuredRecords, firstSequence: WarmupRecords + 1, token),
            allocatePerRecord: true);

        await using var runner = new LiveSessionRunner(
            Session(gateway), handler, Supervisor(), logger: null, metrics: null);

        await StartSessionAsync(gateway, runner);

        var running = runner.RunAsync(Cancel);

        await ReplayAsync(gateway, WarmupRecords, firstSequence: 1, Cancel);

        var sample = await handler.Measured;

        await gateway.CloseAsync();
        await running;

        // No Flushes == 1 assertion here, unlike the test above: the deliberate per-record
        // allocation adds real GC pressure to the drain loop, which makes it plausible — and
        // observed — for the 512-record batch to take several fill/drain cycles to fully arrive
        // instead of one. That is fine for this test's purpose (proving the harness would have
        // noticed a per-record cost), which needs a lower bound on the total, not a single-batch
        // arrival. SameThread still matters: a hop invalidates the byte count either direction.
        Assert.True(sample.SameThread, "See RunAsync_OverASteadyMboStream_AllocatesExactlyNothingPerRecord.");
        Assert.Equal(MeasuredRecords, sample.Decoded);
        Assert.True(
            sample.AllocatedBytes >= MeasuredRecords * 8L,
            $"A deliberate per-record allocation should have been measured; the instrument "
            + $"reported {sample.AllocatedBytes} bytes. Either the allocation stopped happening, "
            + "or this measurement is not measuring the loop it claims to.");
    }

    // ----------------------------------------------------------------------------- Helpers

    private static ResolvedLiveSession Session(MockLiveGateway gateway) => new()
    {
        Name = "equities",
        ApiKey = new ApiKey(MockLiveGateway.TestApiKey),
        Dataset = gateway.Dataset,
        Gateway = gateway.Address,
        Subscriptions = [new Subscription { Schema = Schema.Mbo, Symbols = Symbols.From(["AAPL"]) }],
        // Off, so a stray reconnect never becomes a second, unmeasured pass over the record loop.
        Reconnect = ResolvedReconnect.Default with { Enabled = false },
    };

    private static ReconnectSupervisor Supervisor() =>
        new(ResolvedReconnect.Default with { Enabled = false });

    /// <summary>Runs the gateway's side of connect, authenticate, subscribe and start.</summary>
    private static async Task ServeStartupAsync(MockLiveGateway gateway)
    {
        await gateway.AuthenticateAsync(cancellationToken: Cancel);
        await gateway.ExpectSubscribeAsync(
            new ExpectedSubscription { Schema = Schema.Mbo, StypeIn = SType.RawSymbol, Symbols = ["AAPL"] },
            isLast: true,
            Cancel);
        await gateway.StartAsync(Cancel);
    }

    private static async Task StartSessionAsync(MockLiveGateway gateway, LiveSessionRunner runner)
    {
        var serving = ServeStartupAsync(gateway);
        await runner.StartSessionAsync(Cancel);
        await serving;
    }

    private static async Task ReplayAsync(
        MockLiveGateway gateway, int count, int firstSequence, CancellationToken cancellationToken)
    {
        for (var i = 0; i < count; i++)
        {
            await gateway.SendRecordAsync(SyntheticMbo.Record((uint)(firstSequence + i)), cancellationToken);
        }
    }

    /// <summary>
    /// A <see cref="MeterListener"/> subscribed to every instrument on
    /// <see cref="LiveSessionMetrics.MeterName"/>, enabled and started, with a callback that does
    /// nothing. See the type-level remarks for why "does nothing" is load-bearing rather than an
    /// omission.
    /// </summary>
    private static MeterListener AttachedListener()
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == LiveSessionMetrics.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>(static (_, _, _, _) => { });
        listener.SetMeasurementEventCallback<double>(static (_, _, _, _) => { });
        listener.Start();

        return listener;
    }

    /// <summary>What one bracketed measurement found.</summary>
    /// <param name="Decoded">
    /// Records seen since the warm-up boundary — should equal <see cref="MeasuredRecords"/>.
    /// </param>
    /// <param name="AllocatedBytes">
    /// <see cref="GC.GetAllocatedBytesForCurrentThread"/>, after minus before.
    /// </param>
    /// <param name="SameThread">
    /// Whether the thread that captured "after" is the same one that captured "before". A
    /// <see langword="false"/> here means the bytes above did not measure one continuous stretch
    /// of work and cannot be trusted either way.
    /// </param>
    /// <param name="Flushes">
    /// How many <see cref="ILiveRecordHandler.OnFlushAsync"/> calls fell inside the window. Should
    /// be exactly one — the whole measured batch arriving, and being drained, in a single pass —
    /// which is what the socket-buffer sizing is for.
    /// </param>
    private readonly record struct AllocationSample(long Decoded, long AllocatedBytes, bool SameThread, int Flushes);

    /// <summary>
    /// Counts records and, from inside <see cref="OnFlushAsync"/>, brackets the allocation the
    /// runner's loop makes while carrying exactly <paramref name="measuredTarget"/> records past
    /// the first <paramref name="warmupTarget"/>.
    /// </summary>
    /// <param name="warmupTarget">How many records to let pass, unmeasured, before bracketing.</param>
    /// <param name="measuredTarget">How many records the bracket covers.</param>
    /// <param name="sendMeasuredBatchAsync">
    /// Sends the measured batch. Called once, awaited to completion, from inside the flush that
    /// follows the <paramref name="warmupTarget"/>-th record — see the type-level remarks for why
    /// it cannot be the test method sending it instead.
    /// </param>
    /// <param name="allocatePerRecord">
    /// When <see langword="true"/>, <see cref="OnRecord"/> allocates a small array every call —
    /// <see cref="ExtensionsAllocationTests.TheMeasurementItself_NoticesADeliberateAllocation"/>'s
    /// counter-test, run through the identical bracket rather than a second one written by hand.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Why the bracket lives in <see cref="OnFlushAsync"/> and not in <see cref="OnRecord"/>.</b>
    /// <see cref="LiveSessionRunner.RunAsync"/>'s loop is drain-then-flush-then-fill, in that
    /// order (<c>LiveSessionRunner.PumpAsync</c>'s own remarks): every record in a batch is
    /// drained before the flush that follows it, and the fill that fetches the *next* batch
    /// happens only after that flush returns. So the flush immediately after the
    /// <paramref name="warmupTarget"/>-th record runs exactly once, after every warm-up record has
    /// been handed to <see cref="OnRecord"/> and before anything has attempted to read the
    /// measured batch — the same point in the loop where <c>LiveAllocationTests</c> takes "before",
    /// just reached from inside the loop instead of from outside it. The flush immediately after
    /// the <c>(warmupTarget + measuredTarget)</c>-th record is the same boundary for "after": every
    /// measured record has been drained, and the metrics publish for that drain has already run,
    /// but the next fill — which would suspend waiting for a stream end or more data — has not
    /// started yet.
    /// </para>
    /// <para>
    /// <b>The "before" branch is the only one that ever awaits</b>, and it does so once, for the
    /// call that sends the measured batch — kept in its own method so the ordinary, empty flushes
    /// that happen during warm-up and after the bracket closes stay a synchronous
    /// <see cref="ValueTask.CompletedTask"/> return, exactly like every other test handler's
    /// <c>OnFlushAsync</c> in this suite. "After" is computed before <see cref="Measured"/> is
    /// completed, not after, so the completion source's own continuation-scheduling cost — which
    /// can allocate — falls outside the measured delta rather than inside it.
    /// </para>
    /// <para>
    /// <b>The guard flags matter because a flush can fire with an unchanged count.</b> A drain
    /// that found zero new records — a read that delivered bytes but not a whole record yet — still
    /// reaches the flush at the end of that loop iteration. Capturing "before" again on a repeat
    /// would move it later than the true boundary and cost an extra <see cref="GC.Collect()"/>
    /// for nothing; the flags make each capture happen exactly once.
    /// </para>
    /// </remarks>
    private sealed class MeasuringHandler(
        int warmupTarget,
        int measuredTarget,
        Func<CancellationToken, Task> sendMeasuredBatchAsync,
        bool allocatePerRecord = false)
        : ILiveRecordHandler
    {
        private readonly TaskCompletionSource<AllocationSample> _measured =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private long _seen;
        private bool _beforeCaptured;
        private bool _afterCaptured;
        private int _measuringThread;
        private long _before;
        private int _flushesWithinWindow;

        /// <summary>A small array kept alive so the compiler cannot optimise the allocation away.</summary>
        private byte[]? _sink;

        /// <summary>Completes once the measured batch's last record has been drained and "after" is captured.</summary>
        public Task<AllocationSample> Measured => _measured.Task;

        public void OnRecord(scoped RecordRef record)
        {
            Interlocked.Increment(ref _seen);

            if (allocatePerRecord)
            {
                _sink = new byte[8];
            }
        }

        public ValueTask OnFlushAsync(CancellationToken cancellationToken)
        {
            var seen = Interlocked.Read(ref _seen);

            if (!_beforeCaptured && seen == warmupTarget)
            {
                return CaptureBeforeThenSendAsync(cancellationToken);
            }

            if (_beforeCaptured && !_afterCaptured)
            {
                _flushesWithinWindow++;
            }

            if (!_afterCaptured && seen == (long)warmupTarget + measuredTarget)
            {
                CaptureAfter(seen);
            }

            return ValueTask.CompletedTask;
        }

        private async ValueTask CaptureBeforeThenSendAsync(CancellationToken cancellationToken)
        {
            _beforeCaptured = true;

            // Awaited to completion before this flush returns, so the runner's next FillBufferAsync
            // — issued right after — finds the whole measured batch already in the kernel's receive
            // buffer instead of racing to get there first. See the type-level remarks.
            await sendMeasuredBatchAsync(cancellationToken).ConfigureAwait(false);

            Settle();
            _measuringThread = Environment.CurrentManagedThreadId;
            _before = GC.GetAllocatedBytesForCurrentThread();
        }

        private void CaptureAfter(long seen)
        {
            _afterCaptured = true;

            // Computed before the TaskCompletionSource is touched at all: completing it can itself
            // allocate (scheduling the continuation that resumes the test's await), and that cost
            // must fall outside the number being reported, not inside it.
            var allocated = GC.GetAllocatedBytesForCurrentThread() - _before;
            var sameThread = Environment.CurrentManagedThreadId == _measuringThread;

            _measured.TrySetResult(new AllocationSample(seen - warmupTarget, allocated, sameThread, _flushesWithinWindow));
        }

        private static void Settle()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}

/// <summary>
/// The xUnit collection definition for <see cref="ExtensionsAllocationTests.CollectionName"/> —
/// see that type's remarks. A marker type only; xUnit discovers this attribute by scanning the
/// assembly for it, not by any caller referencing this type directly.
/// </summary>
[CollectionDefinition(ExtensionsAllocationTests.CollectionName, DisableParallelization = true)]
public sealed class ExtensionsAllocationCollectionMarker;
