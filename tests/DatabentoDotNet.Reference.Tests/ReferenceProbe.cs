using System.Globalization;
using System.Text;

namespace DatabentoDotNet.Reference.Tests;

/// <summary>
/// Whether a sequence of index keys arrived in ascending order, and where it first did not.
/// </summary>
/// <remarks>
/// <b><see cref="Descent"/> is a string rather than a pair of keys, and that is the point.</b> The
/// assertion that consumes this verdict interpolates its message eagerly — every
/// <c>Assert.True(condition, message)</c> in C# builds the message on the passing path too — so a
/// message that reached back into the key list by index would throw on the *ordered* path, which
/// is the outcome the test exists to confirm. Formatting the break at the moment it is found means
/// there is no index left to get wrong.
/// </remarks>
public readonly record struct OrderingVerdict
{
    /// <summary>How many keys were compared.</summary>
    public required int ComparedCount { get; init; }

    /// <summary>
    /// The index of the first key that sorts before its predecessor, or <c>-1</c> when there is
    /// none.
    /// </summary>
    public required int FirstDescentIndex { get; init; }

    /// <summary>
    /// The first descent in words, or the empty string when <see cref="IsOrdered"/>.
    /// </summary>
    public required string Descent { get; init; }

    /// <summary>Whether every key was greater than or equal to the one before it.</summary>
    public bool IsOrdered => FirstDescentIndex < 0;
}

/// <summary>What a boundary narrowing established about the range's <c>end</c>.</summary>
public enum BoundaryReading
{
    /// <summary>
    /// The boundary rows left and nothing else did — the claim
    /// <c>ReferenceDateTimeRange</c> documents as unprobed, confirmed.
    /// </summary>
    Exclusive,

    /// <summary>
    /// Rows dated on the boundary came back from a range ending on it. This is the #45 shape:
    /// upstream documents the end exclusive and the server disagrees.
    /// </summary>
    Inclusive,

    /// <summary>
    /// The run establishes neither. Either the narrowing removed rows that were not on the
    /// boundary — so the two queries differ by more than their <c>end</c> — or no row sat on the
    /// boundary at all, so nothing was narrowed away and nothing was tested.
    /// </summary>
    Confounded,
}

/// <summary>
/// What narrowing a range's <c>end</c> onto a populated boundary date did to the rows on it.
/// </summary>
/// <remarks>
/// <para>
/// The arithmetic is trivial and the interpretation is not, which is why this is a type rather
/// than two comparisons at the call site. <b>The two comparisons are not independent and must not
/// be asserted in either order.</b> An inclusive <c>end</c> returns the boundary rows, so it fails
/// the row-count check as well — assert that check first and a genuine #45-shaped finding is
/// reported as "the two queries differ by more than their end", which is both wrong and the exact
/// answer #49 is waiting to not get. Assert exclusivity first and a confounded run is reported as
/// an inclusive <c>end</c>, which is worse.
/// </para>
/// <para>
/// <see cref="Reading"/> is therefore the only thing a caller should branch on: it collapses the
/// pair into the three outcomes that actually exist, in the order that makes each one reachable.
/// </para>
/// </remarks>
public readonly record struct BoundaryVerdict
{
    /// <summary>Rows returned by the full window.</summary>
    public required int WindowCount { get; init; }

    /// <summary>Rows in the full window dated on the boundary day.</summary>
    public required int OnTheBoundary { get; init; }

    /// <summary>Rows returned once the range's <c>end</c> was moved onto the boundary.</summary>
    public required int NarrowedCount { get; init; }

    /// <summary>Rows dated on the boundary that survived that narrowing.</summary>
    public required int Survivors { get; init; }

    /// <summary>What <see cref="NarrowedCount"/> must be if the boundary rows are all that left.</summary>
    public int ExpectedNarrowedCount => WindowCount - OnTheBoundary;

    /// <summary>
    /// Whether the narrowing removed exactly the boundary rows. Only meaningful alongside
    /// <see cref="Survivors"/> — see <see cref="Reading"/>, which is what callers should use.
    /// </summary>
    public bool IsConsistent => NarrowedCount == ExpectedNarrowedCount;

    /// <summary>The answer this run supports, if any.</summary>
    /// <remarks>
    /// Ordered so each outcome is reachable: a surviving boundary row settles the question on its
    /// own, and the row count only corroborates the reading left over when none survived. A run
    /// with no boundary row to remove is confounded rather than exclusive — it narrowed nothing
    /// away, so it demonstrated nothing.
    /// </remarks>
    public BoundaryReading Reading => Survivors > 0
        ? BoundaryReading.Inclusive
        : OnTheBoundary > 0 && IsConsistent
            ? BoundaryReading.Exclusive
            : BoundaryReading.Confounded;
}

