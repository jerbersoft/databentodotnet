namespace DatabentoDotNet.Historical;

/// <summary>The time interval a batch job's output is split into separate files at.</summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>SplitDuration</c> (<c>batch.rs:398-410</c>), whose default is
/// <see cref="Day"/> — so <see cref="Day"/> is the zero value here, and <c>default</c> agrees with
/// upstream's <c>#[default]</c> rather than quietly meaning "do not split".
/// </para>
/// <para>
/// <b><see cref="None"/> arrives as JSON <c>null</c>, never as the string <c>"none"</c>.</b>
/// Upstream discovered this and handles it in a hand-written deserializer
/// (<c>batch.rs:672-681</c>): "The API returns <c>null</c> instead of <c>"none"</c> for no
/// time-based splitting". #39's probe of <c>batch.get_job_details</c> confirms it — a job submitted
/// with no split carries <c>"split_duration":null</c> — so <see cref="BatchWireStrings"/> renders
/// <c>none</c> for a request and <c>Json.SplitDurationJsonConverter</c> reads <c>null</c> back.
/// </para>
/// </remarks>
public enum SplitDuration
{
    /// <summary>One file per day. The API's default, and this enum's.</summary>
    Day,

    /// <summary>One file per week, a week beginning on Sunday UTC.</summary>
    Week,

    /// <summary>One file per month.</summary>
    Month,

    /// <summary>One file per year.</summary>
    Year,

    /// <summary>No splitting by time. Comes back from the API as <c>null</c>.</summary>
    None,
}
