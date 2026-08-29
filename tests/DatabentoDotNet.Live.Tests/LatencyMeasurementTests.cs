using DatabentoDotNet.Dbn;
using DatabentoDotNet.Dbn.Publishers;
using NodaTime;

namespace DatabentoDotNet.Live.Tests;

/// <summary>
/// The whole of #65's measurement path, driven against <see cref="MockLiveGateway"/> for free.
/// </summary>
/// <remarks>
/// <para>
/// <b>The mock cannot say what the latency is, and it does not try.</b> Over a loopback socket the
/// figures are about the loopback socket. What it <em>can</em> settle is everything around them:
/// that the subtraction goes the right way round, that it reads <c>ts_out</c> rather than one of
/// the two other timestamps on the same record, that gateway-generated records are excluded and
/// counted, that a negative observation survives to the report, and that the report renders. All
/// of that is mechanism, and mechanism is exactly what a mock is good for — the same division
/// ROADMAP.md §4 draws for allocation.
/// </para>
/// <para>
/// <b>The gateway's clock is injected, which is what makes the headline check exact.</b>
/// <see cref="MockLiveGateway"/> stamps <c>ts_out</c> from an <see cref="IClock"/> it is handed,
/// so setting that clock a known distance from ours turns "is this latency plausible?" into an
/// answer known before the test runs. A measurement that read <c>ts_event</c> or <c>ts_recv</c> by
/// mistake would land three years out and fail here rather than in the one run that needs an open
/// market.
/// </para>
/// </remarks>
public class LatencyMeasurementTests
{
    private static readonly string DatasetName = Dataset.XnasItch.ToWireString();

    /// <summary>
    /// How far the gateway's clock is placed from ours. Large enough that no plausible loopback
    /// latency could account for it, so the assertion is about the subtraction rather than about
    /// the machine.
    /// </summary>
    private static readonly Duration ClockOffset = Duration.FromSeconds(5);

    /// <summary>
    /// The slack allowed on top of <see cref="ClockOffset"/>: the test's own wall-clock time
    /// between anchoring the gateway's clock and draining the records.
    /// </summary>
    private static readonly Duration Slack = Duration.FromSeconds(30);

    private const int Records = 64;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// The headline subtraction, checked against an answer known in advance: with the gateway's
    /// clock five seconds behind ours, every transport observation is five seconds plus the time
    /// the test itself took.
    /// </summary>
    /// <remarks>
    /// This is the assertion that pins <c>ts_out</c> as the field being read. A record built by
    /// <see cref="SyntheticMbo"/> carries a <c>ts_event</c> and a <c>ts_recv</c> in 2023, so a
    /// measurement that reached for either would report a latency of about three years and fail
    /// the upper bound by nine orders of magnitude.
    /// </remarks>
    [Fact]
    public async Task Transport_MeasuresTsOutToDelivery_InTheRightDirection()
    {
        var anchor = SystemClock.Instance.GetCurrentInstant();
        var measurement = await MeasureAsync(anchor - ClockOffset);

        Assert.Equal(Records, measurement.Count);

        foreach (var observed in measurement.Transport)
        {
            Assert.InRange(
                observed,
                ClockOffset.ToInt64Nanoseconds(),
                (ClockOffset + Slack).ToInt64Nanoseconds());
        }
    }

    /// <summary>
    /// A gateway clock ahead of ours produces negative observations, and they reach the report
    /// intact.
    /// </summary>
    /// <remarks>
    /// <b>This is the honesty property, tested rather than asserted in a comment.</b> One-way
    /// latency between unsynchronised clocks is not observable, and a negative figure is the only
    /// direct evidence the measurement has that the two disagree. Clamping it to zero — the
    /// obvious defensive move — would replace that evidence with a plausible number, which is the
    /// failure mode this repository's date handling exists to prevent, in a different unit.
    /// </remarks>
    [Fact]
    public async Task Transport_WithAGatewayClockAheadOfOurs_ReportsNegativeLatencies()
    {
        var anchor = SystemClock.Instance.GetCurrentInstant();
        var measurement = await MeasureAsync(anchor + ClockOffset);

        Assert.Equal(Records, measurement.Count);
        Assert.All(measurement.Transport, observed => Assert.True(observed < 0));

        var transport = measurement.Summarize()[1];
        Assert.True(transport.MinimumNanoseconds < 0);
        Assert.True(transport.P50Nanoseconds < 0);

        // The spread is unaffected by the offset that made every figure negative, which is the
        // whole reason the report carries it.
        Assert.True(transport.P99AboveFloorNanoseconds >= 0);
    }

