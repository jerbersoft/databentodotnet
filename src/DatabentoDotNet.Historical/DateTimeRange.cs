using NodaTime;

namespace DatabentoDotNet.Historical;

/// <summary>
/// A half-open UTC interval on the timeline: an inclusive <see cref="Start"/> instant and an
/// exclusive <see cref="End"/> instant. This is the type the historical endpoints that query by
/// exact time, rather than by calendar date, take.
/// </summary>
/// <remarks>
/// <para>
/// <b>The server agrees about the exclusive end, and that was probed rather than assumed
/// (<see href="https://github.com/jerbersoft/databentodotnet/issues/46">#46</see>).</b> All three
/// endpoints that send one of these today — <c>metadata.get_record_count</c>,
/// <c>get_billable_size</c> and <c>get_cost</c>, which share
/// <see cref="MetadataQueryParams"/> — leave a record stamped exactly on <see cref="End"/> out of
/// their answer, and all three refuse <c>start == end</c> with HTTP 422
/// <c>data_time_range_start_on_or_after_end</c>. That refusal is the decisive half: an endpoint
/// reading its end as inclusive could not make it, because <c>start == end</c> is how such an
/// endpoint spells a single instant. So no renderer converts anything here — unlike
/// <see cref="DateRange"/>, whose two renderings exist because
/// <c>get_dataset_condition</c> and <c>list_datasets</c> disagree with each other
/// (<see href="https://github.com/jerbersoft/databentodotnet/issues/45">#45</see>).
/// </para>
/// <para>
/// <b>This deliberately no longer claims that <em>every</em> such endpoint reads it this way.</b>
/// It used to, and that sentence was never earned: it was a universal claim about server behaviour
/// with no probe behind it, which is the exact shape of the assumption #45 found to be false for
/// <see cref="DateRange"/> and #37 found to be true — each only because someone asked. Three
/// sibling endpoints agreeing is a strong prior for a fourth and is not a substitute for asking it;
/// <c>get_dataset_condition</c> and <c>list_datasets</c> take an identical type and disagree to
/// this day. <c>timeseries.get_range</c>
/// (<see href="https://github.com/jerbersoft/databentodotnet/issues/38">#38</see>) and
/// <c>batch.submit_job</c> (<see href="https://github.com/jerbersoft/databentodotnet/issues/39">#39</see>)
/// are unprobed because they cost money, and each probes its own end before relying on this.
/// </para>
/// <para>
/// Port of upstream's <c>DateTimeRange</c> (<c>databento-rs/src/historical.rs</c>). Upstream's
/// field is <c>time::OffsetDateTime</c>; this is <see cref="Instant"/>, not <c>ZonedDateTime</c>.
/// The historical API is UTC throughout, so there is no time zone for a caller to get right or
/// wrong — adding one would only invite a caller to pass a local wall-clock time that means
/// something different from what they think. See PORTING.md §2.
/// </para>
/// <para>
/// As with <see cref="DateRange"/>, the named factories — <see cref="OnDay"/>,
/// <see cref="Between"/>, <see cref="Including"/>, <see cref="Spanning"/>,
/// <see cref="FromUnixNanoseconds"/> — replace upstream's <c>From</c> impls, and each name says
/// which end is exclusive. <b>An empty or inverted range is rejected at construction</b>, for the
/// same reason <see cref="DateRange"/> rejects one: see that type's remarks and the M3 ROADMAP
/// decision record.
/// </para>
/// </remarks>
public readonly record struct DateTimeRange
{
    /// <summary>The inclusive start instant.</summary>
    public Instant Start { get; }

    /// <summary>The exclusive end instant.</summary>
    public Instant End { get; }

    private DateTimeRange(Instant start, Instant end, string parameterName)
    {
        Validate(start, end, parameterName);
        Start = start;
        End = end;
    }

    /// <summary>A range covering exactly one UTC calendar day, from midnight to the following midnight.</summary>
    /// <param name="date">The day.</param>
    /// <returns>The range.</returns>
    public static DateTimeRange OnDay(LocalDate date) =>
        new(DateRange.AtMidnightUtc(date), DateRange.AtMidnightUtc(date.PlusDays(1)), nameof(date));

    /// <summary>
    /// A half-open range: <paramref name="start"/> is included, <paramref name="end"/> is not.
    /// </summary>
    /// <param name="start">The inclusive start instant.</param>
    /// <param name="end">The exclusive end instant.</param>
    /// <returns>The range.</returns>
    /// <exception cref="ArgumentException"><paramref name="end"/> is not strictly after <paramref name="start"/>.</exception>
    public static DateTimeRange Between(Instant start, Instant end) => new(start, end, nameof(end));

    /// <summary>
    /// A range where both <paramref name="start"/> and <paramref name="lastInstant"/> are
    /// included — the wire-representable instant immediately after <paramref name="lastInstant"/>
    /// becomes the exclusive <see cref="End"/>, one nanosecond later.
    /// </summary>
    /// <param name="start">The inclusive start instant.</param>
    /// <param name="lastInstant">The last instant the range covers, inclusive.</param>
    /// <returns>The range.</returns>
    /// <exception cref="ArgumentException"><paramref name="lastInstant"/> is before <paramref name="start"/>.</exception>
    public static DateTimeRange Including(Instant start, Instant lastInstant) =>
        new(start, lastInstant + Duration.FromNanoseconds(1), nameof(lastInstant));

    /// <summary>A range starting at <paramref name="start"/> and spanning exactly <paramref name="duration"/>.</summary>
    /// <param name="start">The inclusive start instant.</param>
    /// <param name="duration">How long the range spans.</param>
    /// <returns>The range.</returns>
    /// <exception cref="ArgumentException"><paramref name="duration"/> is zero or negative.</exception>
    public static DateTimeRange Spanning(Instant start, Duration duration) => new(start, start + duration, nameof(duration));

    /// <summary>
    /// A range built directly from Unix-nanosecond integers, the representation in which the
    /// historical API's <c>start</c>/<c>end</c> parameters travel on the wire.
    /// </summary>
    /// <remarks>
    /// This is the crossing a query-response value comes back through. It exists specifically
    /// because it is exact where a BCL <c>DateTimeOffset</c> pair would not be: two Unix-nanosecond
    /// integers one apart collapse to the same <c>DateTimeOffset</c> (100 ns resolution), but
    /// round-trip through <see cref="Instant"/> unchanged. See CLAUDE.md, "Dates and times".
    /// </remarks>
    /// <param name="startUnixNanoseconds">Nanoseconds since the UNIX epoch, inclusive.</param>
    /// <param name="endUnixNanoseconds">Nanoseconds since the UNIX epoch, exclusive.</param>
    /// <returns>The range.</returns>
    /// <exception cref="ArgumentException"><paramref name="endUnixNanoseconds"/> is not strictly after <paramref name="startUnixNanoseconds"/>.</exception>
    public static DateTimeRange FromUnixNanoseconds(long startUnixNanoseconds, long endUnixNanoseconds) =>
        new(ToInstant(startUnixNanoseconds), ToInstant(endUnixNanoseconds), nameof(endUnixNanoseconds));

    /// <summary>
    /// Narrows this range to a <see cref="DateRange"/>: <see cref="Start"/>'s UTC calendar date,
    /// inclusive, through <see cref="End"/>'s, exclusive — rounded <b>up</b> to the next day
    /// when <see cref="End"/> does not fall exactly on a UTC midnight.
    /// </summary>
    /// <remarks>
    /// The round-up is upstream's behavior, not an approximation this port introduces: a range
    /// ending mid-day still covers part of that day, and a <see cref="DateRange"/> can only name
    /// whole days. Rounding down would silently drop that day's data from a date-based query;
    /// rounding up is the direction that never loses data, at the cost of a query that — by
    /// design — may cover slightly more than <see cref="Start"/>–<see cref="End"/> did.
    /// </remarks>
    /// <returns>The narrowed range.</returns>
    public DateRange ToDateRange()
    {
        var startDate = Start.InUtc().Date;
        var endZoned = End.InUtc();
        var endDate = endZoned.TimeOfDay == LocalTime.Midnight ? endZoned.Date : endZoned.Date.PlusDays(1);
        return DateRange.Between(startDate, endDate);
    }

    /// <summary>
    /// A debugging-oriented description, printing <see cref="Start"/> and <see cref="End"/>
    /// directly rather than through <see cref="StartUnixNanoseconds"/>/<see cref="EndUnixNanoseconds"/>.
    /// </summary>
    /// <remarks>
    /// A hand-written override, not the compiler-synthesized record <c>ToString</c>: that
    /// synthesized version prints every public property, including
    /// <see cref="StartUnixNanoseconds"/> and <see cref="EndUnixNanoseconds"/> — which would make
    /// a supposedly inert <c>ToString()</c> call throw <see cref="InvalidOperationException"/> for
    /// a default value, defeating the point of leaving it unguarded. Printing
    /// <see cref="Start"/>/<see cref="End"/> instead never throws, for any value including
    /// <see langword="default"/>.
    /// </remarks>
    /// <returns>The description.</returns>
    public override string ToString() => $"DateTimeRange {{ Start = {Start}, End = {End} }}";

    /// <summary>
    /// This range's <see cref="Start"/>, rendered the way the historical API's <c>start</c>
    /// parameter expects it: Unix nanoseconds.
    /// </summary>
    /// <exception cref="InvalidOperationException">This is a default <see cref="DateTimeRange"/> value.</exception>
    /// <exception cref="OverflowException">
    /// <see cref="Start"/> is too far from the Unix epoch (roughly beyond the year 2262) for its
    /// nanosecond count to fit in a <see cref="long"/>. See CLAUDE.md, "Dates and times".
    /// </exception>
    public long StartUnixNanoseconds
    {
        get
        {
            EnsureUsable();
            return ToUnixNanoseconds(Start);
        }
    }

    /// <summary>
    /// This range's <see cref="End"/>, rendered the way the historical API's <c>end</c>
    /// parameter expects it: Unix nanoseconds.
    /// </summary>
    /// <exception cref="InvalidOperationException">This is a default <see cref="DateTimeRange"/> value.</exception>
    /// <exception cref="OverflowException">
    /// <see cref="End"/> is too far from the Unix epoch (roughly beyond the year 2262) for its
    /// nanosecond count to fit in a <see cref="long"/>. See CLAUDE.md, "Dates and times".
    /// </exception>
    public long EndUnixNanoseconds
    {
        get
        {
            EnsureUsable();
            return ToUnixNanoseconds(End);
        }
    }

    private static long ToUnixNanoseconds(Instant instant) => (instant - NodaConstants.UnixEpoch).ToInt64Nanoseconds();

    private static Instant ToInstant(long unixNanoseconds) => NodaConstants.UnixEpoch + Duration.FromNanoseconds(unixNanoseconds);

    /// <summary>
    /// <see langword="true"/> when <paramref name="end"/> is not strictly after
    /// <paramref name="start"/> — the one condition every factory refuses to construct, and the
    /// one a default-constructed <see cref="DateTimeRange"/> is left in, since <see cref="Start"/>
    /// and <see cref="End"/> then share the same default <see cref="Instant"/>.
    /// </summary>
    private static bool IsInvalidRange(Instant start, Instant end) => end <= start;

    private static void Validate(Instant start, Instant end, string parameterName)
    {
        if (IsInvalidRange(start, end))
        {
            // Instant.ToString() rather than the Unix-nanosecond wire rendering: an out-of-range
            // instant (further than ~292 years from the epoch) would overflow ToInt64Nanoseconds
            // while building this very message, turning a clear "your range is inverted" report
            // into an unrelated OverflowException. ToString() has no such ceiling.
            throw new ArgumentException(
                $"A date-time range's end ({end}) must be strictly after its start ({start}). "
                + "An empty or inverted range is rejected here rather than sent to the historical API.",
                parameterName);
        }
    }

    /// <summary>
    /// Guards the wire-rendering accessors against a default-constructed
    /// <see cref="DateTimeRange"/>. Every factory guarantees <see cref="End"/> is strictly after
    /// <see cref="Start"/>, so this condition holds only for <see langword="default"/> — a
    /// struct's implicit parameterless constructor cannot be suppressed, and skips
    /// <see cref="Validate"/> entirely.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow, matching <c>Symbols.ToChunks()</c> rather than guarding every member:
    /// equality, hashing, and <see cref="ToString"/> all still work on a default value — the
    /// latter only because it is hand-written rather than the compiler-synthesized record
    /// <c>ToString</c>, which would otherwise print
    /// <see cref="StartUnixNanoseconds"/>/<see cref="EndUnixNanoseconds"/> and throw right back.
    /// <see cref="DateRange"/> carries the identical guard. Only the accessors that render what
    /// actually goes on the wire refuse to answer with a plausible-looking but meaningless value
    /// — a silent <c>0</c> here, or <c>"0001-01-01"</c> for <see cref="DateRange"/>'s date-string
    /// accessors.
    /// </remarks>
    private void EnsureUsable()
    {
        if (IsInvalidRange(Start, End))
        {
            throw new InvalidOperationException(
                "This is a default DateTimeRange value, which names no range. Build one with "
                + "DateTimeRange.OnDay, DateTimeRange.Between, DateTimeRange.Including, "
                + "DateTimeRange.Spanning, or DateTimeRange.FromUnixNanoseconds.");
        }
    }
}
