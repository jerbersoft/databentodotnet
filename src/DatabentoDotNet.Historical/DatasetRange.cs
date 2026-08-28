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