    /// <summary>
    /// The drain series never goes backwards, because both of its stamps come from one monotonic
    /// clock. The billable test asserts the same thing about its own run; this proves the
    /// assertion can be satisfied at all.
    /// </summary>
    [Fact]
    public async Task Drain_IsNeverNegative_BecauseBothStampsAreMonotonic()
    {
        var measurement = await MeasureAsync(SystemClock.Instance.GetCurrentInstant());

        Assert.NotEmpty(measurement.Drain);
        Assert.All(measurement.Drain, observed => Assert.True(observed >= 0));
    }

    /// <summary>
    /// <c>ts_recv</c> and <c>ts_event</c> differ on an MBO record, so the gateway-internal series
    /// is populated rather than silently skipped.
    /// </summary>
    [Fact]
    public async Task GatewayInternal_IsCollected_ForRecordsThatCarryATsRecv()
    {
        var measurement = await MeasureAsync(SystemClock.Instance.GetCurrentInstant());

        Assert.Equal(Records, measurement.GatewayInternal.Count);
    }

    /// <summary>
    /// Heartbeats are counted and excluded rather than measured, so a quiet session cannot pad the
    /// sample with the best case the transport ever has.
    /// </summary>
    [Fact]
    public async Task GatewayGeneratedRecords_AreExcludedFromTheSample_AndCounted()
    {
        const int Heartbeats = 5;

        await using var gateway = new MockLiveGateway(
            DatasetName, sendTsOut: true, clock: new FixedClock(SystemClock.Instance.GetCurrentInstant()));
        await using var client = Client(gateway);
        await StartSessionAsync(gateway, client);

        for (var i = 0; i < Heartbeats; i++)
        {
            await gateway.SendRecordAsync(SyntheticSystemMsg.Heartbeat(SyntheticMbo.FirstTsRecv), Cancel);
        }

        for (var i = 0; i < Records; i++)
        {
            await gateway.SendRecordAsync(SyntheticMbo.Record((uint)(i + 1)), Cancel);
        }

        var measurement = await LatencyMeasurement.CollectAsync(
            client, Records, Duration.FromSeconds(30), Cancel);

        Assert.Equal(Records, measurement.Count);
        Assert.Equal(Heartbeats, measurement.GatewayGenerated);
        Assert.Equal(Records, measurement.Transport.Count);
    }

