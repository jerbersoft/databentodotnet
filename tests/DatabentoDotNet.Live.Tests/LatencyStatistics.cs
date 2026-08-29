using System.Globalization;
using System.Text;

namespace DatabentoDotNet.Live.Tests;

/// <summary>
/// One measured latency distribution, reduced to the figures #65 asks for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nanoseconds, and signed.</b> The wire is unsigned nanoseconds, but a latency computed
/// across two machines' clocks can legitimately come out negative — see
/// <see cref="LatencyStatistics"/>. Clamping that to zero would turn the one observation that
/// proves the clocks disagree into a plausible-looking figure that says nothing, so the type is
/// <see cref="long"/> all the way through and a negative minimum is reported as measured.
/// </para>
/// </remarks>
public readonly record struct LatencySummary
{
    /// <summary>What was measured, for the report's leftmost column.</summary>
    public required string Name { get; init; }

    /// <summary>How many observations the figures below are computed over.</summary>
    public required int Count { get; init; }

    /// <summary>The smallest observation.</summary>
    public required long MinimumNanoseconds { get; init; }

    /// <summary>The 50th percentile by nearest rank.</summary>
    public required long P50Nanoseconds { get; init; }

    /// <summary>The 99th percentile by nearest rank.</summary>
    public required long P99Nanoseconds { get; init; }

    /// <summary>The largest observation.</summary>
    public required long MaximumNanoseconds { get; init; }

    /// <summary>
    /// Whether <see cref="Count"/> is large enough for <see cref="P99Nanoseconds"/> to be a 99th
    /// percentile rather than a synonym for <see cref="MaximumNanoseconds"/>.
    /// </summary>
    /// <remarks>
    /// Below <see cref="LatencyStatistics.MinimumSamplesForP99"/> observations, nearest rank puts
    /// p99 on the last element — so the number is real but it is the maximum wearing a percentile's
    /// name. The report prints it either way and marks it, because suppressing it would hide the
    /// sample size rather than the misreading.
    /// </remarks>
    public bool IsP99Supported => Count >= LatencyStatistics.MinimumSamplesForP99;

    /// <summary>
    /// The spread above this series' own floor: <see cref="P99Nanoseconds"/> minus
    /// <see cref="MinimumNanoseconds"/>.
    /// </summary>
    /// <remarks>
    /// <b>This is the figure that survives unsynchronised clocks.</b> An offset between two
    /// machines' clocks is a constant added to every observation in a series, so it cancels out of
    /// any difference between two of them. The absolute percentiles are only as good as the clocks;
    /// this one is as good as the measurement.
    /// </remarks>
    public long P99AboveFloorNanoseconds => P99Nanoseconds - MinimumNanoseconds;
}

/// <summary>
/// Percentiles over a latency sample, and the report #65 exists to produce.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate from the session that feeds it, so that it can be tested for free.</b> Everything
/// here is arithmetic over a <see cref="long"/> array — no socket, no gateway, no key — so
/// <c>LatencyStatisticsTests</c> exercises it on every <c>dotnet test</c> while
/// <c>RealGatewayLatencyTests</c> stays behind two gates. That split follows the precedent
/// <c>AllocationTests</c> set: an instrument nobody checks can report a confident number and be
/// wrong, and the check has to be cheaper to run than the thing it measures or it will not be run.
/// </para>
/// <para>
/// <b>Nearest rank, not interpolation.</b> The p-th percentile is the observation at
/// <c>ceil(p/100 × n)</c>, one-based — so every figure printed is a latency that actually
/// occurred, rather than a weighted average of two that did. For a tail figure that is the more
/// useful of the two: "one request in a hundred was at least this slow" is a claim about the
/// system, where an interpolated p99 is a claim about the interpolation.
/// </para>
/// </remarks>
public static class LatencyStatistics
{
    /// <summary>
    /// The sample size below which a 99th percentile is arithmetically the maximum.
    /// </summary>
    /// <remarks>
    /// At <c>n = 100</c>, nearest rank puts p99 on the 99th of 100 observations — the first sample
    /// size at which one observation can sit above it. Below that, <c>ceil(0.99 × n) = n</c> for
    /// every <c>n</c>, and p99 and the maximum are the same element by construction.
    /// </remarks>
    public const int MinimumSamplesForP99 = 100;

    private const long NanosecondsPerMicrosecond = 1_000;

    /// <summary>
    /// Reduces one series of observations to its summary.
    /// </summary>
    /// <param name="name">What was measured, for the report.</param>
    /// <param name="observations">
    /// The observations, in any order. Copied before sorting, so the caller's array is untouched.
    /// </param>
    /// <returns>The summary, or a zeroed one with <c>Count = 0</c> when there are no observations.</returns>
    public static LatencySummary Summarize(string name, ReadOnlySpan<long> observations)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (observations.Length == 0)
        {
            return new LatencySummary
            {
                Name = name,
                Count = 0,
                MinimumNanoseconds = 0,
                P50Nanoseconds = 0,
                P99Nanoseconds = 0,
                MaximumNanoseconds = 0,
            };
        }

