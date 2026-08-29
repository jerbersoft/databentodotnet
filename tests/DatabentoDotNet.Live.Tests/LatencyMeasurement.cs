using System.Diagnostics;
using System.Globalization;
using System.Text;
using DatabentoDotNet.Dbn;
using NodaTime;
using NodaTime.Text;

namespace DatabentoDotNet.Live.Tests;

/// <summary>
/// What a latency run was measuring, for the report's header.
/// </summary>
/// <remarks>
/// A latency figure without its dataset and its date is not a figure anyone can use, which is
/// #65's definition of done restated as a parameter object: the report cannot be rendered without
/// them.
/// </remarks>
public sealed record LatencyRunContext
{
    /// <summary>The dataset the session streamed, as the metadata reported it.</summary>
    public required string Dataset { get; init; }

    /// <summary>The schema subscribed to, in its wire spelling.</summary>
    public required string Schema { get; init; }

    /// <summary>The symbols subscribed to.</summary>
    public required IReadOnlyList<string> Symbols { get; init; }

    /// <summary>The DBN version the session negotiated.</summary>
    public required byte DbnVersion { get; init; }

    /// <summary>Whether the gateway stamped a send timestamp on every record.</summary>
    public required bool TsOutNegotiated { get; init; }

    /// <summary>When <c>start_session</c> was sent.</summary>
    public required Instant StartedAt { get; init; }

    /// <summary>When collection stopped.</summary>
    public required Instant FinishedAt { get; init; }
}

/// <summary>
/// Collects the three latency series #65 reports, over any <see cref="LiveClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate from the session that pays for it, so the whole path can be exercised for free.</b>
/// <c>RealGatewayLatencyTests</c> is the only caller that spends money and the only one that
/// produces a number worth recording — but the collection loop, the exclusion rule, the clock
/// arithmetic and the report are all mechanism, and mechanism can be driven by
/// <see cref="MockLiveGateway"/>. <c>LatencyMeasurementTests</c> does exactly that, on every
/// <c>dotnet test</c>, with the gateway's clock set to a known offset so the headline subtraction
/// is checked against an answer known in advance.
/// </para>
/// <para>
/// That split is the same one ROADMAP.md §4 draws for allocation, and for the same reason: what
/// only a real gateway can settle is a small, specific claim, and everything around it is cheaper
/// and better tested somewhere else. Debugging a report format during the one run that needs an
/// open market would be the opposite arrangement.
/// </para>
/// <para>
/// <b>Three series, because one number cannot be honest about a clock it does not own.</b> Only
/// the middle one spans two machines; see <see cref="MonotonicClock"/>.
/// </para>
/// </remarks>
public sealed class LatencyMeasurement
{
    private const long NanosecondsPerMicrosecond = 1_000;

    /// <summary><c>ts_recv</c> to <c>ts_out</c>: the gateway's own handling time.</summary>
    public List<long> GatewayInternal { get; } = [];

    /// <summary><c>ts_out</c> to delivery: transport, plus this client's read path.</summary>
    public List<long> Transport { get; } = [];

    /// <summary>Buffer read to delivery: this library's own decode and drain cost.</summary>
    public List<long> Drain { get; } = [];

    /// <summary>How many market-data records were measured.</summary>
    public int Count => Transport.Count;

    /// <summary>How many gateway-generated records were seen and excluded.</summary>
    public int GatewayGenerated { get; private set; }

    /// <summary>How many socket reads the sample came out of.</summary>
    public int Fills { get; private set; }

    /// <summary>
    /// Reads from <paramref name="client"/> until the record target or the budget is reached,
    /// stamping every record as it is handed over.
    /// </summary>
    /// <param name="client">A client with a started session.</param>
    /// <param name="recordTarget">How many market-data records to measure.</param>
    /// <param name="budget">How long to collect for.</param>
    /// <param name="cancellationToken">Cancels the read. Not caught here — see the remarks.</param>
    /// <returns>The collected series.</returns>
    /// <remarks>
    /// <b>Cancellation is not swallowed.</b> Cancelling a <see cref="LiveClient.FillBufferAsync"/>
    /// ends the session (PORTING.md §1), so a caller that wants a partial sample out of a tripped
    /// deadline has to say so by catching — and the one caller that does keeps the measurement it
    /// paid for. Catching here would have hidden a cancelled run behind a short one.
    /// </remarks>
    public static async Task<LatencyMeasurement> CollectAsync(
        LiveClient client,
        int recordTarget,
        Duration budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recordTarget);

        var measurement = new LatencyMeasurement();
        var clock = new MonotonicClock();

        while (measurement.Count < recordTarget && clock.Elapsed < budget)
        {
            // Stamped before the drain rather than after it, so "how long did this record wait
            // behind the ones ahead of it in the same buffer" is measured from when the bytes
            // landed rather than from when we got round to them.
            var filledAt = clock.NowNanoseconds();
            DrainRecords(client, measurement, clock, filledAt, recordTarget);

            if (measurement.Count >= recordTarget || clock.Elapsed >= budget)
            {
                break;
            }

            if (await client.FillBufferAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                break;
            }

            measurement.Fills++;
        }

