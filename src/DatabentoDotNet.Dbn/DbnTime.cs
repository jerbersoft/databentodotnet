using NodaTime;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// Converts between DBN's on-the-wire <see cref="ulong"/> nanosecond timestamps and NodaTime's
/// <see cref="Instant"/> and <see cref="LocalDate"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the boundary.</b> Record struct fields and the codec keep raw <see cref="ulong"/>
/// nanoseconds, because a record is reinterpreted in place over the read buffer and a field's
/// type <em>is</em> its wire layout — <see cref="Instant"/> is 16 bytes and <see cref="LocalDate"/>
/// is 4, where the wire has an 8-byte <c>u64</c>. Everything above the codec uses NodaTime.
/// This type is the only crossing, and it is deliberately explicit: there is no implicit
/// conversion to fall into.
/// </para>
/// <para>
/// <b>Every conversion checks <see cref="DbnConstants.UndefTimestamp"/> first, and that check is
/// the whole reason this type exists.</b> The sentinel is <see cref="ulong.MaxValue"/>, and the
/// obvious conversion wraps it silently:
/// </para>
/// <code>
/// Duration.FromNanoseconds((long)DbnConstants.UndefTimestamp)   // -1 ns. No exception.
/// </code>
/// <para>
/// That resolves to an <see cref="Instant"/> one nanosecond <em>before</em> the UNIX epoch —
/// 1969-12-31T23:59:59.999999999Z, a confidently wrong answer that nothing downstream would
/// question. The sentinel does no better as a date: it floor-divides to a perfectly plausible
/// day in 2554. So <see cref="TryToInstant"/> and <see cref="TryToUtcDate"/> report "no
/// timestamp" by returning <see langword="false"/>, and <see cref="ToInstant"/> and
/// <see cref="ToUtcDate"/> throw rather than answer.
/// </para>
/// <para>
/// <b>Apart from the sentinel, every <see cref="ulong"/> converts exactly.</b> The conversion
/// splits into whole days plus a nanosecond-of-day remainder rather than going through a single
/// <see cref="long"/> nanosecond count, so it never touches the year-2262 ceiling that
/// <c>long.MaxValue</c> nanoseconds imposes. <see cref="ulong.MaxValue"/> minus one nanosecond
/// is in 2554, well inside <see cref="Instant"/>'s range.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// using DatabentoDotNet.Dbn;
/// using NodaTime;
///
/// // ts_recv for 2020-12-28T13:00:00.000000001Z. The trailing 1 is a nanosecond, which is what a
/// // DateTime tick — 100 of them — cannot represent and an Instant can.
/// ulong raw = 1609160400000000001;
///
/// Instant when = DbnTime.ToInstant(raw);              // 2020-12-28T13:00:00.000000001Z
/// LocalDate day = DbnTime.ToUtcDate(raw);             // 2020-12-28, the date a symbol map keys on
/// ulong roundTripped = DbnTime.ToUnixNanoseconds(when);   // exactly `raw` again
///
/// // The sentinel is not a time, and every conversion here says so rather than answering.
/// if (!DbnTime.TryToInstant(DbnConstants.UndefTimestamp, out Instant absent))
/// {
///     Console.WriteLine("no timestamp");
/// }
/// </code>
/// <para>
/// Use the <c>Try</c> pair wherever an absent timestamp is an ordinary outcome — most optional record
/// fields — and <see cref="ToInstant"/> or <see cref="ToUtcDate"/> where it is not, so a sentinel
/// throws instead of becoming a plausible wrong answer:
/// </para>
/// <code>
/// if (record.TryGet(out ImbalanceMsg imbalance)
///     &amp;&amp; DbnTime.TryToInstant(imbalance.AuctionTime, out Instant auction))
/// {
///     Console.WriteLine($"auction at {auction}");
/// }
/// </code>
/// </example>
public static class DbnTime
{
    /// <summary>Nanoseconds in one 24-hour day. No DBN timestamp is zone- or leap-second-aware.</summary>
    private const ulong NanosecondsPerDay = (ulong)NodaConstants.NanosecondsPerDay;

