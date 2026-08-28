using System.Text.Json.Serialization;
using DatabentoDotNet.Dbn;
using NodaTime;

namespace DatabentoDotNet.Historical;

/// <summary>The available range for a dataset.</summary>
/// <remarks>
/// Port of upstream's <c>DatasetRange</c> (<c>databento-rs/src/historical/metadata.rs:307-318</c>).
/// Returned by <c>metadata.get_dataset_range</c>: a top-level range covering every schema, plus
/// the narrower range each individual schema actually has data for.
/// </remarks>
public sealed record DatasetRange
{
    /// <summary>The inclusive UTC start timestamp of the available range.</summary>
    public required Instant Start { get; init; }

    /// <summary>The exclusive UTC end timestamp of the available range.</summary>
    /// <remarks>
    /// <para>
    /// <b>Exclusive was probed, not assumed
    /// (<see href="https://github.com/jerbersoft/databentodotnet/issues/46">#46</see>).</b> A query
    /// ending one nanosecond past this instant is refused with HTTP 422
    /// <c>data_schema_not_fully_available</c>, naming the bound it exceeded, while one ending
    /// exactly on it is accepted — so this is the first instant a query may not reach.
    /// <see cref="ToDateTimeRange"/> hands it to <see cref="DateTimeRange.Between"/> as an
    /// exclusive end, and that is correct. Had it instead named the <em>last available</em>
    /// instant, that conversion would have been silently excluding the final record.
    /// </para>
    /// <para>
    /// <b>For an active dataset this is a live ingest watermark, not a fixed boundary.</b> It
    /// advances every few seconds and carries sub-second precision — one reading was
    /// <c>2026-08-28T07:37:47.468634000Z</c> — so two calls moments apart legitimately disagree.
    /// Compare it against <see cref="Start"/> or use it as a bound; do not pin its value.
    /// </para>
    /// </remarks>
    public required Instant End { get; init; }

    /// <summary>The available ranges for each available schema in the dataset.</summary>
    /// <remarks>
    /// <c>schema</c> on the wire (<c>metadata.rs:316</c>) even though the value is a map of many —
    /// the second, and last, of this endpoint group's two renames.
    /// </remarks>
    [JsonPropertyName("schema")]
    public required IReadOnlyDictionary<Schema, DateTimeRange> RangeBySchema { get; init; }

    /// <summary>
    /// Narrows this range to just its top-level <see cref="Start"/> and <see cref="End"/>,
    /// discarding <see cref="RangeBySchema"/>.
    /// </summary>
    /// <remarks>
    /// Port of upstream's <c>impl From&lt;DatasetRange&gt; for DateTimeRange</c>
    /// (<c>metadata.rs:320-324</c>), which destructures <c>DatasetRange</c> and keeps only
    /// <c>start</c> and <c>end</c>. A conversion operator would let this happen implicitly at a
    /// call site that meant to keep the schema map; a named method makes the narrowing visible
    /// wherever it happens.
    /// </remarks>
    /// <returns>A <see cref="DateTimeRange"/> covering <see cref="Start"/> through <see cref="End"/>.</returns>
    public DateTimeRange ToDateTimeRange() => DateTimeRange.Between(Start, End);
}
