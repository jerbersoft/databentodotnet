using NodaTime;

namespace DatabentoDotNet.Reference.Tests;

/// <summary>
/// Drives <see cref="ReferenceProbe"/> — the decision procedures behind the three answers #57 owes
/// #49, #52 and #53 — with no key, no socket and no subscription.
/// </summary>
/// <remarks>
/// <para>
/// <b>This file is the run that catches the bugs, and the entitled run is only for the facts.</b>
/// <c>RealReferenceRequestTests</c> needs a reference-data subscription this account does not hold,
/// so its three experiments had never executed once — and one of them could not have passed if it
/// had. See <see cref="CheckOrdering_OnTheOrderedPath_StillRendersItsMessage"/>, which is that
/// defect written down as a test.
/// </para>
/// <para>
/// The mock cannot say what the server's <c>end</c> means, what order it sends rows in, or what a
/// real dividend looks like. It can say that the code deciding those things gives the right verdict
/// for inputs whose answer is known before the test runs, which is every part of the experiment
/// except the fact itself.
/// </para>
/// </remarks>
public class ReferenceProbeTests
{
    // ------------------------------------------------------------------------------------------
    // CheckOrdering — the answer #52 is waiting for.
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// The regression this file exists for: the ordered path renders its message rather than
    /// throwing.
    /// </summary>
    /// <remarks>
    /// <b>The defect, exactly.</b> The experiment built its failure message by indexing the key
    /// list at the first descent's index, and C# evaluates an assertion's message argument before
    /// it evaluates the assertion — so on the ordered path it read <c>keys[-1]</c> and threw
    /// <see cref="ArgumentOutOfRangeException"/>. A correctly sorted response, which is the outcome
    /// the test exists to confirm and the one #52's decision needs, was the single outcome it could
    /// not report. Formatting the message is therefore the whole test.
    /// </remarks>
    [Fact]
    public void CheckOrdering_OnTheOrderedPath_StillRendersItsMessage()
    {
        var verdict = ReferenceProbe.CheckOrdering<int>([1, 2, 3]);

        var message = $"The server does NOT sort. {verdict.Descent}, "
            + $"over {verdict.ComparedCount} comparable row(s).";

        Assert.True(verdict.IsOrdered);
        Assert.Equal(-1, verdict.FirstDescentIndex);
        Assert.Contains("over 3 comparable row(s)", message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckOrdering_WithNothingToCompare_IsOrdered()
    {
        var verdict = ReferenceProbe.CheckOrdering(Array.Empty<int>());

        Assert.True(verdict.IsOrdered);
        Assert.Equal(0, verdict.ComparedCount);
        Assert.Equal(string.Empty, verdict.Descent);
    }

    [Fact]
    public void CheckOrdering_WithOneKey_IsOrdered()
    {
        Assert.True(ReferenceProbe.CheckOrdering<int>([7]).IsOrdered);
    }

    [Fact]
    public void CheckOrdering_WithEqualKeys_IsOrdered()
    {
        // The server sorting by a column with ties has not failed to sort. Ascending here means
        // non-descending, or every response with two events on one date would read as unsorted.
        Assert.True(ReferenceProbe.CheckOrdering<int>([3, 3, 3]).IsOrdered);
    }

    [Fact]
    public void CheckOrdering_WithADescent_ReportsWhereAndWhat()
    {
        var verdict = ReferenceProbe.CheckOrdering<int>([1, 5, 2]);

        Assert.False(verdict.IsOrdered);
        Assert.Equal(2, verdict.FirstDescentIndex);
        Assert.Equal(3, verdict.ComparedCount);
        Assert.Equal("Row 2 carries 2, which is before row 1's 5", verdict.Descent);
    }

    [Fact]
    public void CheckOrdering_WithSeveralDescents_ReportsTheFirst()
    {
        // "Where did it first stop being sorted" is the diagnostic; the later breaks are noise.
        var verdict = ReferenceProbe.CheckOrdering<int>([4, 1, 9, 2]);

        Assert.Equal(1, verdict.FirstDescentIndex);
    }

    [Fact]
    public void CheckOrdering_OverEventDates_ReadsTheRealIndexType()
    {
        // CorporateActionIndex.EventDate is a LocalDate?, dropped to LocalDate by the caller.
        var verdict = ReferenceProbe.CheckOrdering(
        [
            new LocalDate(2024, 1, 2),
            new LocalDate(2024, 3, 9),
            new LocalDate(2024, 2, 1),
        ]);

        Assert.False(verdict.IsOrdered);
        Assert.Equal(2, verdict.FirstDescentIndex);
    }

    [Fact]
    public void CheckOrdering_OverRecordTimestamps_ReadsTheOtherIndexType()
    {
        // CorporateActionIndex.TsRecord is an Instant, and is the control in the experiment: a
        // response sorted under both indexes is being sorted rather than returned in storage order.
        var verdict = ReferenceProbe.CheckOrdering(
        [
            Instant.FromUtc(2024, 1, 2, 3, 4),
            Instant.FromUtc(2024, 1, 2, 3, 5),
        ]);

        Assert.True(verdict.IsOrdered);
    }

    [Fact]
    public void CheckOrdering_WithNoKeys_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ReferenceProbe.CheckOrdering<int>(null!));
    }

