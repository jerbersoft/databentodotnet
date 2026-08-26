using NodaTime;

namespace DatabentoDotNet.Dbn.Tests;

/// <summary>
/// Conformance tests for <see cref="DbnTime"/>, the one crossing between DBN's raw
/// <see cref="ulong"/> nanoseconds and NodaTime.
/// </summary>
/// <remarks>
/// <para>
/// Two things are being protected here, and neither would fail loudly on its own.
/// </para>
/// <para>
/// The first is <see cref="DbnConstants.UndefTimestamp"/>. It is <see cref="ulong.MaxValue"/>,
/// and the natural conversion — cast to <see cref="long"/>, hand it to
/// <c>Duration.FromNanoseconds</c> — wraps to -1 ns without throwing, producing an
/// <see cref="Instant"/> one nanosecond before the epoch. As a date it is no better: it
/// floor-divides to a perfectly ordinary-looking day in 2554. Both are answers a caller would
/// believe. <see cref="SentinelTrap_NaiveCastWrapsToOneNanosecondBeforeTheEpoch"/> pins the trap
/// itself so this file states what it is defending against, not just that the defence holds.
/// </para>
/// <para>
/// The second is precision. A DBN timestamp is nanoseconds and a BCL tick is 100 ns, so the whole
/// reason this codebase uses NodaTime is that the low two digits have to survive — see
/// <see cref="ToInstant_TimestampWithNonZeroSubTickDigits_RoundTripsExactly"/>.
/// </para>
/// </remarks>
public sealed class DbnTimeTests
{
    /// <summary>2020-12-28T13:00:00.000000001Z — the worked example in CLAUDE.md.</summary>
    private const ulong SubTickTimestamp = 1_609_160_400_000_000_001UL;

    // ------------------------------------------------------------------------------------
    // The sentinel
    // ------------------------------------------------------------------------------------

    [Fact]
    public void TryToInstant_UndefTimestamp_ReportsNoTimestampRatherThanAPreEpochInstant()
    {
        Assert.False(DbnTime.TryToInstant(DbnConstants.UndefTimestamp, out var instant));
        Assert.Equal(default, instant);
    }

    [Fact]
    public void TryToUtcDate_UndefTimestamp_ReportsNoTimestampRatherThanADayIn2554()
    {
        Assert.False(DbnTime.TryToUtcDate(DbnConstants.UndefTimestamp, out var date));
        Assert.Equal(default, date);
    }