    /// <summary>The largest whole day count a <see cref="ulong"/> nanosecond timestamp can carry.</summary>
    private const ulong MaxDays = ulong.MaxValue / NanosecondsPerDay;

    /// <summary>
    /// The nanosecond-of-day at which <see cref="MaxDays"/> reaches
    /// <see cref="DbnConstants.UndefTimestamp"/>. Bounds checks use <c>&gt;=</c> against this, so
    /// one comparison rejects both arithmetic overflow and a value that would land exactly on the
    /// sentinel and come back out as "no timestamp".
    /// </summary>
    private const ulong UndefNanosecondOfDayOnMaxDay = ulong.MaxValue % NanosecondsPerDay;

    private static readonly LocalDate UnixEpochDate = new(1970, 1, 1);

    /// <summary>
    /// Reports whether a raw timestamp is DBN's "no timestamp" sentinel,
    /// <see cref="DbnConstants.UndefTimestamp"/>.
    /// </summary>
    /// <param name="unixNanoseconds">The raw timestamp to test.</param>
    /// <returns><see langword="true"/> when the timestamp is undefined.</returns>
    public static bool IsUndefined(ulong unixNanoseconds) => unixNanoseconds == DbnConstants.UndefTimestamp;

    /// <summary>
    /// Converts a raw DBN timestamp to an <see cref="Instant"/>, unless it is the undefined
    /// sentinel.
    /// </summary>
    /// <param name="unixNanoseconds">Nanoseconds since the UNIX epoch, as read from the wire.</param>
    /// <param name="instant">
    /// Receives the converted instant, or <see langword="default"/> when
    /// <paramref name="unixNanoseconds"/> is <see cref="DbnConstants.UndefTimestamp"/>.
    /// </param>
    /// <returns>
    /// <see langword="false"/> when the timestamp is undefined — the only value that does not
    /// convert. Every other <see cref="ulong"/> converts exactly.
    /// </returns>
    public static bool TryToInstant(ulong unixNanoseconds, out Instant instant)
    {
        if (IsUndefined(unixNanoseconds))
        {
            instant = default;
            return false;
        }

        var days = (int)(unixNanoseconds / NanosecondsPerDay);
        var nanosecondOfDay = (long)(unixNanoseconds % NanosecondsPerDay);
        instant = NodaConstants.UnixEpoch + Duration.FromDays(days) + Duration.FromNanoseconds(nanosecondOfDay);
        return true;
    }

    /// <summary>Converts a raw DBN timestamp to an <see cref="Instant"/>.</summary>
    /// <param name="unixNanoseconds">Nanoseconds since the UNIX epoch, as read from the wire.</param>
    /// <returns>The converted instant.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="unixNanoseconds"/> is <see cref="DbnConstants.UndefTimestamp"/>, which is
    /// not a time. Use <see cref="TryToInstant"/> where an absent timestamp is expected.
    /// </exception>
    public static Instant ToInstant(ulong unixNanoseconds)
    {
        if (!TryToInstant(unixNanoseconds, out var instant))
        {
            throw new ArgumentOutOfRangeException(
                nameof(unixNanoseconds),
                "This is DBN's undefined-timestamp sentinel, not a time. Use TryToInstant where an absent timestamp is expected.");
        }

        return instant;
    }