    // ------------------------------------------------------------------------------------------
    // CheckBoundary — the answer #49 is waiting for.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void CheckBoundary_WhenTheBoundaryRowsAllLeave_ReadsExclusive()
    {
        var verdict = ReferenceProbe.CheckBoundary(
            windowCount: 10,
            onTheBoundary: 3,
            narrowedCount: 7,
            survivors: 0);

        Assert.Equal(BoundaryReading.Exclusive, verdict.Reading);
        Assert.Equal(7, verdict.ExpectedNarrowedCount);
    }

    [Fact]
    public void CheckBoundary_WhenABoundaryRowSurvives_ReadsInclusive()
    {
        // This is the #45 shape: upstream documents the end exclusive and the server disagrees.
        var verdict = ReferenceProbe.CheckBoundary(
            windowCount: 10,
            onTheBoundary: 3,
            narrowedCount: 10,
            survivors: 3);

        Assert.Equal(BoundaryReading.Inclusive, verdict.Reading);
    }

    /// <summary>
    /// An inclusive <c>end</c> reads inclusive, not confounded — even though it also fails the
    /// row-count check.
    /// </summary>
    /// <remarks>
    /// <b>The trap this enum exists to close.</b> An inclusive end returns the boundary rows, so
    /// <see cref="BoundaryVerdict.IsConsistent"/> is false for it too. A test that asserted the row
    /// count before exclusivity would report #49's actual answer as "the two queries differ by more
    /// than their end" and send the reader looking for a flaky window instead of a documentation
    /// bug.
    /// </remarks>
    [Fact]
    public void CheckBoundary_WhenTheEndIsInclusive_IsNotReportedAsConfounded()
    {
        var verdict = ReferenceProbe.CheckBoundary(
            windowCount: 10,
            onTheBoundary: 3,
            narrowedCount: 10,
            survivors: 3);

        Assert.False(verdict.IsConsistent);
        Assert.Equal(BoundaryReading.Inclusive, verdict.Reading);
    }

    [Fact]
    public void CheckBoundary_WhenTheNarrowingRemovedMoreThanTheBoundary_IsConfounded()
    {
        // Exclusivity is not established by rows merely going missing. If the narrowed query lost
        // rows that were not on the boundary, the two queries differ by more than their end and
        // neither answer is supported — reporting "exclusive" here would put a wrong answer on #49.
        var verdict = ReferenceProbe.CheckBoundary(
            windowCount: 10,
            onTheBoundary: 3,
            narrowedCount: 4,
            survivors: 0);

        Assert.Equal(BoundaryReading.Confounded, verdict.Reading);
    }

    [Fact]
    public void CheckBoundary_WhenNothingSatOnTheBoundary_IsConfoundedRatherThanExclusive()
    {
        // Nothing was narrowed away, so nothing was demonstrated. Reading this as "exclusive" would
        // let an empty window answer #49.
        var verdict = ReferenceProbe.CheckBoundary(0, 0, 0, 0);

        Assert.True(verdict.IsConsistent);
        Assert.Equal(BoundaryReading.Confounded, verdict.Reading);
    }

