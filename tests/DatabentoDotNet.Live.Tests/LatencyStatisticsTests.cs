namespace DatabentoDotNet.Live.Tests;

/// <summary>
/// Tests for the instrument <c>RealGatewayLatencyTests</c> reports through.
/// </summary>
/// <remarks>
/// <para>
/// <b>Free, and that is the point.</b> The measurement itself needs a billable session and can
/// only run during market hours, so it runs rarely and its output is a number nobody can check by
/// inspection. Everything that turns observations into that number is arithmetic, so it lives in
/// <see cref="LatencyStatistics"/> and is pinned here on every <c>dotnet test</c> — the same
/// argument <c>AllocationTests</c> makes when it asserts that its own measurement notices a
/// deliberate allocation. A percentile function that is quietly off by one would produce a report
/// that looked exactly as credible as a correct one.
/// </para>
/// </remarks>
public class LatencyStatisticsTests
{
    /// <summary>
    /// Nearest rank on a sample whose answer can be read off by hand: 1..100, so the p-th
    /// percentile is p.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(50, 50)]
    [InlineData(99, 99)]
    [InlineData(100, 100)]
    public void PercentileOfSorted_OnOneToOneHundred_IsThePercentileItself(double percentile, long expected)
    {
        var sample = Enumerable.Range(1, 100).Select(value => (long)value).ToArray();

        Assert.Equal(expected, LatencyStatistics.PercentileOfSorted(sample, percentile));
    }

    /// <summary>
    /// The boundary the <see cref="LatencySummary.IsP99Supported"/> flag exists for, asserted from
    /// both sides: at 99 observations p99 is the last element, at 100 it is the second to last.
    /// </summary>
    [Fact]
    public void PercentileOfSorted_BelowOneHundredObservations_PutsP99OnTheMaximum()
    {
        var ninetyNine = Enumerable.Range(1, 99).Select(value => (long)value).ToArray();
        var oneHundred = Enumerable.Range(1, 100).Select(value => (long)value).ToArray();

        Assert.Equal(ninetyNine[^1], LatencyStatistics.PercentileOfSorted(ninetyNine, 99));
        Assert.NotEqual(oneHundred[^1], LatencyStatistics.PercentileOfSorted(oneHundred, 99));
    }

    /// <summary>
    /// Every figure a report prints has to be an observation that happened, which is what choosing
    /// nearest rank over interpolation buys. Asserted on a sample with a gap wide enough that an
    /// interpolating implementation could not land inside it by accident.
    /// </summary>
    [Fact]
    public void Percentiles_AreObservationsThatOccurred_NotInterpolationsBetweenThem()
    {
        long[] sample = [1, 2, 3, 1_000_000];

        foreach (var percentile in new double[] { 1, 25, 50, 75, 99, 100 })
        {
            Assert.Contains(LatencyStatistics.PercentileOfSorted(sample, percentile), sample);
        }
    }

    /// <summary>
    /// A summary is computed over a copy: the caller's array comes back in the order it went in.
    /// </summary>
    /// <remarks>
    /// The session collects three series into three lists and summarises each. Sorting in place
    /// would reorder observations that are positionally aligned across the three — record <c>i</c>'s
    /// transport latency and record <c>i</c>'s drain latency — and nothing downstream would report
    /// an error, because each series would still be internally consistent.
    /// </remarks>
    [Fact]
    public void Summarize_DoesNotReorderTheCallersObservations()
    {
        long[] observations = [5, 1, 4, 2, 3];

        _ = LatencyStatistics.Summarize("unchanged", observations);

        Assert.Equal([5, 1, 4, 2, 3], observations);
    }

    /// <summary>
    /// Negative observations survive to the report rather than being clamped away.
    /// </summary>
    /// <remarks>
    /// A negative transport latency means the gateway's clock is ahead of ours, and it is the only
    /// direct evidence of skew the measurement can produce. Clamping it to zero would replace that
    /// evidence with a plausible figure — the failure mode this repository's date handling exists
    /// to prevent, in a different unit.
    /// </remarks>
    [Fact]
    public void Summarize_KeepsNegativeObservations()
    {
        long[] skewed = [-4_000, -3_000, -2_500, -2_000];

        var summary = LatencyStatistics.Summarize("skewed", skewed);

        Assert.Equal(-4_000, summary.MinimumNanoseconds);
        Assert.Equal(-2_000, summary.MaximumNanoseconds);
        Assert.True(summary.P50Nanoseconds < 0);
    }