    [Fact]
    public void ToInstant_UndefTimestamp_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => DbnTime.ToInstant(DbnConstants.UndefTimestamp));

    [Fact]
    public void ToUtcDate_UndefTimestamp_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => DbnTime.ToUtcDate(DbnConstants.UndefTimestamp));

    [Fact]
    public void IsUndefined_OnlyTheSentinel_IsUndefined()
    {
        Assert.True(DbnTime.IsUndefined(DbnConstants.UndefTimestamp));
        Assert.False(DbnTime.IsUndefined(DbnConstants.UndefTimestamp - 1));
        Assert.False(DbnTime.IsUndefined(0));
    }

    [Fact]
    public void SentinelTrap_NaiveCastWrapsToOneNanosecondBeforeTheEpoch()
    {
        // Not a test of DbnTime — a test of the mistake DbnTime exists to make impossible. If
        // this ever stops being true, the sentinel checks are guarding a hazard that moved.
        //
        // The sentinel goes through a local first, deliberately. Written against the constant,
        // `(long)DbnConstants.UndefTimestamp` is CS0221 and the compiler stops you; read off the
        // wire into a ulong, as every real timestamp is, the identical cast compiles and wraps in
        // silence. The dangerous spelling is the one that looks like ordinary code.
        var fromTheWire = ulong.MaxValue;
        var wrapped = NodaConstants.UnixEpoch + Duration.FromNanoseconds((long)fromTheWire);

        Assert.Equal(DbnConstants.UndefTimestamp, fromTheWire);

        Assert.Equal(Instant.FromUtc(1969, 12, 31, 23, 59, 59) + Duration.FromNanoseconds(999_999_999), wrapped);
        Assert.True(wrapped < NodaConstants.UnixEpoch);
    }

    // ------------------------------------------------------------------------------------
    // Precision: the reason NodaTime is here at all
    // ------------------------------------------------------------------------------------

    [Fact]
    public void ToInstant_TimestampWithNonZeroSubTickDigits_RoundTripsExactly()
    {
        var instant = DbnTime.ToInstant(SubTickTimestamp);

        // The low digit is what a 100 ns BCL tick would have discarded.
        Assert.Equal(Instant.FromUtc(2020, 12, 28, 13, 0, 0) + Duration.FromNanoseconds(1), instant);
        Assert.Equal(SubTickTimestamp, DbnTime.ToUnixNanoseconds(instant));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(999_999_999UL)]
    [InlineData(SubTickTimestamp)]
    [InlineData(ulong.MaxValue - 1)]
    public void ToUnixNanoseconds_EveryTimestampBelowTheSentinel_RoundTrips(ulong unixNanoseconds)
        => Assert.Equal(unixNanoseconds, DbnTime.ToUnixNanoseconds(DbnTime.ToInstant(unixNanoseconds)));

    [Fact]
    public void ToInstant_Zero_IsTheUnixEpoch()
        => Assert.Equal(NodaConstants.UnixEpoch, DbnTime.ToInstant(0));

    [Fact]
    public void ToInstant_LargestNonSentinelTimestamp_IsInTheYear2554()
    {
        // The conversion splits into whole days plus a nanosecond-of-day remainder rather than
        // going through a single long nanosecond count, so it clears the year-2262 ceiling that
        // long.MaxValue nanoseconds would impose. This is that claim, asserted.
        var instant = DbnTime.ToInstant(ulong.MaxValue - 1);

        Assert.Equal(
            Instant.FromUtc(2554, 7, 21, 23, 34, 33) + Duration.FromNanoseconds(709_551_614),
            instant);
    }

    // ------------------------------------------------------------------------------------
    // Instant -> ulong bounds
    // ------------------------------------------------------------------------------------

    [Fact]
    public void ToUnixNanoseconds_OneNanosecondBeforeTheEpoch_Throws()
    {
        var beforeEpoch = NodaConstants.UnixEpoch - Duration.FromNanoseconds(1);

        Assert.Throws<ArgumentOutOfRangeException>(() => DbnTime.ToUnixNanoseconds(beforeEpoch));
    }

    [Fact]
    public void ToUnixNanoseconds_TheInstantThatWouldEncodeAsTheSentinel_Throws()
    {
        // One nanosecond past the largest round-trippable timestamp. Encoding it would produce
        // ulong.MaxValue, which reads back out of the codec as "no timestamp" — so it is rejected
        // rather than silently turned into an absent one.
        var onTheSentinel = DbnTime.ToInstant(ulong.MaxValue - 1) + Duration.FromNanoseconds(1);

        Assert.Throws<ArgumentOutOfRangeException>(() => DbnTime.ToUnixNanoseconds(onTheSentinel));
    }

    // ------------------------------------------------------------------------------------
    // Dates: the UTC-midnight boundary the symbol maps key on
    // ------------------------------------------------------------------------------------

    [Fact]
    public void ToUtcDate_TimestampsEitherSideOfUtcMidnight_ResolveToDifferentDays()
    {
        // The whole hazard #14 is about: two timestamps 1 ns apart, two different symbol-map keys.
        var midnight = DbnTime.ToUnixNanosecondsAtMidnightUtc(new LocalDate(2023, 7, 4));

        Assert.Equal(new LocalDate(2023, 7, 3), DbnTime.ToUtcDate(midnight - 1));
        Assert.Equal(new LocalDate(2023, 7, 4), DbnTime.ToUtcDate(midnight));
    }

    [Fact]
    public void ToUtcDate_AnyTimestampWithinADay_ResolvesToThatDay()
    {
        var midnight = DbnTime.ToUnixNanosecondsAtMidnightUtc(new LocalDate(2023, 7, 4));
        var lastNanosecondOfTheDay = midnight + (ulong)NodaConstants.NanosecondsPerDay - 1;

        Assert.Equal(new LocalDate(2023, 7, 4), DbnTime.ToUtcDate(midnight));
        Assert.Equal(new LocalDate(2023, 7, 4), DbnTime.ToUtcDate(lastNanosecondOfTheDay));
    }

    [Fact]
    public void ToUnixNanosecondsAtMidnightUtc_TheEpochDate_IsZero()
        => Assert.Equal(0UL, DbnTime.ToUnixNanosecondsAtMidnightUtc(new LocalDate(1970, 1, 1)));

    [Fact]
    public void ToUnixNanosecondsAtMidnightUtc_RoundTripsThroughToUtcDate()
    {
        var date = new LocalDate(2023, 7, 4);

        Assert.Equal(date, DbnTime.ToUtcDate(DbnTime.ToUnixNanosecondsAtMidnightUtc(date)));
    }

    [Fact]
    public void ToUnixNanosecondsAtMidnightUtc_DateBeforeTheEpoch_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => DbnTime.ToUnixNanosecondsAtMidnightUtc(new LocalDate(1969, 12, 31)));

    [Fact]
    public void ToUnixNanosecondsAtMidnightUtc_DateBeyondTheUlongRange_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => DbnTime.ToUnixNanosecondsAtMidnightUtc(new LocalDate(2554, 7, 22)));
}
