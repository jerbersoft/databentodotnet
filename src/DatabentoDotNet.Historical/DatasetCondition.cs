namespace DatabentoDotNet.Historical;

/// <summary>The condition of a dataset on a single day.</summary>
/// <remarks>
/// Port of upstream's <c>DatasetCondition</c>
/// (<c>databento-rs/src/historical/metadata.rs:216-228</c>). Received from
/// <c>metadata.get_dataset_condition</c> and never sent.
/// </remarks>
public enum DatasetCondition
{
    /// <summary>The data is available with no known issues.</summary>
    Available,

    /// <summary>
    /// The data is available, but there may be missing data or other correctness issues.
    /// </summary>
    Degraded,

    /// <summary>The data is not yet available, but may be available soon.</summary>
    Pending,

    /// <summary>The data is not available.</summary>
    Missing,
}