    [Theory]
    [InlineData(-1, 0, 0, 0)]
    [InlineData(0, -1, 0, 0)]
    [InlineData(0, 0, -1, 0)]
    [InlineData(0, 0, 0, -1)]
    [InlineData(2, 3, 0, 0)]
    public void CheckBoundary_WithCountsThatCannotHaveBeenMeasured_Throws(
        int windowCount, int onTheBoundary, int narrowedCount, int survivors)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ReferenceProbe.CheckBoundary(windowCount, onTheBoundary, narrowedCount, survivors));
    }

    // ------------------------------------------------------------------------------------------
    // CheckMagnitudes — the answer #53 is waiting for.
    // ------------------------------------------------------------------------------------------

    private const decimal Ceiling = 1_000_000_000_000m;
    private const decimal Floor = 0.000_000_000_001m;

    private static RateObservation Rate(string field, decimal value) =>
        new() { Field = field, Value = value };

    [Fact]
    public void CheckMagnitudes_OverOrdinaryRates_IsWithinTheBand()
    {
        var verdict = ReferenceProbe.CheckMagnitudes(
            [
                Rate("adjustment_factors factor", 0.9231m),
                Rate("adjustment_factors factor", 1.0m),
                Rate("corporate_actions rate_info['gross_dividend']", 0.24m),
            ],
            Floor,
            Ceiling);

        Assert.True(verdict.IsWithinBand);
        Assert.Equal(3, verdict.ObservedCount);
    }

    [Fact]
    public void CheckMagnitudes_ReportsEachFieldsSpan()
    {
        // The per-field span is what #53 asked for. "Nothing threw" is not an answer anybody can
        // write into the issue; "factor ran 0.9231 to 1.0 over three rows" is.
        var verdict = ReferenceProbe.CheckMagnitudes(
            [
                Rate("factor", 0.9231m),
                Rate("factor", 1.5m),
                Rate("factor", 1.0m),
                Rate("close", 42.75m),
            ],
            Floor,
            Ceiling);

        var factor = Assert.Single(verdict.Fields, f => f.Field == "factor");
        Assert.Equal(3, factor.Count);
        Assert.Equal(0.9231m, factor.Minimum);
        Assert.Equal(1.5m, factor.Maximum);

        // Ordered by name, so the report reads the same way twice.
        Assert.Equal(["close", "factor"], verdict.Fields.Select(f => f.Field));
    }

    [Fact]
    public void CheckMagnitudes_WithANegativeRate_ReportsItSignedRatherThanAsAMagnitude()
    {
        var verdict = ReferenceProbe.CheckMagnitudes([Rate("factor", -2.5m)], Floor, Ceiling);

        var factor = Assert.Single(verdict.Fields);
        Assert.Equal(-2.5m, factor.Minimum);
        Assert.True(verdict.IsWithinBand);
    }

    [Fact]
    public void CheckMagnitudes_AboveTheCeiling_IsExtreme()
    {
        var verdict = ReferenceProbe.CheckMagnitudes(
            [Rate("factor", Ceiling * 10m)],
            Floor,
            Ceiling);

        Assert.False(verdict.IsWithinBand);
        Assert.Equal("factor = 10000000000000", Assert.Single(verdict.Extreme));
    }

    [Fact]
    public void CheckMagnitudes_BelowTheFloorAndNotZero_IsExtreme()
    {
        var verdict = ReferenceProbe.CheckMagnitudes(
            [Rate("factor", Floor / 10m)],
            Floor,
            Ceiling);

        Assert.False(verdict.IsWithinBand);
    }

    [Fact]
    public void CheckMagnitudes_WithZero_IsWithinTheBand()
    {
        // Zero is an ordinary value for a rate, not an underflow, and it is below every floor.
        Assert.True(ReferenceProbe.CheckMagnitudes([Rate("factor", 0m)], Floor, Ceiling).IsWithinBand);
    }

    [Fact]
    public void CheckMagnitudes_WithTheSameExtremeTwice_ReportsItOnce()
    {
        var huge = Ceiling * 2m;
        var verdict = ReferenceProbe.CheckMagnitudes(
            [Rate("factor", huge), Rate("factor", huge)],
            Floor,
            Ceiling);

        Assert.Single(verdict.Extreme);
        Assert.Equal(2, verdict.ObservedCount);
    }

    [Fact]
    public void CheckMagnitudes_WithNothingObserved_RendersASentenceRatherThanAnEmptyTable()
    {
        var verdict = ReferenceProbe.CheckMagnitudes([], Floor, Ceiling);

        Assert.True(verdict.IsWithinBand);
        Assert.Equal(0, verdict.ObservedCount);
        Assert.Equal("No rate was observed, so there is nothing to report.", verdict.Render());
    }

    [Fact]
    public void CheckMagnitudes_Render_CarriesTheCountsAndTheSpans()
    {
        var report = ReferenceProbe.CheckMagnitudes(
            [Rate("factor", 0.5m), Rate("factor", 1.5m)],
            Floor,
            Ceiling).Render();

        Assert.Contains("2 rate(s) observed", report, StringComparison.Ordinal);
        Assert.Contains("n=2", report, StringComparison.Ordinal);
        Assert.Contains("min=0.5", report, StringComparison.Ordinal);
        Assert.Contains("max=1.5", report, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckMagnitudes_WithNoObservations_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ReferenceProbe.CheckMagnitudes(null!, Floor, Ceiling));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 1)]
    public void CheckMagnitudes_WithABandThatIsNotOne_Throws(int floor, int ceiling)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ReferenceProbe.CheckMagnitudes([], floor, ceiling));
    }
}
