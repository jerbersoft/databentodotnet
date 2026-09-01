using System.Diagnostics.Metrics;

namespace DatabentoDotNet.Extensions.Hosting;

/// <summary>
/// The four instruments a live session publishes: records received, sessions started, reconnects
/// attempted, and how long each flush took.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every method here is written as though a listener is always attached, because in the test
/// that matters one is.</b> <see cref="Counter{T}.Add(T)"/> short-circuits when no
/// <see cref="MeterListener"/> is subscribed to the instrument — it checks a flag and returns. An
/// implementation that allocated per call would therefore measure as costing nothing in any test
/// that did not attach a listener, and would begin allocating on the first record the moment a
/// consumer wired up OpenTelemetry. <c>ExtensionsAllocationTests</c> attaches one for exactly that
/// reason, so the shapes below are not defensive: they are what the guarantee is made of.
/// </para>
/// <para>
/// <b>The session tag arrives by <see langword="in"/> and goes straight to the single-tag
/// overload.</b> That overload is the one that does not allocate. Building a tag at the call site
/// would, and so would <c>TagList</c> and the <c>params</c> overloads — each of which is the
/// obvious way to write this and each of which puts an allocation on the one path in this library
/// that promises none. A <see cref="string"/> sitting in the tag's <see cref="object"/> value does
/// not box, so the pair itself is a plain 16-byte struct copied onto the stack.
/// </para>
/// <para>
/// <b>What is <em>not</em> here is a per-record call.</b> The runner counts records into a
/// <see cref="long"/> local inside its drain loop and calls <see cref="RecordsReceived"/> once
/// after it, which reports the same number for a fraction of the work. Nothing in this type should
/// ever be invited into that loop.
/// </para>
/// </remarks>
public sealed class LiveSessionMetrics : IDisposable
{
    /// <summary>
    /// The meter every instrument here is published on:
    /// <c>DatabentoDotNet.Extensions.Hosting</c>.
    /// </summary>
    /// <remarks>
    /// The value is load-bearing rather than cosmetic: it is what a consumer passes to
    /// <c>AddMeter</c> when configuring OpenTelemetry, and what a
    /// <see cref="MeterListener.InstrumentPublished"/> filter matches on. Renaming it silently
    /// stops an operator's dashboards receiving anything.
    /// </remarks>
    public const string MeterName = "DatabentoDotNet.Extensions.Hosting";

    private readonly Meter _meter;
    private readonly bool _ownsMeter;
    private readonly Counter<long> _recordsReceived;
    private readonly Counter<long> _sessionsStarted;
    private readonly Counter<long> _reconnectsAttempted;
    private readonly Histogram<double> _flushDuration;

    /// <summary>Creates the instruments on a <see cref="Meter"/> this instance owns and disposes.</summary>
    /// <remarks>
    /// The constructor for a caller with no host — a test, a console program, a benchmark. A host
    /// that has called <c>AddMetrics</c> gets the <see cref="IMeterFactory"/> overload instead, and
    /// the container picks it without being told to: it is the constructor with the most
    /// parameters it can satisfy.
    /// </remarks>
    public LiveSessionMetrics()
        : this(new Meter(MeterName), ownsMeter: true)
    {
    }

    /// <summary>Creates the instruments on a <see cref="Meter"/> from <paramref name="meterFactory"/>.</summary>
    /// <param name="meterFactory">The host's factory, which owns every meter it hands out.</param>
    /// <remarks>
    /// <b><see cref="Dispose"/> does not dispose this one.</b> The factory cached it, may hand the
    /// same instance to something else, and disposes it itself when the container is torn down —
    /// disposing it here would stop an instrument somebody else is still writing to.
    /// </remarks>
    public LiveSessionMetrics(IMeterFactory meterFactory)
        : this(FromFactory(meterFactory), ownsMeter: false)
    {
    }