        return measurement;
    }

    /// <summary>
    /// Decodes every record already buffered, stamping each as it is handed over.
    /// </summary>
    /// <remarks>
    /// Non-<c>async</c> because a <see cref="RecordRef"/> cannot be in scope across an
    /// <c>await</c> — the constraint that shapes the public API, applied to its own measurement.
    /// The clock is read <em>inside</em> the loop rather than once around it: #65 asks for the
    /// moment the caller has the record, and for the second and later records of one buffer read
    /// those are different moments.
    /// </remarks>
    private static void DrainRecords(
        LiveClient client,
        LatencyMeasurement measurement,
        MonotonicClock clock,
        ulong filledAt,
        int recordTarget)
    {
        while (measurement.Count < recordTarget && client.TryNextRecord(out var record))
        {
            measurement.Observe(record, filledAt, clock.NowNanoseconds());
        }
    }

    /// <summary>
    /// Records one delivered record's three latencies, or counts it as excluded.
    /// </summary>
    /// <param name="record">The record, as handed over.</param>
    /// <param name="filledAt">When the buffer read that produced it completed.</param>
    /// <param name="delivered">When it was handed over.</param>
    /// <remarks>
    /// <para>
    /// <b>Gateway-generated records are excluded, and that is a measurement decision rather than
    /// tidiness.</b> A <c>SystemMsg</c> heartbeat, an <c>ErrorMsg</c>, and the
    /// <c>SymbolMappingMsg</c>s at the head of every session are emitted on the gateway's own
    /// schedule, which means they arrive on an idle socket. Their transport latency is real, but
    /// it is the best case the path ever has, and mixing a session's worth of them into a sample
    /// of market data moves p50 toward a number no consumer will ever see. They are counted and
    /// reported separately instead.
    /// </para>
    /// <para>
    /// <b>The <c>ts_recv</c> guard is not defensive either.</b> Not every schema's records carry
    /// one, and <see cref="RecordRef.IndexTs"/> falls back to <c>ts_event</c> where the struct has
    /// no <c>ts_recv</c> field. Subtracting that fallback would be measuring the gateway against
    /// its own send clock, so those records are left out of that one series and the report prints
    /// its <c>n</c> separately.
    /// </para>
    /// </remarks>
    public void Observe(RecordRef record, ulong filledAt, ulong delivered)
    {
        if (record.Has<SystemMsg>() || record.Has<ErrorMsg>() || record.Has<SymbolMappingMsg>())
        {
            GatewayGenerated++;
            return;
        }

        // Signed on purpose: the gateway's clock and ours are not synchronised, so a negative
        // transport figure is a legitimate observation about the clocks rather than an error to
        // clamp away. Both operands are nanosecond counts around 1.8e18, well inside a long.
        var sentAt = (long)record.TsOut;

        Transport.Add((long)delivered - sentAt);
        Drain.Add((long)delivered - (long)filledAt);

        var indexTs = record.IndexTs;
        if (!DbnTime.IsUndefined(indexTs) && indexTs != record.Header.TsEvent)
        {
            GatewayInternal.Add(sentAt - (long)indexTs);
        }
    }

    /// <summary>Reduces the three series to the report's rows, in reading order.</summary>
    /// <returns>The summaries.</returns>
    public IReadOnlyList<LatencySummary> Summarize() =>
    [
        LatencyStatistics.Summarize("gateway internal (ts_recv -> ts_out)", GatewayInternal.ToArray()),
        LatencyStatistics.Summarize("transport (ts_out -> delivered)", Transport.ToArray()),
        LatencyStatistics.Summarize("drain (buffer read -> delivered)", Drain.ToArray()),
    ];

    /// <summary>
    /// Renders the report that gets pasted into ROADMAP.md §7.
    /// </summary>
    /// <param name="context">What was measured, and when.</param>
    /// <returns>The report, as a multi-line string.</returns>
    public string Render(LatencyRunContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        const string Rule = "==========================================================================================";

        var summaries = Summarize();
        var report = new StringBuilder();

        report.AppendLine().AppendLine(Rule);
        report.AppendLine(" Live end-to-end latency — #65");
        report.AppendLine(Rule);
        report.AppendLine(CultureInfo.InvariantCulture, $" dataset      {context.Dataset}");
        report.AppendLine(CultureInfo.InvariantCulture, $" schema       {context.Schema}");
        report.AppendLine(CultureInfo.InvariantCulture, $" symbols      {string.Join(", ", context.Symbols)}");
        report.AppendLine(CultureInfo.InvariantCulture, $" dbn version  {context.DbnVersion}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $" ts_out       {(context.TsOutNegotiated ? "negotiated" : "NOT NEGOTIATED — the transport row is meaningless")}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $" measured     {InstantPattern.ExtendedIso.Format(context.StartedAt)}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $" duration     {Seconds(context.FinishedAt - context.StartedAt)}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $" records      {Count} measured, {GatewayGenerated} gateway-generated (excluded), "
            + $"over {Fills} buffer read(s)");
        report.AppendLine(Rule);
        report.AppendLine(LatencyStatistics.RenderTable(summaries));
        report.AppendLine(Rule);
        report.Append(
            """
             How to read this
             ----------------
             gateway internal   ts_recv -> ts_out. Both stamps are the gateway's own clock, so no
                                clock of ours enters it: this is the gateway's handling time, exact.
             transport          ts_out -> delivered. THE HEADLINE, and the only row that spans two
                                machines' clocks. Any offset between them is added to every figure
                                in it — including a negative one, which is reported rather than
                                clamped because it is the only direct evidence the clocks disagree.
             drain              buffer read -> delivered. This library's own cost: decoding, and the
                                wait behind earlier records of the same read. One machine, one
                                monotonic clock, no skew.

             One-way latency between unsynchronised clocks is not observable, so the absolute
             transport figures are only as good as the two clocks. A constant offset cancels out of
             any difference between two observations in the same row, so this survives it:

            """);
        report.AppendLine();

        foreach (var summary in summaries)
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"   {summary.Name,-38} p99 - min = {Microseconds(summary.P99AboveFloorNanoseconds)}");
        }

        report.AppendLine();
        report.AppendLine(" Record the table and this header together in ROADMAP.md §7. A latency figure");
        report.AppendLine(" without its date and its dataset is not a figure anyone can use.");
        report.AppendLine(Rule);

        return report.ToString();
    }

    private static string Seconds(Duration duration) =>
        ((double)duration.ToInt64Nanoseconds() / NodaConstants.NanosecondsPerSecond)
            .ToString("F2", CultureInfo.InvariantCulture) + " s";

    private static string Microseconds(long nanoseconds) =>
        ((double)nanoseconds / NanosecondsPerMicrosecond).ToString("F1", CultureInfo.InvariantCulture) + " us";

    /// <summary>
    /// A wall-clock anchor plus a monotonic stopwatch, which is what a latency measurement needs
    /// and what neither alone provides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The anchor is needed because <c>ts_out</c> is an epoch timestamp</b>, so the local
    /// reading it is subtracted from has to be one too. <see cref="Stopwatch"/> alone has no epoch.
    /// </para>
    /// <para>
    /// <b>The stopwatch is needed because the wall clock is neither monotonic nor fine.</b> An NTP
    /// correction during a sixty-second collection would step every observation after it by
    /// however far the daemon moved the clock, and nothing in the output would say so. Reading the
    /// wall clock once and advancing it by a monotonic delta means the run is measured on one
    /// clock that only goes forwards.
    /// </para>
    /// <para>
    /// <b>What it does not fix is the offset to the gateway's clock.</b> Anchoring makes the
    /// measurement self-consistent; it cannot make it correct, because one-way latency between
    /// unsynchronised clocks is not observable at all. That is why the report carries
    /// <c>p99 - min</c> beside every absolute figure.
    /// </para>
    /// </remarks>
    public sealed class MonotonicClock
    {
        private readonly ulong _anchorNanoseconds;
        private readonly long _anchorTimestamp;

        /// <summary>Anchors the clock to the current instant.</summary>
        public MonotonicClock()
        {
            // DbnTime is the one crossing between NodaTime and DBN's ulong nanoseconds, in both
            // directions — see CLAUDE.md. A second conversion here would be a second place for the
            // sentinel and range checks to go missing.
            _anchorNanoseconds = DbnTime.ToUnixNanoseconds(SystemClock.Instance.GetCurrentInstant());
            _anchorTimestamp = Stopwatch.GetTimestamp();
        }

        /// <summary>Nanoseconds since the UNIX epoch, on the anchored monotonic clock.</summary>
        /// <returns>The current reading.</returns>
        public ulong NowNanoseconds() => _anchorNanoseconds + ElapsedNanoseconds();

        /// <summary>How long this clock has been running.</summary>
        public Duration Elapsed => Duration.FromNanoseconds((long)ElapsedNanoseconds());

        private ulong ElapsedNanoseconds()
        {
            var ticks = Stopwatch.GetTimestamp() - _anchorTimestamp;

            // Split rather than `ticks * NanosecondsPerSecond / Frequency`. Stopwatch.Frequency is
            // 1 GHz on Linux and macOS, so that multiplication overflows a long after about nine
            // seconds — silently — and this clock runs for sixty. Whole seconds and the sub-second
            // remainder are each exact and neither can overflow: the remainder is below Frequency,
            // so its product is at most 1e18.
            var whole = ticks / Stopwatch.Frequency;
            var fraction = ticks % Stopwatch.Frequency;

            return (ulong)((whole * NodaConstants.NanosecondsPerSecond)
                + (fraction * NodaConstants.NanosecondsPerSecond / Stopwatch.Frequency));
        }
    }
}