        var sorted = observations.ToArray();
        Array.Sort(sorted);

        return new LatencySummary
        {
            Name = name,
            Count = sorted.Length,
            MinimumNanoseconds = sorted[0],
            P50Nanoseconds = PercentileOfSorted(sorted, 50),
            P99Nanoseconds = PercentileOfSorted(sorted, 99),
            MaximumNanoseconds = sorted[^1],
        };
    }

    /// <summary>
    /// The <paramref name="percentile"/>-th percentile of an already-sorted sample, by nearest rank.
    /// </summary>
    /// <param name="sorted">The sample, ascending. Not checked — sorting it is the caller's job.</param>
    /// <param name="percentile">The percentile to take, in <c>(0, 100]</c>.</param>
    /// <returns>An observation from <paramref name="sorted"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="sorted"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="percentile"/> is outside <c>(0, 100]</c>.</exception>
    public static long PercentileOfSorted(ReadOnlySpan<long> sorted, double percentile)
    {
        if (sorted.Length == 0)
        {
            throw new ArgumentException("A percentile of an empty sample is not a number.", nameof(sorted));
        }

        if (percentile is <= 0 or > 100 || double.IsNaN(percentile))
        {
            throw new ArgumentOutOfRangeException(
                nameof(percentile),
                percentile,
                "A percentile is in (0, 100]. Zero has no nearest rank and above 100 has no element.");
        }

        // One-based rank, then clamped rather than trusted: ceil() on a double can land one past
        // the end for a percentile of exactly 100 at large n, and an off-by-one here would read
        // out of bounds rather than report a wrong latency.
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    /// <summary>
    /// Renders the summaries as the fixed-width block that gets pasted into ROADMAP.md §7.
    /// </summary>
    /// <param name="summaries">The series to render, in the order they should appear.</param>
    /// <returns>A multi-line table in microseconds, with no trailing newline.</returns>
    /// <remarks>
    /// Microseconds throughout, and stated in the header rather than adapted per row: the three
    /// series this report carries differ by three orders of magnitude, and a table that switched
    /// units per row would invite exactly the comparison it had just made invalid.
    /// </remarks>
    public static string RenderTable(IReadOnlyList<LatencySummary> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);

        const int NameWidth = 38;
        const int CountWidth = 7;
        const int FigureWidth = 12;

        var table = new StringBuilder();

        table.Append("series".PadRight(NameWidth))
             .Append("n".PadLeft(CountWidth))
             .Append("min".PadLeft(FigureWidth))
             .Append("p50".PadLeft(FigureWidth))
             .Append("p99".PadLeft(FigureWidth))
             .Append("max".PadLeft(FigureWidth))
             .Append("  (us)")
             .AppendLine();

        table.Append(new string('-', NameWidth + CountWidth + (FigureWidth * 4) + 6)).AppendLine();

        foreach (var summary in summaries)
        {
            table.Append(summary.Name.Length >= NameWidth ? summary.Name + " " : summary.Name.PadRight(NameWidth))
                 .Append(Column(summary.Count.ToString(CultureInfo.InvariantCulture), CountWidth));

            if (summary.Count == 0)
            {
                table.Append("  (no observations)").AppendLine();
                continue;
            }

            table.Append(Column(Microseconds(summary.MinimumNanoseconds), FigureWidth))
                 .Append(Column(Microseconds(summary.P50Nanoseconds), FigureWidth))
                 .Append(Column(Microseconds(summary.P99Nanoseconds), FigureWidth))
                 .Append(Column(Microseconds(summary.MaximumNanoseconds), FigureWidth));

            if (!summary.IsP99Supported)
            {
                table.Append("  p99 = max at this n");
            }

            table.AppendLine();
        }

        return table.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>
    /// Nanoseconds as microseconds to one decimal, invariant. Negative values keep their sign.
    /// </summary>
    private static string Microseconds(long nanoseconds) =>
        ((double)nanoseconds / NanosecondsPerMicrosecond).ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>
    /// One right-aligned cell, which keeps its separation from the previous one even when the
    /// value is too wide for the column.
    /// </summary>
    /// <remarks>
    /// <b>A plain <c>PadLeft</c> is wrong here, and it was wrong visibly.</b> Padding does nothing
    /// to a string already at the width, so two wide figures run together into one unreadable
    /// number and the whole table loses its alignment from that row down. The values that do this
    /// are exactly the ones worth reading — a latency that is orders of magnitude out is either the
    /// finding or the bug — so an over-wide cell takes a single leading space and pushes the row
    /// out rather than silently merging with its neighbour.
    /// </remarks>
    private static string Column(string text, int width) =>
        text.Length >= width ? " " + text : text.PadLeft(width);
}
