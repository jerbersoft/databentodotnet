using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Live;
using DatabentoDotNet.Live.Tests;

namespace DatabentoDotNet.Benchmarks;

/// <summary>
/// The same measurement over the live path: <see cref="MockLiveGateway"/> replaying synthetic MBO
/// across a loopback socket into <see cref="LiveClient.FillBufferAsync"/> and
/// <see cref="LiveClient.TryNextRecord"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a second benchmark at all.</b> <see cref="DecodeBenchmarks"/> covers the state machine
/// over an array. The live path adds the socket read seam and the <see cref="Memory{T}"/>
/// projection it needs — the largest single allocation risk on the read path, per PORTING.md §1 —
/// and wraps both in an <c>async</c> method, which is where a per-call allocation hides in plain
/// sight. Measuring only the array path would leave exactly the seam that was hardest to get
/// right unmeasured.
/// </para>
/// <para>
/// <b>The gateway's replay is in <c>[IterationSetup]</c>, not in the benchmark method,</b> and
/// that is what makes the <c>Allocated</c> column mean anything. Both halves of a loopback socket
/// run in this process, and the mock's own replay allocates roughly a record's worth per record —
/// enough to swamp the figure the benchmark exists to report. Filling the socket first and
/// measuring only the drain leaves the client's read path alone in the measurement.
/// </para>
/// <para>
/// It costs a job configuration to do that: <see cref="RunStrategy.Monitoring"/> with
/// <c>invocationCount: 1</c>, so BenchmarkDotNet runs the setup before <em>every</em> invocation
/// rather than once per iteration. The default strategy would replay one batch and then drain it
/// many times over, which would block on an empty socket at the second invocation.
/// </para>
/// <para>
/// <b>The figure will not be a flat zero, and that is not a regression.</b> The benchmark method
/// is <c>async Task&lt;int&gt;</c>, so its state machine and returned task are charged here — a
/// small constant, independent of <see cref="Records"/>. The client's own per-record figure, with
/// that harness cost outside the measurement, is asserted to be exactly zero in
/// <c>LiveAllocationTests</c>. What this row catches is the figure <em>scaling</em> with the
/// record count.
/// </para>
/// <para>
/// <b>MBO, and synthetic.</b> The densest schema DBN defines, and one no dataset this account
/// licenses offers. ROADMAP.md §4 records why that costs nothing: allocation is a property of the
/// code path, and nothing between the socket and <see cref="RecordRef"/> can tell a real gateway
/// from this one.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 3, iterationCount: 15, invocationCount: 1)]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification =
        "BenchmarkDotNet owns this object's lifetime and calls the [GlobalCleanup] method, which "
        + "is where both fields are disposed. Both need DisposeAsync, and an IDisposable that "
        + "blocked on it would deadlock-bait the harness for no gain: nothing but the harness "
        + "ever constructs this type.")]
public class LiveBenchmarks
{
    /// <summary>
    /// Records per invocation. Kept inside a loopback socket's receive buffer at
    /// <see cref="MboMsg.WireSize"/> bytes each, so the replay never blocks waiting for the client
    /// to catch up — which would measure the kernel's flow control rather than the decoder.
    /// </summary>
    [Params(512)]
    public int Records { get; set; }

    private MockLiveGateway _gateway = null!;
    private LiveClient _client = null!;
    private uint _sequence;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _gateway = new MockLiveGateway("XNAS.ITCH");
        _client = new LiveClient
        {
            ApiKey = new ApiKey(MockLiveGateway.TestApiKey),
            Dataset = _gateway.Dataset,
            Gateway = _gateway.Address,
        };

        var handshake = _gateway.AuthenticateAsync();
        await _client.ConnectAsync();
        await _client.AuthenticateAsync();
        await handshake;

        var serving = _gateway.StartAsync();
        await _client.StartAsync();
        await serving;
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _client.DisposeAsync();
        await _gateway.DisposeAsync();
    }

    /// <summary>
    /// Fills the socket with one batch, outside the measurement.
    /// </summary>
    [IterationSetup]
    public void Replay()
    {
        // Synchronous by necessity: BenchmarkDotNet's iteration setup is void-returning. The
        // writes are to a loopback socket with a batch sized to fit its receive buffer, so this
        // completes without blocking — which is the same property that makes every read in the
        // measured drain complete synchronously.
        for (var i = 0; i < Records; i++)
        {
            _gateway.SendRecordAsync(SyntheticMbo.Record(++_sequence)).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// The measured half: read and decode <see cref="Records"/> records the gateway has already
    /// written.
    /// </summary>
    /// <returns>How many bytes decoded, returned so nothing is optimised away.</returns>
    [Benchmark(Description = "FillBufferAsync/TryNextRecord over a filled socket")]
    public async Task<int> RecordLoop()
    {
        var decoded = 0;
        var remaining = Records;

        while (remaining > 0)
        {
            var drained = Drain(_client, remaining, ref decoded);
            remaining -= drained;

            if (remaining == 0 || await _client.FillBufferAsync() == 0)
            {
                break;
            }
        }

        return decoded;
    }

    /// <summary>
    /// Decodes up to <paramref name="limit"/> buffered records. Separate and non-<c>async</c>
    /// because a <see cref="RecordRef"/> cannot be in scope across an <c>await</c>.
    /// </summary>
    private static int Drain(LiveClient client, int limit, ref int decoded)
    {
        var count = 0;

        while (count < limit && client.TryNextRecord(out var record))
        {
            decoded += record.SizeInBytes;
            count++;
        }

        return count;
    }
}
