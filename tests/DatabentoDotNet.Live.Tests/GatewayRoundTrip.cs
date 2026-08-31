using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using NodaTime;

namespace DatabentoDotNet.Live.Tests;

/// <summary>
/// The round trip to a live gateway, measured on one clock (#83).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists beside <see cref="LatencyMeasurement"/>.</b> That measurement's headline row
/// subtracts our reading from the gateway's <c>ts_out</c>, so it reads one end of an interval off
/// Databento's clock and the other off ours — and what comes out is the interval <em>plus the
/// distance between the two clocks' zeros</em>. Both real runs on 2026-08-31 returned a negative
/// median for exactly that reason. It is not a defect: one-way delay between unsynchronised clocks
/// is not observable at all, and the only quantities a single clock can observe are intervals on
/// itself and round trips.
/// </para>
/// <para>
/// <b>A round trip is the observable form of the same question.</b> Every stamp here comes from
/// <see cref="Stopwatch"/>, and every figure is the difference between two of them, so no epoch is
/// involved, no wall clock is read, and no offset can enter. The result is independent of the skew
/// that makes the transport row unreadable — which is the whole reason the two are worth comparing.
/// </para>
/// <para>
/// <b>Two series, because they are not the same quantity.</b> A TCP connect is a three-way
/// handshake the kernel completes with no application on the far side, so it is one network round
/// trip and nothing else. The authentication that follows is a round trip <em>plus</em> the gateway
/// reading a challenge response and validating a digest. Reporting one number for both would
/// present the gateway's CPU time as distance.
/// </para>
/// <para>
/// <b>Read the minimum, not the median.</b> Queueing, scheduling and a shared network only ever
/// add, so the floor of a sample is the best estimate of the path and every figure above it is
/// that path plus something. The first connect also pays DNS resolution, which is why one sample
/// would be the wrong measurement rather than a noisy one.
/// </para>
/// </remarks>
public sealed class GatewayRoundTrip
{
    /// <summary>TCP connect: one network round trip, no application on the far side.</summary>
    public List<long> Connect { get; } = [];

    /// <summary>
    /// Greeting, CRAM challenge, response and verdict: a round trip plus the gateway's own work.
    /// </summary>
    public List<long> Handshake { get; } = [];

    /// <summary>The address the last sample resolved to and connected against.</summary>
    public IPEndPoint? Endpoint { get; private set; }

    /// <summary>How many complete samples were collected.</summary>
    public int Samples => Connect.Count;

    /// <summary>
    /// Connects and authenticates <paramref name="samples"/> times, timing each leg.
    /// </summary>
    /// <param name="samples">How many round trips to measure.</param>
    /// <param name="prepare">
    /// Supplies a fresh, unconnected client for each sample. Against
    /// <see cref="MockLiveGateway"/> this is also where the gateway side is armed, because the mock
    /// accepts one connection at a time.
    /// </param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The completed measurement.</returns>
    /// <remarks>
    /// Each sample gets its own connection. Reusing one would measure the first round trip and then
    /// nine reads of an already-open socket, which is a different and much smaller number.
    /// </remarks>
    public static async Task<GatewayRoundTrip> MeasureAsync(
        int samples,
        Func<int, CancellationToken, Task<LiveClient>> prepare,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(samples, 1);
        ArgumentNullException.ThrowIfNull(prepare);

        var measurement = new GatewayRoundTrip();

        for (var sample = 0; sample < samples; sample++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var client = await prepare(sample, cancellationToken).ConfigureAwait(false);

            var opened = Stopwatch.GetTimestamp();
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var connected = Stopwatch.GetTimestamp();
            await client.AuthenticateAsync(cancellationToken).ConfigureAwait(false);
            var authenticated = Stopwatch.GetTimestamp();

            measurement.Connect.Add(ElapsedNanoseconds(opened, connected));
            measurement.Handshake.Add(ElapsedNanoseconds(connected, authenticated));
            measurement.Endpoint = client.Endpoint;

            await client.CloseAsync().ConfigureAwait(false);
        }

        return measurement;
    }

    /// <summary>Reduces both series to the report's rows, in reading order.</summary>
    /// <returns>The summaries.</returns>
    public IReadOnlyList<LatencySummary> Summarize() =>
    [
        LatencyStatistics.Summarize("connect (TCP handshake)", Connect.ToArray()),
        LatencyStatistics.Summarize("authenticate (greeting + CRAM)", Handshake.ToArray()),
    ];

    /// <summary>
    /// Renders the report that gets recorded in ROADMAP.md §7 beside #65's.
    /// </summary>
    /// <param name="dataset">The dataset whose gateway was measured.</param>
    /// <returns>The report, as a multi-line string.</returns>
    public string Render(string dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        const string Rule = "==========================================================================================";

        var summaries = Summarize();
        var report = new StringBuilder();

        report.AppendLine().AppendLine(Rule);
        report.AppendLine(" Gateway round trip — #83");
        report.AppendLine(Rule);
        report.AppendLine(CultureInfo.InvariantCulture, $" dataset      {dataset}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $" endpoint     {Endpoint?.ToString() ?? "(not resolved)"}");
        report.AppendLine(CultureInfo.InvariantCulture, $" samples      {Samples} connection(s)");
        report.AppendLine(Rule);
        report.AppendLine(LatencyStatistics.RenderTable(summaries));
        report.AppendLine(Rule);
        report.Append(
            """
             How to read this
             ----------------
             connect         A TCP three-way handshake, completed by the kernel on the far side with
                             no application involved. One network round trip and nothing else, so its
                             MINIMUM is the best estimate this machine can make of the path.
             authenticate    Greeting, CRAM challenge, our response, the gateway's verdict. One more
                             round trip PLUS the gateway hashing and comparing a digest, so it is an
                             upper bound on a round trip and never a measurement of one.

             Both rows are differences between two Stopwatch readings on this machine. No epoch is
             read and no wall clock is involved, so the clock offset that makes #65's transport row
             negative cannot enter either figure — which is what makes them worth comparing with it.

             Halving the connect minimum estimates the one-way leg, and that assumes the path is
             symmetric. It is a weaker assumption than "two wall clocks agree" and it is still an
             assumption: a one-way delay was not measured here and must not be reported as one.

            """);
        report.AppendLine(Rule);

        return report.ToString();
    }

    /// <summary>
    /// Nanoseconds between two <see cref="Stopwatch"/> readings.
    /// </summary>
    /// <remarks>
    /// Split into whole seconds plus a remainder rather than <c>ticks * 1e9 / Frequency</c>, for
    /// the reason <c>LatencyMeasurement.MonotonicClock</c> gives: on a platform whose
    /// <see cref="Stopwatch.Frequency"/> is 1 GHz the multiplication overflows a <see cref="long"/>
    /// after about nine seconds. <see cref="Stopwatch.GetElapsedTime(long, long)"/> would do this
    /// correctly and returns a <c>TimeSpan</c>, which `BannedSymbols.txt` forbids — see CLAUDE.md.
    /// </remarks>
    private static long ElapsedNanoseconds(long from, long to)
    {
        var ticks = to - from;
        var whole = ticks / Stopwatch.Frequency;
        var fraction = ticks % Stopwatch.Frequency;

        return (whole * NodaConstants.NanosecondsPerSecond)
            + (fraction * NodaConstants.NanosecondsPerSecond / Stopwatch.Frequency);
    }
}
