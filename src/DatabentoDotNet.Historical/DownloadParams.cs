namespace DatabentoDotNet.Historical;

/// <summary>The parameter set <see cref="BatchClient.DownloadAsync"/> takes.</summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>DownloadParams</c> (<c>batch.rs:620-630</c>), plus
/// <see cref="MaximumConcurrency"/>, which upstream has no equivalent of because its
/// <c>download</c> transfers files one at a time.
/// </para>
/// <para>
/// <b>Downloading costs nothing.</b> A batch job is billed when it is submitted, and its files stay
/// fetchable until <see cref="BatchJob.ExpirationTimestamp"/> — upstream marks
/// <c>submit_job</c> with a cost warning and marks <c>download</c> with none, and #39 confirmed the
/// asymmetry against the live API. So a download can be retried, resumed, or run again from
/// scratch without a second charge.
/// </para>
/// </remarks>
public sealed record DownloadParams
{
    private readonly int _maximumConcurrency = DefaultMaximumConcurrency;

    /// <summary>How many files transfer at once unless <see cref="MaximumConcurrency"/> says otherwise.</summary>
    public const int DefaultMaximumConcurrency = 4;

    /// <summary>The directory to write into. The job's own directory is created inside it.</summary>
    /// <remarks>
    /// Files land in <c>{OutputDirectory}/{JobId}/{filename}</c>, as upstream does
    /// (<c>batch.rs:196</c>). The job directory is created if it is absent; an existing
    /// <em>file</em> at that path is an error rather than something to overwrite.
    /// </remarks>
    public required string OutputDirectory { get; init; }

    /// <summary>The job to download, by <see cref="BatchJob.Id"/>.</summary>
    public required string JobId { get; init; }

    /// <summary>
    /// One file to download by name, or <see langword="null"/> for every file the job produced.
    /// </summary>
    /// <remarks>
    /// The name must be one <see cref="BatchClient.ListFilesAsync"/> reports for this job —
    /// <see cref="BatchClient.DownloadAsync"/> looks it up rather than constructing a URL from it,
    /// so a name that is not in the job is an error and not a 404 much later. Remember that a job's
    /// files include the three Databento packages with every job: <c>manifest.json</c>,
    /// <c>metadata.json</c> and <c>condition.json</c>.
    /// </remarks>
    public string? Filename { get; init; }

    /// <summary>How many files to transfer at once. Defaults to <see cref="DefaultMaximumConcurrency"/>.</summary>
    /// <remarks>
    /// <para>
    /// <b>Parallel transfer is a departure from upstream</b>, whose <c>download</c> loops over the
    /// file list one file at a time (<c>batch.rs:227-243</c>). ROADMAP.md §5 asks for it, and a
    /// batch job routinely splits into hundreds of files — a day-split year of data is 250-odd —
    /// where sequential transfer spends most of its time waiting rather than reading.
    /// </para>
    /// <para>
    /// <b>The per-file logic is unchanged by it.</b> Each file still resumes, skips or fails on its
    /// own exactly as upstream's does; only how many are in flight differs. That is what keeps the
    /// departure reviewable — see <see cref="BatchClient.DownloadAsync"/>.
    /// </para>
    /// <para>
    /// The default is deliberately small. Every file of a job comes from one host, so the useful
    /// range is "more than one" rather than "as many as there are files", and a large bound turns a
    /// job with hundreds of files into hundreds of simultaneous connections to Databento. Set it to
    /// <c>1</c> for upstream's exact behaviour.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than one.</exception>
    public int MaximumConcurrency
    {
        get => _maximumConcurrency;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _maximumConcurrency = value;
        }
    }
}