    /// <summary>
    /// The skew-invariant figure is invariant: adding a constant to every observation — which is
    /// exactly what a clock offset does — leaves it unchanged while moving every absolute figure.
    /// </summary>
    [Fact]
    public void P99AboveFloor_IsUnchangedByAConstantClockOffset()
    {
        long[] observations = [100, 250, 300, 900, 1_400];
        const long Offset = 8_000_000;

        var measured = LatencyStatistics.Summarize("measured", observations);
        var skewed = LatencyStatistics.Summarize("skewed", observations.Select(value => value + Offset).ToArray());

        Assert.Equal(measured.P99AboveFloorNanoseconds, skewed.P99AboveFloorNanoseconds);
        Assert.NotEqual(measured.P99Nanoseconds, skewed.P99Nanoseconds);
    }

    /// <summary>
    /// An empty series reports zero observations rather than throwing, because the session that
    /// feeds it can legitimately collect nothing — a market that is closed, or a schema whose
    /// records carry no <c>ts_recv</c>.
    /// </summary>
    [Fact]
    public void Summarize_WithNoObservations_ReportsAnEmptySummary()
    {
        var summary = LatencyStatistics.Summarize("nothing", []);

        Assert.Equal(0, summary.Count);
        Assert.False(summary.IsP99Supported);
    }

    /// <summary>
    /// A percentile of nothing is not a number, and says so rather than returning one.
    /// </summary>
    [Fact]
    public void PercentileOfSorted_WithNoObservations_Throws()
    {
        Assert.Throws<ArgumentException>(() => LatencyStatistics.PercentileOfSorted([], 50));
    }

    /// <summary>
    /// Percentiles outside <c>(0, 100]</c> are rejected rather than clamped.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(double.NaN)]
    public void PercentileOfSorted_OutsideTheValidRange_Throws(double percentile)
    {
        long[] sample = [1, 2, 3];

        Assert.Throws<ArgumentOutOfRangeException>(() => LatencyStatistics.PercentileOfSorted(sample, percentile));
    }

    /// <summary>
    /// The rendered table carries the sample size and marks a p99 that is arithmetically the
    /// maximum, so a short run cannot be read as a long one.
    /// </summary>
    [Fact]
    public void RenderTable_MarksAP99ThatIsOnlyTheMaximum()
    {
        var shortRun = LatencyStatistics.Summarize("short", [1_000, 2_000, 3_000]);
        var longRun = LatencyStatistics.Summarize(
            "long",
            Enumerable.Range(1, 500).Select(value => (long)value * 1_000).ToArray());

        var rendered = LatencyStatistics.RenderTable([shortRun, longRun]);

        var lines = rendered.Split('\n');
        Assert.Contains(lines, line => line.Contains("short", StringComparison.Ordinal) && line.Contains("p99 = max", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("long", StringComparison.Ordinal) && !line.Contains("p99 = max", StringComparison.Ordinal));
    }

    /// <summary>
    /// A figure too wide for its column keeps a space between itself and the one before it.
    /// </summary>
    /// <remarks>
    /// <b>Found by reading the rendered report rather than by reading the code.</b> The first
    /// version padded with <c>PadLeft</c>, which does nothing to a string already at the column
    /// width — so a series whose figures ran to thirteen digits printed as one unbroken number and
    /// took the table's alignment with it for every row after. The values that overflow are the
    /// ones most worth reading: a latency orders of magnitude out is either the finding or the bug.
    /// </remarks>
    [Fact]
    public void RenderTable_WithFiguresTooWideForTheirColumn_KeepsThemSeparated()
    {
        // Three years in nanoseconds, which is what a ts_recv read off the wrong field looks like.
        const long Absurd = 94_608_000_000_000_000L;

        var summary = LatencyStatistics.Summarize("absurd", [Absurd, Absurd + 1, Absurd + 2]);

        var row = LatencyStatistics.RenderTable([summary])
            .Split('\n')
            .Single(line => line.Contains("absurd", StringComparison.Ordinal));

        // Every figure is still a separate token: four of them, none run together.
        var figures = row.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Contains('.', StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(4, figures.Length);
    }

    /// <summary>
    /// Figures are rendered invariantly, so a machine with a comma decimal separator produces the
    /// same report as one without.
    /// </summary>
    [Fact]
    public void RenderTable_RendersFiguresInvariantly()
    {
        var summary = LatencyStatistics.Summarize("invariant", [1_234_500]);

        Assert.Contains("1234.5", LatencyStatistics.RenderTable([summary]), StringComparison.Ordinal);
    }
}