/// <summary>One rate the server sent, and which field it came from.</summary>
public readonly record struct RateObservation
{
    /// <summary>The field's name, qualified by endpoint, for the report.</summary>
    public required string Field { get; init; }

    /// <summary>The value, as <see cref="decimal"/> already held it.</summary>
    public required decimal Value { get; init; }
}

/// <summary>The magnitudes one field's rates spanned.</summary>
public readonly record struct RateFieldSummary
{
    /// <summary>The field's name.</summary>
    public required string Field { get; init; }

    /// <summary>How many values it carried.</summary>
    public required int Count { get; init; }

    /// <summary>The smallest value, signed and as measured.</summary>
    public required decimal Minimum { get; init; }

    /// <summary>The largest value, signed and as measured.</summary>
    public required decimal Maximum { get; init; }
}

/// <summary>
/// The magnitudes a sample of rates carried, and whether any sat outside the plausible band.
/// </summary>
/// <remarks>
/// <b>The report is the deliverable, not the pass.</b> #53 asks what magnitudes the rate fields
/// actually carry; a green assertion answers "nothing outrageous", which is not a sentence anyone
/// can put in the issue. <see cref="Render"/> prints the per-field spans so the answer can be
/// copied back to #53 rather than re-derived from a checkmark.
/// </remarks>
public sealed record MagnitudeVerdict
{
    /// <summary>How many rates were read.</summary>
    public required int ObservedCount { get; init; }

    /// <summary>The lower bound of the asserted band, on magnitude.</summary>
    public required decimal Floor { get; init; }

    /// <summary>The upper bound of the asserted band, on magnitude.</summary>
    public required decimal Ceiling { get; init; }

    /// <summary>Every field that carried a value, with the span it covered, ordered by name.</summary>
    public required IReadOnlyList<RateFieldSummary> Fields { get; init; }

    /// <summary>
    /// The values outside the band, formatted and deduplicated, ordered by name. Empty when the
    /// whole sample sat inside it.
    /// </summary>
    public required IReadOnlyList<string> Extreme { get; init; }

    /// <summary>Whether every rate sat inside the band.</summary>
    public bool IsWithinBand => Extreme.Count == 0;

    /// <summary>
    /// The per-field spans, as a table for <c>ITestOutputHelper</c>.
    /// </summary>
    /// <returns>The report, or a single line saying nothing was observed.</returns>
    public string Render()
    {
        if (Fields.Count == 0)
        {
            return "No rate was observed, so there is nothing to report.";
        }

        var width = Fields.Max(f => f.Field.Length);
        var report = new StringBuilder();

        report.Append(CultureInfo.InvariantCulture, $"{ObservedCount} rate(s) observed; ");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"band asserted on magnitude is [{Floor}, {Ceiling}].");

        foreach (var field in Fields)
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"  {field.Field.PadRight(width)}  n={field.Count,-5} "
                + $"min={field.Minimum.ToString(CultureInfo.InvariantCulture)} "
                + $"max={field.Maximum.ToString(CultureInfo.InvariantCulture)}");
        }

        return report.ToString().TrimEnd();
    }
}