    /// <summary>
    /// Converts an <see cref="Instant"/> back to the raw <see cref="ulong"/> nanoseconds DBN puts
    /// on the wire.
    /// </summary>
    /// <param name="instant">The instant to convert.</param>
    /// <returns>Nanoseconds since the UNIX epoch.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="instant"/> is before the UNIX epoch, or so far after it that it does not
    /// fit in a <see cref="ulong"/> — or that it would land exactly on
    /// <see cref="DbnConstants.UndefTimestamp"/> and read back as "no timestamp".
    /// </exception>
    public static ulong ToUnixNanoseconds(Instant instant)
    {
        // The comparison is against the epoch itself, not against the elapsed Duration's Days.
        // Duration.Days truncates toward zero, so a duration of -1 ns reports 0 days and would
        // slip past a `Days < 0` check into the unsigned arithmetic below.
        if (instant < NodaConstants.UnixEpoch)
        {
            throw new ArgumentOutOfRangeException(
                nameof(instant),
                "DBN timestamps are unsigned nanoseconds since the UNIX epoch; this instant is before it.");
        }

        var elapsed = instant - NodaConstants.UnixEpoch;
        var days = (ulong)elapsed.Days;
        var nanosecondOfDay = (ulong)elapsed.NanosecondOfDay;
        if (days > MaxDays || (days == MaxDays && nanosecondOfDay >= UndefNanosecondOfDayOnMaxDay))
        {
            throw new ArgumentOutOfRangeException(
                nameof(instant),
                "This instant is too far in the future to be a DBN timestamp: it does not fit in an unsigned 64-bit nanosecond count below the undefined-timestamp sentinel.");
        }

        return (days * NanosecondsPerDay) + nanosecondOfDay;
    }

    /// <summary>
    /// Converts a raw DBN timestamp to the UTC calendar date it falls on, unless it is the
    /// undefined sentinel.
    /// </summary>
    /// <remarks>
    /// This is the date the symbol maps key on — see <see cref="TsSymbolMap.TryGetSymbol(LocalDate, uint, out string?)"/>.
    /// </remarks>
    /// <param name="unixNanoseconds">Nanoseconds since the UNIX epoch, as read from the wire.</param>
    /// <param name="date">
    /// Receives the UTC date, or <see langword="default"/> when <paramref name="unixNanoseconds"/>
    /// is <see cref="DbnConstants.UndefTimestamp"/>.
    /// </param>
    /// <returns><see langword="false"/> when the timestamp is undefined.</returns>
    public static bool TryToUtcDate(ulong unixNanoseconds, out LocalDate date)
    {
        if (IsUndefined(unixNanoseconds))
        {
            date = default;
            return false;
        }

        date = UnixEpochDate.PlusDays((int)(unixNanoseconds / NanosecondsPerDay));
        return true;
    }

    /// <summary>Converts a raw DBN timestamp to the UTC calendar date it falls on.</summary>
    /// <param name="unixNanoseconds">Nanoseconds since the UNIX epoch, as read from the wire.</param>
    /// <returns>The UTC date.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="unixNanoseconds"/> is <see cref="DbnConstants.UndefTimestamp"/>. It would
    /// otherwise floor-divide to an entirely plausible day in 2554.
    /// </exception>
    public static LocalDate ToUtcDate(ulong unixNanoseconds)
    {
        if (!TryToUtcDate(unixNanoseconds, out var date))
        {
            throw new ArgumentOutOfRangeException(
                nameof(unixNanoseconds),
                "This is DBN's undefined-timestamp sentinel, not a time. Use TryToUtcDate where an absent timestamp is expected.");
        }

        return date;
    }

    /// <summary>
    /// Converts a UTC calendar date to the raw <see cref="ulong"/> nanoseconds of its 00:00 UTC
    /// midnight.
    /// </summary>
    /// <remarks>
    /// The inverse of <see cref="ToUtcDate"/> only in the direction that loses nothing: every
    /// timestamp on a given day maps to that day, and the day maps back to its first nanosecond.
    /// </remarks>
    /// <param name="date">The UTC date.</param>
    /// <returns>Nanoseconds since the UNIX epoch at 00:00 UTC on <paramref name="date"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="date"/> is before 1970-01-01, or so far after it that its midnight does not
    /// fit in a <see cref="ulong"/> nanosecond count.
    /// </exception>
    public static ulong ToUnixNanosecondsAtMidnightUtc(LocalDate date)
    {
        var days = Period.DaysBetween(UnixEpochDate, date);
        if (days < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(date),
                "DBN timestamps are unsigned nanoseconds since the UNIX epoch; this date is before it.");
        }

        if ((ulong)days > MaxDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(date),
                "This date is too far in the future to be a DBN timestamp: its midnight does not fit in an unsigned 64-bit nanosecond count.");
        }

        return (ulong)days * NanosecondsPerDay;
    }
}