    /// <summary>
    /// The report renders, carries the run's identity, and states the sample size — the three
    /// things #65's definition of done asks of it.
    /// </summary>
    [Fact]
    public async Task Render_CarriesTheDatasetTheDateAndTheSampleSize()
    {
        var startedAt = SystemClock.Instance.GetCurrentInstant();
        var measurement = await MeasureAsync(startedAt);

        var report = measurement.Render(new LatencyRunContext
        {
            Dataset = DatasetName,
            Schema = "mbo",
            Symbols = ["AAPL", "MSFT"],
            DbnVersion = DbnConstants.Version,
            TsOutNegotiated = true,
            StartedAt = startedAt,
            FinishedAt = startedAt + Duration.FromSeconds(12),
        });

        Assert.Contains(DatasetName, report, StringComparison.Ordinal);
        Assert.Contains("mbo", report, StringComparison.Ordinal);
        Assert.Contains("AAPL, MSFT", report, StringComparison.Ordinal);
        Assert.Contains("12.00 s", report, StringComparison.Ordinal);
        Assert.Contains($"{Records} measured", report, StringComparison.Ordinal);
        Assert.Contains("negotiated", report, StringComparison.Ordinal);

        // The three rows, and the caveat that makes the middle one readable.
        Assert.Contains("gateway internal", report, StringComparison.Ordinal);
        Assert.Contains("transport", report, StringComparison.Ordinal);
        Assert.Contains("drain", report, StringComparison.Ordinal);
        Assert.Contains("p99 - min", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// A report from a run that failed to negotiate <c>ts_out</c> says so where the figures are,
    /// rather than printing a transport row that looks like every other one.
    /// </summary>
    [Fact]
    public async Task Render_WhenTsOutWasNotNegotiated_SaysTheTransportRowIsMeaningless()
    {
        var startedAt = SystemClock.Instance.GetCurrentInstant();
        var measurement = await MeasureAsync(startedAt);

        var report = measurement.Render(new LatencyRunContext
        {
            Dataset = DatasetName,
            Schema = "mbo",
            Symbols = ["AAPL"],
            DbnVersion = DbnConstants.Version,
            TsOutNegotiated = false,
            StartedAt = startedAt,
            FinishedAt = startedAt,
        });

        Assert.Contains("NOT NEGOTIATED", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// Collection stops at the record target rather than reading the socket dry, so a caller that
    /// asked for a bounded sample gets one.
    /// </summary>
    [Fact]
    public async Task CollectAsync_StopsAtTheRecordTarget()
    {
        const int Target = 10;

        await using var gateway = new MockLiveGateway(
            DatasetName, sendTsOut: true, clock: new FixedClock(SystemClock.Instance.GetCurrentInstant()));
        await using var client = Client(gateway);
        await StartSessionAsync(gateway, client);

        for (var i = 0; i < Records; i++)
        {
            await gateway.SendRecordAsync(SyntheticMbo.Record((uint)(i + 1)), Cancel);
        }

        var measurement = await LatencyMeasurement.CollectAsync(
            client, Target, Duration.FromSeconds(30), Cancel);

        Assert.Equal(Target, measurement.Count);
    }

    /// <summary>
    /// The monotonic clock advances, and its readings are anchored near the wall clock rather than
    /// to the stopwatch's own arbitrary origin.
    /// </summary>
    /// <remarks>
    /// The arithmetic inside it splits whole seconds from the sub-second remainder to avoid an
    /// overflow that would appear only after nine seconds of running. This pins the ordinary case;
    /// the split is what keeps the long case from wrapping.
    /// </remarks>
    [Fact]
    public void MonotonicClock_AdvancesAndIsAnchoredToTheWallClock()
    {
        var clock = new LatencyMeasurement.MonotonicClock();

        var first = clock.NowNanoseconds();
        Thread.SpinWait(200_000);
        var second = clock.NowNanoseconds();

        Assert.True(second > first, "The monotonic clock did not advance across a spin.");
        Assert.True(clock.Elapsed > Duration.Zero);

        var wallClock = DbnTime.ToUnixNanoseconds(SystemClock.Instance.GetCurrentInstant());
        var drift = Math.Abs((long)second - (long)wallClock);

        Assert.True(
            drift < Duration.FromSeconds(10).ToInt64Nanoseconds(),
            $"The anchored clock reads {drift} ns from the wall clock, which means it is anchored to "
            + "the stopwatch's origin rather than to an instant.");
    }

    // ----------------------------------------------------------------------------- Helpers

    /// <summary>
    /// Runs a full session against the mock with its clock at <paramref name="gatewayNow"/>, and
    /// returns the measurement.
    /// </summary>
    private static async Task<LatencyMeasurement> MeasureAsync(Instant gatewayNow)
    {
        await using var gateway = new MockLiveGateway(DatasetName, sendTsOut: true, clock: new FixedClock(gatewayNow));
        await using var client = Client(gateway);
        await StartSessionAsync(gateway, client);

        for (var i = 0; i < Records; i++)
        {
            await gateway.SendRecordAsync(SyntheticMbo.Record((uint)(i + 1)), Cancel);
        }

        return await LatencyMeasurement.CollectAsync(client, Records, Duration.FromSeconds(30), Cancel);
    }

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

    private static LiveClient Client(MockLiveGateway gateway) => new()
    {
        ApiKey = new ApiKey(MockLiveGateway.TestApiKey),
        Dataset = gateway.Dataset,
        Gateway = gateway.Address,
        SendTsOut = true,
    };

    /// <summary>
    /// An <see cref="IClock"/> stopped at one instant, so the gateway's <c>ts_out</c> is known
    /// before the test runs.
    /// </summary>
    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }
}
