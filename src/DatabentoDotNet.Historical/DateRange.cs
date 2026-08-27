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
/// instead: <see cref="OnDay"/>, <see cref="Between"/>, <see cref="Including"/>, <see cref="Spanning"/>.
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

    private DateRange(LocalDate start, LocalDate end, string parameterName)
    {
        Validate(start, end, parameterName);
        Start = start;
        End = end;
    }

    /// <summary>A range covering exactly one UTC calendar day.</summary>
    /// <param name="date">The day.</param>
    /// <returns>A range from <paramref name="date"/> to the following day.</returns>
    public static DateRange OnDay(LocalDate date) => new(date, date.PlusDays(1), nameof(date));

    /// <summary>
    /// A half-open range: <paramref name="start"/> is included, <paramref name="end"/> is not.
    /// </summary>
    /// <param name="start">The inclusive start date.</param>
    /// <param name="end">The exclusive end date.</param>
    /// <returns>The range.</returns>
    /// <exception cref="ArgumentException"><paramref name="end"/> is not strictly after <paramref name="start"/>.</exception>
    public static DateRange Between(LocalDate start, LocalDate end) => new(start, end, nameof(end));

    /// <summary>
    /// A range where both <paramref name="start"/> and <paramref name="lastDay"/> are included.
    /// </summary>
    /// <param name="start">The inclusive start date.</param>
    /// <param name="lastDay">The last day the range covers, inclusive.</param>
    /// <returns>The range, whose exclusive <see cref="End"/> is the day after <paramref name="lastDay"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="lastDay"/> is before <paramref name="start"/>.</exception>
    public static DateRange Including(LocalDate start, LocalDate lastDay) =>
        new(start, lastDay.PlusDays(1), nameof(lastDay));

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
    public static DateRange Spanning(LocalDate start, Duration duration) =>
        new(start, start.PlusDays(duration.Days), nameof(duration));

    /// <summary>Widens this date range to a <see cref="DateTimeRange"/>, at UTC midnight on each end.</summary>
    /// <returns>
    /// A <see cref="DateTimeRange"/> whose <see cref="DateTimeRange.Start"/> and
    /// <see cref="DateTimeRange.End"/> are UTC midnight on <see cref="Start"/> and <see cref="End"/>
    /// respectively.
    /// </returns>
    public DateTimeRange ToDateTimeRange() => DateTimeRange.Between(AtMidnightUtc(Start), AtMidnightUtc(End));

    /// <summary>
    /// A debugging-oriented description, printing <see cref="Start"/> and <see cref="End"/>
    /// directly rather than through <see cref="StartIsoDate"/>/<see cref="EndIsoDate"/>.
    /// </summary>
    /// <remarks>
    /// A hand-written override, not the compiler-synthesized record <c>ToString</c>: that
    /// synthesized version prints every public property, including <see cref="StartIsoDate"/> and
    /// <see cref="EndIsoDate"/> — which would make a supposedly inert <c>ToString()</c> call throw
    /// <see cref="InvalidOperationException"/> for a default value, defeating the point of
    /// leaving it unguarded. Printing <see cref="Start"/>/<see cref="End"/> instead never throws,
    /// for any value including <see langword="default"/>.
    /// </remarks>
    /// <returns>The description.</returns>
    public override string ToString() => $"DateRange {{ Start = {Start}, End = {End} }}";

    /// <summary>
    /// This range's <see cref="Start"/>, rendered the way the historical API's
    /// <c>start_date</c> parameter expects it: <c>yyyy-MM-dd</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">This is a default <see cref="DateRange"/> value.</exception>
    public string StartIsoDate
    {
        get
        {
            EnsureUsable();
            return LocalDatePattern.Iso.Format(Start);
        }
    }

    /// <summary>
    /// This range's <see cref="End"/>, rendered the way the historical API's <c>end_date</c>
    /// parameter expects it: <c>yyyy-MM-dd</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">This is a default <see cref="DateRange"/> value.</exception>
    public string EndIsoDate
    {
        get
        {
            EnsureUsable();
            return LocalDatePattern.Iso.Format(End);
        }
    }

    /// <summary>UTC midnight at the start of <paramref name="date"/>, as an <see cref="Instant"/>.</summary>
    internal static Instant AtMidnightUtc(LocalDate date) => Instant.FromUtc(date.Year, date.Month, date.Day, 0, 0);

    /// <summary>
    /// <see langword="true"/> when <paramref name="end"/> is not strictly after
    /// <paramref name="start"/> — the one condition every factory refuses to construct, and the
    /// one a default-constructed <see cref="DateRange"/> is left in, since <see cref="Start"/>
    /// and <see cref="End"/> then share the same default <see cref="LocalDate"/>.
    /// </summary>
    private static bool IsInvalidRange(LocalDate start, LocalDate end) => end <= start;

    private static void Validate(LocalDate start, LocalDate end, string parameterName)
    {
        if (IsInvalidRange(start, end))
        {
            throw new ArgumentException(
                $"A date range's end ({LocalDatePattern.Iso.Format(end)}) must be strictly after "
                + $"its start ({LocalDatePattern.Iso.Format(start)}). An empty or inverted range is "
                + "rejected here rather than sent to the historical API.",
                parameterName);
        }
    }

    /// <summary>
    /// Guards the wire-rendering accessors against a default-constructed <see cref="DateRange"/>.
    /// Every factory guarantees <see cref="End"/> is strictly after <see cref="Start"/>, so this
    /// condition holds only for <see langword="default"/> — a struct's implicit parameterless
    /// constructor cannot be suppressed, and skips <see cref="Validate"/> entirely.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow, matching <c>Symbols.ToChunks()</c> rather than guarding every member:
    /// equality, hashing, and <see cref="ToString"/> all still work on a default value — the
    /// latter only because it is hand-written rather than the compiler-synthesized record
    /// <c>ToString</c>, which would otherwise print <see cref="StartIsoDate"/>/<see cref="EndIsoDate"/>
    /// and throw right back. Only the accessors that render what actually goes on the wire refuse
    /// to answer with a plausible-looking but meaningless value — <c>"0001-01-01"</c> here, or a
    /// silent <c>0</c> for <see cref="DateTimeRange"/>'s nanosecond accessors.
    /// </remarks>
    private void EnsureUsable()
    {
        if (IsInvalidRange(Start, End))
        {
            throw new InvalidOperationException(
                "This is a default DateRange value, which names no range. Build one with "
                + "DateRange.OnDay, DateRange.Between, DateRange.Including, or DateRange.Spanning.");
        }
    }
}