/// <summary>
/// The decision procedures behind the three answers #57 owes #49, #52 and #53.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate from the requests that feed them, so that they can be tested for free.</b>
/// Everything here is arithmetic over values a caller supplies — no socket, no key, no
/// entitlement — so <c>ReferenceProbeTests</c> exercises it on every <c>dotnet test</c> while
/// <c>RealReferenceRequestTests</c> stays behind two gates and a subscription this account does
/// not hold. That split is <c>LatencyStatistics</c>'s, for <c>RealGatewayLatencyTests</c>, and the
/// rule it follows is CLAUDE.md's: <em>the expensive run is for the fact only it can settle, never
/// for finding out whether the code works.</em>
/// </para>
/// <para>
/// <b>It was not obeyed here first time, and the bill for that was exact.</b> The ordering
/// experiment built its failure message by indexing the key list at the index of the first
/// descent — <c>-1</c> on the ordered path, because C# evaluates an assertion's message before it
/// evaluates the assertion. The one outcome that test existed to confirm was the one outcome it
/// could not report: a correctly sorted response threw <see cref="ArgumentOutOfRangeException"/>
/// instead of passing. Nothing caught it, because the only thing that ran it would have been the
/// entitled run it is meant to inform. <see cref="CheckOrdering"/> is that logic with the index
/// removed from the message, and <c>ReferenceProbeTests</c> is the run that would have caught it.
/// </para>
/// </remarks>
public static class ReferenceProbe
{
    /// <summary>
    /// Finds the first key that sorts before its predecessor.
    /// </summary>
    /// <typeparam name="T">The index column's type — <c>LocalDate</c> or <c>Instant</c>.</typeparam>
    /// <param name="keys">
    /// The index column, in the order the server sent it. Callers drop rows with no value rather
    /// than sorting against them: where a null sorts is a question about the server's collation
    /// and not about whether it sorted at all.
    /// </param>
    /// <returns>The verdict, ordered when fewer than two keys were supplied.</returns>
    public static OrderingVerdict CheckOrdering<T>(IEnumerable<T> keys)
        where T : IComparable<T>
    {
        ArgumentNullException.ThrowIfNull(keys);

        var ordered = keys as IReadOnlyList<T> ?? keys.ToList();

        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].CompareTo(ordered[i - 1]) < 0)
            {
                return new OrderingVerdict
                {
                    ComparedCount = ordered.Count,
                    FirstDescentIndex = i,
                    Descent = string.Create(
                        CultureInfo.InvariantCulture,
                        $"Row {i} carries {ordered[i]}, which is before row {i - 1}'s {ordered[i - 1]}"),
                };
            }
        }

        return new OrderingVerdict
        {
            ComparedCount = ordered.Count,
            FirstDescentIndex = -1,
            Descent = string.Empty,
        };
    }

    /// <summary>
    /// Reads what narrowing the range's <c>end</c> onto the boundary day did.
    /// </summary>
    /// <param name="windowCount">Rows the full window returned.</param>
    /// <param name="onTheBoundary">Rows in that window dated on the boundary day.</param>
    /// <param name="narrowedCount">Rows returned with the <c>end</c> moved onto the boundary.</param>
    /// <param name="survivors">Rows dated on the boundary that came back anyway.</param>
    /// <returns>The verdict.</returns>
    public static BoundaryVerdict CheckBoundary(
        int windowCount,
        int onTheBoundary,
        int narrowedCount,
        int survivors)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(windowCount);
        ArgumentOutOfRangeException.ThrowIfNegative(onTheBoundary);
        ArgumentOutOfRangeException.ThrowIfNegative(narrowedCount);
        ArgumentOutOfRangeException.ThrowIfNegative(survivors);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(onTheBoundary, windowCount);

        return new BoundaryVerdict
        {
            WindowCount = windowCount,
            OnTheBoundary = onTheBoundary,
            NarrowedCount = narrowedCount,
            Survivors = survivors,
        };
    }

    /// <summary>
    /// Reduces a sample of rates to its per-field spans and the values outside the band.
    /// </summary>
    /// <param name="observations">Every rate read, in any order.</param>
    /// <param name="floor">
    /// The smallest magnitude a real rate plausibly carries. A non-zero value below it fails; zero
    /// itself is exempt, being an ordinary value for a rate rather than an underflow.
    /// </param>
    /// <param name="ceiling">The largest magnitude a real rate plausibly carries.</param>
    /// <returns>The verdict.</returns>
    public static MagnitudeVerdict CheckMagnitudes(
        IEnumerable<RateObservation> observations,
        decimal floor,
        decimal ceiling)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(floor);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ceiling, floor);

        var sample = observations as IReadOnlyList<RateObservation> ?? observations.ToList();

        var fields = sample
            .GroupBy(o => o.Field, StringComparer.Ordinal)
            .Select(g => new RateFieldSummary
            {
                Field = g.Key,
                Count = g.Count(),
                Minimum = g.Min(o => o.Value),
                Maximum = g.Max(o => o.Value),
            })
            .OrderBy(f => f.Field, StringComparer.Ordinal)
            .ToList();

        var extreme = sample
            .Where(o => Math.Abs(o.Value) > ceiling
                || (o.Value != 0m && Math.Abs(o.Value) < floor))
            .Select(o => $"{o.Field} = {o.Value.ToString(CultureInfo.InvariantCulture)}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        return new MagnitudeVerdict
        {
            ObservedCount = sample.Count,
            Floor = floor,
            Ceiling = ceiling,
            Fields = fields,
            Extreme = extreme,
        };
    }
}
