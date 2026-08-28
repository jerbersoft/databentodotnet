using NodaTime;

namespace DatabentoDotNet.Historical;

/// <summary>The condition of a dataset on a particular day.</summary>
/// <remarks>
/// Port of upstream's <c>DatasetConditionDetail</c>
/// (<c>databento-rs/src/historical/metadata.rs:292-304</c>). Returned by
/// <c>metadata.get_dataset_condition</c>, one entry for every date in the requested range —
/// including a date with no data at all, which is the only way a caller can tell "the range is
/// complete and empty" apart from "the response silently dropped a day".
/// </remarks>
public sealed record DatasetConditionDetail
{
    /// <summary>The day of the described data.</summary>
    public required LocalDate Date { get; init; }

    /// <summary>
    /// The condition code describing the quality and availability of the data on
    /// <see cref="Date"/>.
    /// </summary>
    public required DatasetCondition Condition { get; init; }

    /// <summary>
    /// The date when any schema in the dataset on <see cref="Date"/> was last generated or
    /// modified, or <see langword="null"/> when no such date is known.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one property on this record that is not <see langword="required"/>. Upstream's doc
    /// comment says this "will be <c>None</c> when <c>condition</c> is <c>Missing</c>"
    /// (<c>metadata.rs:300-301</c>) — but that is a description of what the API happens to send,
    /// not a constraint this type enforces. On the wire <c>last_modified_date</c> is an
    /// <c>Option</c> regardless of <see cref="Condition"/>
    /// (<c>#[serde(deserialize_with = "deserialize_opt_date")]</c>, <c>metadata.rs:302-303</c>), so
    /// a <see langword="null"/> arriving on an <see cref="DatasetCondition.Available"/> day
    /// deserializes here exactly as readily as one on a <see cref="DatasetCondition.Missing"/> day
    /// — neither this property nor its converter rejects that combination.
    /// </para>
    /// </remarks>
    public LocalDate? LastModifiedDate { get; init; }
}
