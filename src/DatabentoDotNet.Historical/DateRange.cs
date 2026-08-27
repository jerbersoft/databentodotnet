using NodaTime;
using NodaTime.Text;

namespace DatabentoDotNet.Historical;

/// <summary>
/// A half-open UTC date interval: an inclusive <see cref="Start"/> date and an exclusive
/// <see cref="End"/> date. Every Databento historical endpoint that queries by calendar date,
/// rather than by exact instant, takes one of these.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>DateRange</c> (<c>databento-rs/src/historical.rs</c>). Rust spells
/// "which end is exclusive" with the range type itself — <c>Range&lt;Date&gt;</c> for half-open,
/// <c>RangeInclusive&lt;Date&gt;</c> for inclusive — because a Rust range is a type. C# has no
/// range literal to overload against, so the upstream <c>From</c> impls become named factories
/// instead: <see cref="OnDay"/>, <see cref="Between"/>, <see cref="Including"/>, <see cref="From"/>.
/// Each name says, on its own, which end is exclusive — a half-open interval is exactly the kind
/// of API a caller misreads silently, and nothing here reuses a bare constructor that would hide
/// that choice.
/// </para>
/// <para>
/// <b>An empty or inverted range is rejected at construction</b> — <see cref="End"/> must be
/// strictly after <see cref="Start"/>. Upstream sends whatever a caller built and lets the API
/// answer with an error. This port fails locally instead, the same way this codebase's
/// <c>Symbols</c> type already rejects a symbol carrying a character the wire format reserves:
/// the offending pair is still in the caller's hand at the point it is rejected, rather than
/// round-tripping to the server to learn the same thing from an HTTP error that bills for the
/// request. See the M3 ROADMAP decision record for the full reasoning.
/// </para>
/// </remarks>
public readonly record struct DateRange
{
    /// <summary>The inclusive UTC start date.</summary>
    public LocalDate Start { get; }

    /// <summary>The exclusive UTC end date.</summary>
    public LocalDate End { get; }

    private DateRange(LocalDate start, LocalDate end)
    {
        Validate(start, end);
        Start = start;
        End = end;
    }

    /// <summary>A range covering exactly one UTC calendar day.</summary>
    /// <param name="date">The day.</param>
    /// <returns>A range from <paramref name="date"/> to the following day.</returns>
    public static DateRange OnDay(LocalDate date) => new(date, date.PlusDays(1));

    /// <summary>
    /// A half-open range: <paramref name="start"/> is included, <paramref name="end"/> is not.
    /// </summary>
    /// <param name="start">The inclusive start date.</param>
    /// <param name="end">The exclusive end date.</param>
    /// <returns>The range.</returns>
    /// <exception cref="ArgumentException"><paramref name="end"/> is not strictly after <paramref name="start"/>.</exception>
    public static DateRange Between(LocalDate start, LocalDate end) => new(start, end);

    /// <summary>
    /// A range where both <paramref name="start"/> and <paramref name="lastDay"/> are included.
    /// </summary>
    /// <param name="start">The inclusive start date.</param>
    /// <param name="lastDay">The last day the range covers, inclusive.</param>
    /// <returns>The range, whose exclusive <see cref="End"/> is the day after <paramref name="lastDay"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="lastDay"/> is before <paramref name="start"/>.</exception>
    public static DateRange Including(LocalDate start, LocalDate lastDay) => new(start, lastDay.PlusDays(1));

    /// <summary>
    /// A range starting at <paramref name="start"/> and spanning <paramref name="duration"/>,
    /// counting only whole days.
    /// </summary>
    /// <remarks>
    /// A calendar date carries no time of day, so a duration shorter than 24 hours contributes no
    /// day to the range at all — this matches upstream's own <c>Date + Duration</c>, which adds
    /// <c>duration.whole_days()</c>. Upstream's own test for this construction
    /// (<c>date_range_from_lt_day_duration</c>) passes a one-second duration and asserts the
    /// resulting range is empty (<c>start == end</c>). This port does not carry that case forward:
    /// <see cref="Validate"/> runs here exactly as it does in every other factory, so a duration
    /// under one day is rejected rather than silently producing a range that would query no data.
    /// </remarks>
    /// <param name="start">The inclusive start date.</param>
    /// <param name="duration">How long the range spans, truncated down to whole days.</param>
    /// <returns>The range.</returns>
    /// <exception cref="ArgumentException"><paramref name="duration"/> spans fewer than one whole day.</exception>
    public static DateRange From(LocalDate start, Duration duration) => new(start, start.PlusDays(duration.Days));

    /// <summary>Widens this date range to a <see cref="DateTimeRange"/>, at UTC midnight on each end.</summary>
    /// <returns>
    /// A <see cref="DateTimeRange"/> whose <see cref="DateTimeRange.Start"/> and
    /// <see cref="DateTimeRange.End"/> are UTC midnight on <see cref="Start"/> and <see cref="End"/>
    /// respectively.
    /// </returns>
    public DateTimeRange ToDateTimeRange() => DateTimeRange.Between(AtMidnightUtc(Start), AtMidnightUtc(End));

    /// <summary>
    /// This range's <see cref="Start"/>, rendered the way the historical API's
    /// <c>start_date</c> query parameter expects it: <c>yyyy-MM-dd</c>.
    /// </summary>
    public string StartDate => LocalDatePattern.Iso.Format(Start);

    /// <summary>
    /// This range's <see cref="End"/>, rendered the way the historical API's <c>end_date</c>
    /// query parameter expects it: <c>yyyy-MM-dd</c>.
    /// </summary>
    public string EndDate => LocalDatePattern.Iso.Format(End);

    /// <summary>UTC midnight at the start of <paramref name="date"/>, as an <see cref="Instant"/>.</summary>
    internal static Instant AtMidnightUtc(LocalDate date) => Instant.FromUtc(date.Year, date.Month, date.Day, 0, 0);

    private static void Validate(LocalDate start, LocalDate end)
    {
        if (end <= start)
        {
            throw new ArgumentException(
                $"A date range's end ({LocalDatePattern.Iso.Format(end)}) must be strictly after "
                + $"its start ({LocalDatePattern.Iso.Format(start)}). An empty or inverted range is "
                + "rejected here rather than sent to the historical API.");
        }
    }
}
