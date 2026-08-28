namespace DatabentoDotNet.Historical;

/// <summary>The stage a batch job has reached.</summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>JobState</c> (<c>batch.rs:422-432</c>), <b>widened from four members to
/// seven</b> — and the three extra ones are not speculative. #39 asked the API what it accepts by
/// sending <c>batch.list_jobs?states=bogus</c>, and its <c>400</c> enumerates them:
/// <c>['received', 'queued', 'processing', 'finalizing', 'done', 'expired', 'purged']</c>.
/// Upstream models <c>queued</c>, <c>processing</c>, <c>done</c> and <c>expired</c> only.
/// </para>
/// <para>
/// <b>The three it is missing are a live defect there, not a cosmetic gap.</b> Upstream's
/// <c>JobState</c> derives <see langword="its"/> deserializer from <c>FromStr</c>, which errors on
/// any spelling it does not know — and <c>batch.list_jobs</c> returns <em>every</em> job the
/// account has. So one job sitting in <c>received</c> or <c>finalizing</c>, which is where a job
/// spends its first seconds, fails the deserialization of the whole listing rather than of that
/// one element. Porting the four faithfully would have reproduced an outage that happens to be
/// invisible in a test suite whose fixtures are all <c>done</c>.
/// </para>
/// <para>
/// The order is the API's own, which is also the lifecycle order: a job is received, queued,
/// processed, finalized and done, and later expires and is purged. Nothing on the wire carries the
/// numeric value — <see cref="BatchWireStrings"/> holds the only spellings that matter — so the
/// ordering is documentation rather than protocol.
/// </para>
/// </remarks>
public enum JobState
{
    /// <summary>The API has accepted the job but has not yet queued it for processing.</summary>
    Received,

    /// <summary>The job is queued for processing.</summary>
    Queued,

    /// <summary>The job is being processed.</summary>
    Processing,

    /// <summary>The data is ready and the job's files are being packaged.</summary>
    Finalizing,

    /// <summary>The job has finished and its files are available to download.</summary>
    Done,

    /// <summary>
    /// The job has passed its <see cref="BatchJob.ExpirationTimestamp"/> and its files are no
    /// longer downloadable.
    /// </summary>
    Expired,

    /// <summary>The job's files have been deleted.</summary>
    Purged,
}
