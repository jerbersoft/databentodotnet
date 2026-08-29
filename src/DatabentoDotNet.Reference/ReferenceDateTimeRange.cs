using System.Globalization;
using DatabentoDotNet.Historical;
using NodaTime;

namespace DatabentoDotNet.Reference;

/// <summary>
/// The request range the three reference <c>get_range</c> endpoints take: an inclusive
/// <see cref="Start"/> instant and an <b>optional</b> exclusive <see cref="End"/>. Omit the end and
/// the response runs to the end of the data.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because <see cref="DateTimeRange"/> cannot express an absent end</b>, and not for
/// any reason to do with what the reference API means by a range. That type requires both ends and
/// rejects an empty one at construction, which is right for the historical endpoints that carry it
/// — every one of them names a bounded query. The reference endpoints do not: upstream's
/// <c>End</c> renderer pushes nothing at all when the end is <c>None</c>
/// (<c>databento-rs/src/reference.rs:234-250</c>), and an <c>end=</c> with an empty value is a
/// different request from no <c>end</c> at all.
/// </para>
/// <para>
/// <b>That the end is exclusive is upstream's doc comment, not a probe.</b>
/// <c>reference/security.rs:101-105</c>, <c>corporate.rs:128-132</c> and
/// <c>adjustment.rs:59-63</c> each say "the exclusive end time of the request range", and nothing
/// in either library has asked the server. That is the exact shape of the assumption
/// <see href="https://github.com/jerbersoft/databentodotnet/issues/45">#45</see> found to be
/// <em>false</em> for <c>get_dataset_condition</c> and
/// <see href="https://github.com/jerbersoft/databentodotnet/issues/46">#46</see> found to be true
/// for three others — each only because someone asked. So this documents the end as
/// <em>documented</em> exclusive and <em>unprobed</em>, the way <see cref="DateTimeRange"/> already
/// does for <c>timeseries.get_range</c> and <c>batch.submit_job</c>. The probe belongs to
/// <see href="https://github.com/jerbersoft/databentodotnet/issues/57">#57</see>, where a real
/// reference request can be made under its own gate; these three endpoints bill, so it is not free
/// and is not made here.
/// </para>
/// <para>
/// <b>It lives in this package rather than beside <see cref="DateTimeRange"/>.</b> No historical
/// endpoint accepts an open-ended range, so putting it there would add a public type to a package
/// that nothing in that package consumes — and would stand next to <see cref="DateTimeRange"/>
/// inviting a caller to reach for the wrong one. Upstream draws the same line: its <c>Start</c> and
/// <c>End</c> renderers are declared in <c>reference.rs</c>, not shared down from <c>historical</c>
/// the way <c>AddToForm</c> and <c>handle_zstd_jsonl_response</c> are. The direction of the
/// dependency makes <see cref="From(DateTimeRange)"/> possible from here and would not make the
/// reverse possible from there.
/// </para>
/// <para>
/// <b>The factory set is deliberately four, where <see cref="DateTimeRange"/> has five.</b>
/// <see cref="StartingAt"/> is the open range this type exists for; <see cref="Between"/> is the
/// closed one; <see cref="FromUnixNanoseconds"/> is the wire crossing; and
/// <see cref="From(DateTimeRange)"/> converts. There is no <c>OnDay</c>, <c>Including</c> or
/// <c>Spanning</c> here, because <see cref="DateTimeRange"/> already has all three and
/// <see cref="From(DateTimeRange)"/> is one call — duplicating them would mean two places to keep
/// the day-boundary and off-by-one-nanosecond rules right instead of one.
/// </para>
/// </remarks>
public readonly record struct ReferenceDateTimeRange
{
    /// <summary>
    /// <see langword="false"/> for <see langword="default"/> and <see langword="true"/> for every
    /// value a factory produced.
    /// </summary>
    /// <remarks>
    /// <see cref="DateTimeRange"/> needs no such flag: its invariant is that the end is strictly
    /// after the start, which <see langword="default"/> violates, so the invariant itself detects
    /// one. Here it cannot. An absent end is a <em>legal</em> state, so a
    /// <see langword="default"/> value is indistinguishable from
    /// <c>StartingAt(NodaConstants.UnixEpoch)</c> by its values alone — and that value renders as a
    /// perfectly well-formed request for everything recorded since 1970, against endpoints that
    /// bill by what they return. A silently enormous query is the failure mode this field is here
    /// to prevent; it is a field rather than a computed check because there is nothing left to
    /// compute.
    /// </remarks>
    private readonly bool _built;

    private ReferenceDateTimeRange(Instant start, Instant? end, string parameterName)
    {
        Validate(start, end, parameterName);
        Start = start;
        End = end;
        _built = true;
    }

    /// <summary>The inclusive start instant. Always present.</summary>
    public Instant Start { get; }

    /// <summary>
    /// The exclusive end instant, or <see langword="null"/> for a range that runs to the end of the
    /// data.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> here is also what <see langword="default"/> carries, so this alone
    /// does not distinguish an open range from a value no factory built. The accessors that render
    /// what goes on the wire do — see <see cref="ToFormParameters"/>.
    /// </remarks>
    public Instant? End { get; }

    /// <summary>
    /// A range starting at <paramref name="start"/> and running to the end of the data: the
    /// <c>end</c> parameter is not sent.
    /// </summary>
    /// <param name="start">The inclusive start instant.</param>
    /// <returns>The range.</returns>
    public static ReferenceDateTimeRange StartingAt(Instant start) => new(start, null, nameof(start));

    /// <summary>
    /// A half-open range: <paramref name="start"/> is included, <paramref name="end"/> is not.
    /// </summary>
    /// <param name="start">The inclusive start instant.</param>
    /// <param name="end">The exclusive end instant.</param>
    /// <returns>The range.</returns>
    /// <exception cref="ArgumentException"><paramref name="end"/> is not strictly after <paramref name="start"/>.</exception>
    public static ReferenceDateTimeRange Between(Instant start, Instant end) => new(start, end, nameof(end));

    /// <summary>
    /// Widens a historical <see cref="DateTimeRange"/> into this type, keeping both ends. The
    /// result is never open-ended, because the range it came from could not be.
    /// </summary>
    /// <remarks>
    /// This is the conversion a caller who already holds a <see cref="DateTimeRange"/> — from
    /// <see cref="DateTimeRange.OnDay"/>, <see cref="DateTimeRange.Including"/>,
    /// <see cref="DateTimeRange.Spanning"/>, or a metadata query they are about to repeat against
    /// the reference API — reaches for instead of rebuilding it. There is no conversion back, and
    /// that is not an omission: an open range has no <see cref="DateTimeRange"/> to become.
    /// </remarks>
    /// <param name="range">The historical range.</param>
    /// <returns>The range.</returns>
    /// <exception cref="ArgumentException"><paramref name="range"/> is a default <see cref="DateTimeRange"/> value, which names no range.</exception>
    public static ReferenceDateTimeRange From(DateTimeRange range)
    {
        if (range == default)
        {
            throw new ArgumentException(
                "A default DateTimeRange value names no range, so there is nothing to convert. "
                + "Build one with DateTimeRange.OnDay, DateTimeRange.Between, DateTimeRange.Including, "
                + "DateTimeRange.Spanning, or DateTimeRange.FromUnixNanoseconds — or, for a range with "
                + "no end, ReferenceDateTimeRange.StartingAt.",
                nameof(range));
        }

        return new(range.Start, range.End, nameof(range));
    }

    /// <summary>
    /// A range built directly from Unix-nanosecond integers, the representation in which the
    /// reference API's <c>start</c> and <c>end</c> parameters travel on the wire.
    /// </summary>
    /// <remarks>
    /// Exact where a BCL <c>DateTimeOffset</c> pair would not be: two Unix-nanosecond integers one
    /// apart collapse to the same <c>DateTimeOffset</c> (100 ns resolution) and round-trip through
    /// <see cref="Instant"/> unchanged. See CLAUDE.md, "Dates and times", and
    /// <see cref="DateTimeRange.FromUnixNanoseconds"/>, which carries the same guarantee for the
    /// historical endpoints.
    /// </remarks>
    /// <param name="startUnixNanoseconds">Nanoseconds since the UNIX epoch, inclusive.</param>
    /// <param name="endUnixNanoseconds">
    /// Nanoseconds since the UNIX epoch, exclusive, or <see langword="null"/> for a range that runs
    /// to the end of the data.
    /// </param>
    /// <returns>The range.</returns>
    /// <exception cref="ArgumentException"><paramref name="endUnixNanoseconds"/> is not strictly after <paramref name="startUnixNanoseconds"/>.</exception>
    public static ReferenceDateTimeRange FromUnixNanoseconds(long startUnixNanoseconds, long? endUnixNanoseconds) =>
        new(
            ToInstant(startUnixNanoseconds),
            endUnixNanoseconds is { } end ? ToInstant(end) : null,
            nameof(endUnixNanoseconds));

    /// <summary>
    /// This range's <see cref="Start"/>, rendered the way the reference API's <c>start</c>
    /// parameter expects it: Unix nanoseconds.
    /// </summary>
    /// <exception cref="InvalidOperationException">This is a default <see cref="ReferenceDateTimeRange"/> value.</exception>
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
    /// This range's <see cref="End"/> in Unix nanoseconds, or <see langword="null"/> when the range
    /// is open-ended — in which case the <c>end</c> parameter is not sent at all.
    /// </summary>
    /// <exception cref="InvalidOperationException">This is a default <see cref="ReferenceDateTimeRange"/> value.</exception>
    /// <exception cref="OverflowException">
    /// <see cref="End"/> is too far from the Unix epoch (roughly beyond the year 2262) for its
    /// nanosecond count to fit in a <see cref="long"/>. See CLAUDE.md, "Dates and times".
    /// </exception>
    public long? EndUnixNanoseconds
    {
        get
        {
            EnsureUsable();
            return End is { } end ? ToUnixNanoseconds(end) : null;
        }
    }

    /// <summary>
    /// Renders this range as the <c>start</c> and <c>end</c> form fields the reference
    /// <c>get_range</c> endpoints post — <b>one field when the range is open, two when it is
    /// not</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The branch lives here rather than in each of the three parameter sets that carry a range, so
    /// there is one place to get it right instead of three. Upstream reaches the same arrangement
    /// through its <c>AddToForm&lt;End&gt;</c> impl (<c>reference.rs:242-250</c>), which the three
    /// <c>get_range</c> functions each call rather than re-deriving.
    /// </para>
    /// <para>
    /// An open range yields the key set <c>{start}</c>. It does not yield <c>{start, end}</c> with
    /// an empty value: <c>end=</c> is a different request, and one this library never sends.
    /// </para>
    /// </remarks>
    /// <returns>One or two form fields, <c>start</c> first.</returns>
    /// <exception cref="InvalidOperationException">This is a default <see cref="ReferenceDateTimeRange"/> value.</exception>
    /// <exception cref="OverflowException">
    /// An end of the range is too far from the Unix epoch (roughly beyond the year 2262) for its
    /// nanosecond count to fit in a <see cref="long"/>.
    /// </exception>
    public IReadOnlyList<KeyValuePair<string, string>> ToFormParameters()
    {
        var parameters = new List<KeyValuePair<string, string>>(2)
        {
            new("start", StartUnixNanoseconds.ToString(CultureInfo.InvariantCulture)),
        };

        if (EndUnixNanoseconds is { } end)
        {
            parameters.Add(new("end", end.ToString(CultureInfo.InvariantCulture)));
        }

        return parameters;
    }

    /// <summary>
    /// A debugging-oriented description, printing <see cref="Start"/> and <see cref="End"/>
    /// directly rather than through <see cref="StartUnixNanoseconds"/>/<see cref="EndUnixNanoseconds"/>.
    /// </summary>
    /// <remarks>
    /// Hand-written for the reason <see cref="DateTimeRange.ToString"/> is: the compiler-synthesized
    /// record <c>ToString</c> prints every public property, including the two that refuse to render
    /// a <see langword="default"/> value — which would make a supposedly inert <c>ToString()</c>
    /// call throw. It goes one step further than that sibling and says <c>(default)</c> outright,
    /// because printing this type's default as a start of <c>1970-01-01T00:00:00Z</c> with no end
    /// would look exactly like a range someone meant to build.
    /// </remarks>
    /// <returns>The description.</returns>
    public override string ToString() =>
        _built
            ? $"ReferenceDateTimeRange {{ Start = {Start}, End = {(End is { } end ? end.ToString() : "(open)")} }}"
            : "ReferenceDateTimeRange { (default) }";

    private static long ToUnixNanoseconds(Instant instant) => (instant - NodaConstants.UnixEpoch).ToInt64Nanoseconds();

    private static Instant ToInstant(long unixNanoseconds) => NodaConstants.UnixEpoch + Duration.FromNanoseconds(unixNanoseconds);

    private static void Validate(Instant start, Instant? end, string parameterName)
    {
        // An absent end is not a range to validate: it is the one this type was added to express.
        if (end is not { } bound || bound > start)
        {
            return;
        }

        // Instant.ToString() rather than the Unix-nanosecond wire rendering, matching
        // DateTimeRange.Validate: an out-of-range instant would overflow ToInt64Nanoseconds while
        // building this very message, turning a clear "your range is inverted" report into an
        // unrelated OverflowException.
        throw new ArgumentException(
            $"A reference range's end ({bound}) must be strictly after its start ({start}). "
            + "An empty or inverted range is rejected here rather than sent to the reference API; "
            + "for a range with no end at all, use ReferenceDateTimeRange.StartingAt.",
            parameterName);
    }

    /// <summary>
    /// Guards the wire-rendering accessors against a default-constructed
    /// <see cref="ReferenceDateTimeRange"/> — a struct's implicit parameterless constructor cannot
    /// be suppressed, and skips <see cref="Validate"/> entirely.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow, matching <see cref="DateTimeRange"/>: equality, hashing and
    /// <see cref="ToString"/> all still work on a default value, and so do <see cref="Start"/> and
    /// <see cref="End"/>. Only what actually goes on the wire refuses to answer.
    /// </remarks>
    private void EnsureUsable()
    {
        if (!_built)
        {
            throw new InvalidOperationException(
                "This is a default ReferenceDateTimeRange value, which names no range. Build one "
                + "with ReferenceDateTimeRange.StartingAt, ReferenceDateTimeRange.Between, "
                + "ReferenceDateTimeRange.From, or ReferenceDateTimeRange.FromUnixNanoseconds.");
        }
    }
}
