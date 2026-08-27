using DatabentoDotNet.Historical;
using NodaTime;

namespace DatabentoDotNet.Dbn.Tests;

/// <summary>
/// Conformance tests for <see cref="DateRange"/> and <see cref="DateTimeRange"/>, the two
/// half-open UTC intervals every historical endpoint takes, against both wire renderings:
/// <c>yyyy-MM-dd</c> date strings and Unix-nanosecond integers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Temporary home.</b> These types belong to <c>DatabentoDotNet.Historical</c>
/// (see <c>src/DatabentoDotNet.Historical</c>), and this file belongs in
/// <c>tests/DatabentoDotNet.Historical.Tests</c>. That project does not exist yet — issue #34 is
/// creating it, by that exact name, in a parallel worktree — so this file lives here to avoid
/// colliding with it. Move it once #34 lands.
/// </para>
/// <para>
/// The off-by-one-day mistakes this codec-adjacent conversion invites are invisible from the
/// outside: a query that silently covers one day too many or too few still returns data, just
/// the wrong amount of it — and Databento's historical API bills for what it returns. Every row
/// in the brief's table is asserted here, plus the empty/inverted-range decision recorded in
/// task-2-report.md's ROADMAP decision record.
/// </para>
/// </remarks>
public sealed class DateRangeTests
{
    // ------------------------------------------------------------------------------------
    // Table-driven: the brief's table, verbatim. Every row is asserted against both wire
    // renderings — the DateRange's own yyyy-MM-dd strings, and the Unix-nanosecond values its
    // widened DateTimeRange renders.
    // ------------------------------------------------------------------------------------

    private static readonly (
        string Description,
        DateRange Range,
        string ExpectedStartDate,
        string ExpectedEndDate,
        long? ExpectedStartNanos,
        long? ExpectedEndNanos)[] DateRangeRows =
    [
        (
            "DateRange.OnDay(2024-03-15)",
            DateRange.OnDay(new LocalDate(2024, 3, 15)),
            "2024-03-15",
            "2024-03-16",
            1_710_460_800_000_000_000,
            1_710_547_200_000_000_000
        ),
        (
            "DateRange.Including(2024-03-15, 2024-03-16)",
            DateRange.Including(new LocalDate(2024, 3, 15), new LocalDate(2024, 3, 16)),
            "2024-03-15",
            "2024-03-17",
            null,
            null
        ),
    ];

    [Fact]
    public void DateRange_WireRendering_MatchesTheBriefsTable()
    {
        foreach (var row in DateRangeRows)
        {
            Assert.True(
                row.ExpectedStartDate == row.Range.StartDate,
                $"{row.Description}: expected start_date '{row.ExpectedStartDate}', got '{row.Range.StartDate}'.");
            Assert.True(
                row.ExpectedEndDate == row.Range.EndDate,
                $"{row.Description}: expected end_date '{row.ExpectedEndDate}', got '{row.Range.EndDate}'.");

            if (row.ExpectedStartNanos is { } expectedStart && row.ExpectedEndNanos is { } expectedEnd)
            {
                var widened = row.Range.ToDateTimeRange();
                Assert.True(
                    expectedStart == widened.StartUnixNanoseconds,
                    $"{row.Description}: expected start {expectedStart} ns, got {widened.StartUnixNanoseconds} ns.");
                Assert.True(
                    expectedEnd == widened.EndUnixNanoseconds,
                    $"{row.Description}: expected end {expectedEnd} ns, got {widened.EndUnixNanoseconds} ns.");
            }
        }
    }

    private static readonly (
        string Description,
        DateTimeRange Range,
        long ExpectedStartNanos,
        long ExpectedEndNanos,
        string ExpectedStartDate,
        string ExpectedEndDate)[] DateTimeRangeRows =
    [
        (
            "DateTimeRange.From(2024-03-15T09:30:00Z, 6h30m)",
            DateTimeRange.From(Instant.FromUtc(2024, 3, 15, 9, 30), Duration.FromHours(6) + Duration.FromMinutes(30)),
            1_710_495_000_000_000_000,
            1_710_518_400_000_000_000,
            "2024-03-15",
            "2024-03-16"
        ),
    ];

