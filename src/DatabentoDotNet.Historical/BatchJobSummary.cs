using System.Text.Json.Serialization;
using NodaTime;

namespace DatabentoDotNet.Historical;

/// <summary>The three fields <see cref="BatchClient.ListJobsAsync"/> returns for each job.</summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>BatchJobShort</c> (<c>batch.rs:583-592</c>), named for what it is rather
/// than for how much of it there is. Fetch the rest with
/// <see cref="BatchClient.GetJobDetailsAsync"/>.
/// </para>
/// <para>
/// <b>This shape is the API's future, not a convenience.</b> Upstream deprecated
/// <c>list_jobs_full</c> in 0.60.0 with the note that "the <c>batch.list_jobs</c> endpoint will
/// stop returning full job details at a future date" (<c>batch.rs:125-129</c>) — so the full
/// listing is the form that is going away and this is the one that stays. #39 confirmed both still
/// answer today: <c>short=true</c> returns exactly these three fields, and omitting it returns the
/// whole job.
/// </para>
/// </remarks>
public sealed record BatchJobSummary
{
    /// <summary>The job's unique identifier — <c>XNAS-20260825-6T3F5G5TYH</c>.</summary>
    public required string Id { get; init; }

    /// <summary>How far the job has got.</summary>
    public required JobState State { get; init; }

    /// <summary>When Databento received the job.</summary>
    /// <remarks>
    /// <c>ts_received</c> on the wire. Also the value
    /// <see cref="ListJobsParams.Since"/> filters on, so a listing can be continued from the last
    /// summary it returned.
    /// </remarks>
    [JsonPropertyName("ts_received")]
    public required Instant ReceivedTimestamp { get; init; }
}
