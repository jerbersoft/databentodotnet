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
    /// Renders <paramref name="range"/> as <c>start_date</c>/<c>end_date</c> for an endpoint that
    /// reads <c>end_date</c> as <b>exclusive</b> — the same half-open contract this type carries,
    /// so <see cref="End"/> goes on the wire verbatim. This is upstream's single
    /// <c>impl AddToQuery&lt;DateRange&gt;</c> (<c>historical.rs:348-353</c>), unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both original call sites — <see cref="MetadataClient.ListDatasetsAsync"/> and
    /// <see cref="GetDatasetConditionParams.ToQueryParameters"/> — built this same pair
    /// independently before this helper existed. Consolidating them here matters beyond avoiding
    /// two copies: #37's <c>symbology.resolve</c> posts the identical two fields in a <em>form</em>
    /// rather than a query string, so without one shared renderer a third, inevitably slightly
    /// different copy was already scheduled. <c>internal</c>, not public — this adds no public
    /// surface, only a single place for every <see cref="DateRange"/> wire-rendering call site to
    /// route through.
    /// </para>
    /// <para>
    /// <b>It is no longer <em>every</em> call site, and that is the point of the name.</b>
    /// <c>get_dataset_condition</c> reads <c>end_date</c> as inclusive and takes
    /// <see cref="ToInclusiveEndDateParameters"/> instead (#45). Both methods take the identical
    /// half-open <see cref="DateRange"/> and differ only in what they put on the wire, so the
    /// choice a call site makes is a claim about the <em>endpoint</em> — the only thing that
    /// actually varies. Naming them for that, rather than leaving one of them the unmarked
    /// default, is what stops a future endpoint from picking the wrong one by simply not choosing.
    /// <b>#37's <c>symbology.resolve</c> was that future endpoint, and it was probed rather than
    /// assumed.</b> Upstream's doc for it says "inclusive start and an exclusive end"
    /// (<c>symbology.rs:78</c>) — but so did the doc for <c>get_dataset_condition</c>'s neighbours,
    /// and the answer for that one turned out to be the other way round. Asked directly, the
    /// endpoint rejects <c>start_date == end_date</c> with HTTP 422
    /// <c>data_date_range_start_on_or_after_end</c>, which is the server declaring the range
    /// half-open in its own words, so <c>symbology.resolve</c> takes this renderer.
    /// <see cref="ResolveParams.DateRange"/> records the three probes.
    /// </para>
    /// </remarks>
    /// <param name="range">The range to render.</param>
    /// <returns>The <c>start_date</c> and <c>end_date</c> key/value pairs, in that order.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="range"/> is a default <see cref="DateRange"/> value.</exception>
    internal static IReadOnlyList<KeyValuePair<string, string>> ToExclusiveEndDateParameters(DateRange range) =>
        [new("start_date", range.StartIsoDate), new("end_date", range.EndIsoDate)];

    /// <summary>
    /// Renders <paramref name="range"/> as <c>start_date</c>/<c>end_date</c> for an endpoint that
    /// reads <c>end_date</c> as <b>inclusive</b>, by sending the day before this range's exclusive
    /// <see cref="End"/>. <c>metadata.get_dataset_condition</c> is the only such endpoint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Verified against <c>hist.databento.com</c> by #44, and this conversion is #45.
    /// <c>get_dataset_condition</c> returns its <c>end_date</c>, so its range is closed at both
    /// ends; <c>metadata.list_datasets</c> — the other endpoint taking a <see cref="DateRange"/> —
    /// was probed the same way and is genuinely half-open, which is what makes converting here
    /// rather than in <see cref="ToExclusiveEndDateParameters"/> the correct half of the fix.
    /// </para>
    /// <para>
    /// The subtraction cannot invert the range: every factory guarantees <see cref="End"/> is
    /// strictly after <see cref="Start"/>, so the rendered <c>end_date</c> is at worst equal to
    /// <c>start_date</c> — which is exactly the single day <see cref="OnDay"/> asks for.
    /// </para>
    /// </remarks>
    /// <param name="range">The range to render.</param>
    /// <returns>The <c>start_date</c> and <c>end_date</c> key/value pairs, in that order.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="range"/> is a default <see cref="DateRange"/> value.</exception>
    internal static IReadOnlyList<KeyValuePair<string, string>> ToInclusiveEndDateParameters(DateRange range)
    {
        // Explicitly, because this method reaches none of the guarded accessors: it formats
        // End.PlusDays(-1), which EndIsoDate cannot give it, and so formats Start directly too
        // rather than reading one accessor for its guard and bypassing the other. Without this
        // line a default DateRange would render as "0001-01-01"/"0000-12-31" -- a range that was
        // never constructed, sent as though it had been.
        range.EnsureUsable();

        return
        [
            new("start_date", LocalDatePattern.Iso.Format(range.Start)),
            new("end_date", LocalDatePattern.Iso.Format(range.End.PlusDays(-1))),
        ];
    }

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