    [Fact]
    public void DateTimeRange_WireRendering_MatchesTheBriefsTable()
    {
        foreach (var row in DateTimeRangeRows)
        {
            Assert.True(
                row.ExpectedStartNanos == row.Range.StartUnixNanoseconds,
                $"{row.Description}: expected start {row.ExpectedStartNanos} ns, got {row.Range.StartUnixNanoseconds} ns.");
            Assert.True(
                row.ExpectedEndNanos == row.Range.EndUnixNanoseconds,
                $"{row.Description}: expected end {row.ExpectedEndNanos} ns, got {row.Range.EndUnixNanoseconds} ns.");

            var narrowed = row.Range.ToDateRange();
            Assert.True(
                row.ExpectedStartDate == narrowed.StartDate,
                $"{row.Description} -> DateRange: expected start_date '{row.ExpectedStartDate}', got '{narrowed.StartDate}'.");
            Assert.True(
                row.ExpectedEndDate == narrowed.EndDate,
                $"{row.Description} -> DateRange: expected end_date '{row.ExpectedEndDate}', got '{narrowed.EndDate}'.");
        }
    }

    // ------------------------------------------------------------------------------------
    // The round-up rule, on its own: a DateTimeRange whose end is not midnight rounds its
    // DateRange end UP to the next day, never down. Row 4 of the brief's table.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void ToDateRange_EndNotMidnight_RoundsEndUpToNextDay()
    {
        // 2024-03-15T16:00:00Z: same day as the start, but not midnight.
        var range = DateTimeRange.Between(
            Instant.FromUtc(2024, 3, 15, 9, 30),
            Instant.FromUtc(2024, 3, 15, 16, 0));

        var dateRange = range.ToDateRange();

        Assert.Equal(new LocalDate(2024, 3, 15), dateRange.Start);
        Assert.Equal(new LocalDate(2024, 3, 16), dateRange.End);
        Assert.Equal("2024-03-16", dateRange.EndDate);
    }

    [Fact]
    public void ToDateRange_EndExactlyMidnight_DoesNotRoundUp()
    {
        // Contrast case: an end that already falls exactly on a UTC midnight must not gain an
        // extra day. A round-up rule that ignores time-of-day entirely would fail only this test.
        var range = DateTimeRange.Between(
            Instant.FromUtc(2024, 3, 15, 0, 0),
            Instant.FromUtc(2024, 3, 20, 0, 0));

        var dateRange = range.ToDateRange();

        Assert.Equal(new LocalDate(2024, 3, 15), dateRange.Start);
        Assert.Equal(new LocalDate(2024, 3, 20), dateRange.End);
    }

    [Fact]
    public void RoundTrip_DateRangeToDateTimeRangeAndBack_IsExactWhenBothEndsAreMidnight()
    {
        // Mirrors upstream's own `range_equivalency` test (historical.rs): widening to
        // DateTimeRange and narrowing back is lossless exactly when nothing needs rounding,
        // because a DateRange's bounds are always midnight once turned into instants.
        var original = DateRange.Between(new LocalDate(2025, 3, 27), new LocalDate(2025, 4, 10));

        var roundTripped = original.ToDateTimeRange().ToDateRange();

        Assert.Equal(original, roundTripped);
    }

    // ------------------------------------------------------------------------------------
    // The row only Instant can pass: two Unix-nanosecond integers one apart render back
    // exactly. Through DateTimeOffset (100 ns resolution) they would collapse to the same
    // value — see CLAUDE.md, "Dates and times", and DbnTimeTests for the codec-side version of
    // this same guarantee.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void FromUnixNanoseconds_OneNanosecondApart_RoundTripsExactlyThroughInstant()
    {
        const long startNanos = 1_710_495_000_000_000_000;
        const long endNanos = 1_710_495_000_000_000_001;

        var range = DateTimeRange.FromUnixNanoseconds(startNanos, endNanos);

        Assert.Equal(startNanos, range.StartUnixNanoseconds);
        Assert.Equal(endNanos, range.EndUnixNanoseconds);
        Assert.Equal(1, range.EndUnixNanoseconds - range.StartUnixNanoseconds);
        Assert.NotEqual(range.Start, range.End);
    }

    // ------------------------------------------------------------------------------------
    // The remaining named factories: Between, Including, From(start, Duration), and the
    // DateRange <-> DateTimeRange conversions, each exercised once directly rather than only
    // through the table above.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void DateRange_Between_IsHalfOpen()
    {
        var range = DateRange.Between(new LocalDate(2024, 1, 1), new LocalDate(2024, 1, 5));

        Assert.Equal(new LocalDate(2024, 1, 1), range.Start);
        Assert.Equal(new LocalDate(2024, 1, 5), range.End);
    }