    private LiveSessionMetrics(Meter meter, bool ownsMeter)
    {
        _meter = meter;
        _ownsMeter = ownsMeter;

        _recordsReceived = meter.CreateCounter<long>(
            "databento.live.records.received",
            unit: "{record}",
            description: "Records handed to the session's handler.");

        _sessionsStarted = meter.CreateCounter<long>(
            "databento.live.sessions.started",
            unit: "{session}",
            description: "Live sessions opened, including those re-established by a reconnect.");

        _reconnectsAttempted = meter.CreateCounter<long>(
            "databento.live.reconnects.attempted",
            unit: "{attempt}",
            description: "Reconnection attempts, successful or not. Each success is a newly billed session.");

        _flushDuration = meter.CreateHistogram<double>(
            "databento.live.flush.duration",
            unit: "ms",
            description: "How long the handler's OnFlushAsync took, once per drained buffer.");
    }

    /// <summary>Reports the records one flush carried.</summary>
    /// <param name="count">How many records were drained since the previous flush.</param>
    /// <param name="session">The pre-built session tag — see the type's remarks.</param>
    /// <remarks>
    /// Called once per flush, never once per record. See the type's remarks for why that is a
    /// requirement rather than an optimisation.
    /// </remarks>
    public void RecordsReceived(long count, in KeyValuePair<string, object?> session) =>
        _recordsReceived.Add(count, session);

    /// <summary>Reports that a session was established.</summary>
    /// <param name="session">The pre-built session tag — see the type's remarks.</param>
    /// <remarks>
    /// <para>
    /// <b>One per session opened, restarts included</b> — both the runner's first
    /// <c>StartSessionAsync</c> and every successful reconnect, because a reconnect calls
    /// <c>StartAsync</c> and opens a session exactly as the first one did.
    /// </para>
    /// <para>
    /// <b>That is what the counter is for, and why it cannot skip the reconnect path.</b> Every
    /// successful restart is a newly billed session — <see cref="ReconnectSupervisor"/> says so at
    /// length, and bounds the attempts for that reason. An operator watches this counter to know
    /// how many billable sessions a process has opened, so one that under-reported on precisely
    /// the path documented as newly billed would be worse than no counter at all: it would still
    /// read as authoritative.
    /// </para>
    /// <para>
    /// <see cref="ReconnectAttempted"/> cannot stand in for the difference. It counts
    /// <em>attempts</em>, and an attempt that fails opens nothing and bills nothing. Subtracting
    /// the two answers a third question — how much the connection flapped — rather than restating
    /// either.
    /// </para>
    /// </remarks>
    public void SessionStarted(in KeyValuePair<string, object?> session) =>
        _sessionsStarted.Add(1, session);

    /// <summary>Reports that the backoff is about to make an attempt.</summary>
    /// <param name="session">The pre-built session tag — see the type's remarks.</param>
    /// <remarks>
    /// Published before the attempt rather than after it, so a session that is failing to come
    /// back is visible while it is failing rather than only once it gives up.
    /// </remarks>
    public void ReconnectAttempted(in KeyValuePair<string, object?> session) =>
        _reconnectsAttempted.Add(1, session);

    /// <summary>Reports how long a flush took.</summary>
    /// <param name="milliseconds">The elapsed time, computed from <c>Stopwatch.GetTimestamp()</c>.</param>
    /// <param name="session">The pre-built session tag — see the type's remarks.</param>
    /// <remarks>
    /// A <see cref="double"/> of milliseconds rather than a <c>Duration</c> or the banned
    /// <c>TimeSpan</c>: OpenTelemetry's histogram buckets are numbers, and the runner's caller-side
    /// arithmetic never names a date/time type at all.
    /// </remarks>
    public void FlushCompleted(double milliseconds, in KeyValuePair<string, object?> session) =>
        _flushDuration.Record(milliseconds, session);

    /// <summary>Disposes the <see cref="Meter"/>, but only the one this instance created.</summary>
    public void Dispose()
    {
        if (_ownsMeter)
        {
            _meter.Dispose();
        }
    }

    private static Meter FromFactory(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        return meterFactory.Create(MeterName);
    }
}