    [Fact]
    public void DateRange_From_StartPlusDuration_CountsWholeDaysOnly()
    {
        var range = DateRange.From(new LocalDate(2024, 3, 15), Duration.FromDays(5));

        Assert.Equal(new LocalDate(2024, 3, 15), range.Start);
        Assert.Equal(new LocalDate(2024, 3, 20), range.End);
    }

    [Fact]
    public void DateTimeRange_OnDay_IsMidnightToMidnight()
    {
        var range = DateTimeRange.OnDay(new LocalDate(2024, 3, 15));

        Assert.Equal(Instant.FromUtc(2024, 3, 15, 0, 0), range.Start);
        Assert.Equal(Instant.FromUtc(2024, 3, 16, 0, 0), range.End);
    }

    [Fact]
    public void DateTimeRange_Including_AddsOneNanosecondToTheEnd()
    {
        var lastInstant = Instant.FromUtc(2024, 3, 15, 16, 0);

        var range = DateTimeRange.Including(Instant.FromUtc(2024, 3, 15, 9, 30), lastInstant);

        Assert.Equal(lastInstant + Duration.FromNanoseconds(1), range.End);
    }

    [Fact]
    public void DateRange_ToDateTimeRange_WidensToUtcMidnightOnEachEnd()
    {
        var range = DateRange.OnDay(new LocalDate(2024, 3, 15)).ToDateTimeRange();

        Assert.Equal(Instant.FromUtc(2024, 3, 15, 0, 0), range.Start);
        Assert.Equal(Instant.FromUtc(2024, 3, 16, 0, 0), range.End);
    }

    // ------------------------------------------------------------------------------------
    // The empty/inverted-range decision: rejected at construction, for every factory alike.
    // See task-2-report.md's ROADMAP decision record for the reasoning.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void DateRange_Between_EmptyRange_Throws()
    {
        var date = new LocalDate(2024, 3, 15);

        Assert.Throws<ArgumentException>(() => DateRange.Between(date, date));
    }

    [Fact]
    public void DateRange_Between_InvertedRange_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => DateRange.Between(new LocalDate(2024, 3, 16), new LocalDate(2024, 3, 15)));
    }

    [Fact]
    public void DateRange_Including_LastDayBeforeStart_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => DateRange.Including(new LocalDate(2024, 3, 16), new LocalDate(2024, 3, 15)));
    }

    [Fact]
    public void DateRange_From_SubDayDuration_Throws()
    {
        // Upstream's own test for this construction (date_range_from_lt_day_duration) passes
        // this exact pair and asserts the resulting range is empty (start == end). This port
        // deliberately does not carry that outcome forward: see DateRange.From's remarks.
        Assert.Throws<ArgumentException>(
            () => DateRange.From(new LocalDate(2024, 2, 16), Duration.FromSeconds(1)));
    }

    [Fact]
    public void DateTimeRange_Between_EmptyRange_Throws()
    {
        var instant = Instant.FromUtc(2024, 3, 15, 9, 30);

        Assert.Throws<ArgumentException>(() => DateTimeRange.Between(instant, instant));
    }

    [Fact]
    public void DateTimeRange_Between_InvertedRange_Throws()
    {
        Assert.Throws<ArgumentException>(() => DateTimeRange.Between(
            Instant.FromUtc(2024, 3, 15, 16, 0),
            Instant.FromUtc(2024, 3, 15, 9, 30)));
    }

    [Fact]
    public void DateTimeRange_From_ZeroDuration_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => DateTimeRange.From(Instant.FromUtc(2024, 3, 15, 9, 30), Duration.Zero));
    }

    [Fact]
    public void DateTimeRange_From_NegativeDuration_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => DateTimeRange.From(Instant.FromUtc(2024, 3, 15, 9, 30), Duration.FromHours(-1)));
    }

    [Fact]
    public void DateTimeRange_FromUnixNanoseconds_EqualValues_Throws()
    {
        Assert.Throws<ArgumentException>(() => DateTimeRange.FromUnixNanoseconds(1_710_495_000_000_000_000, 1_710_495_000_000_000_000));
    }
}
