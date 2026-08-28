using System.Text.Json.Serialization;
using DatabentoDotNet.Dbn;
using NodaTime;

namespace DatabentoDotNet.Historical;

/// <summary>Everything the API knows about one batch job.</summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>BatchJob</c> (<c>batch.rs:502-576</c>). Returned by
/// <see cref="BatchClient.SubmitJobAsync"/>, <see cref="BatchClient.GetJobDetailsAsync"/> and the
/// deprecated <see cref="BatchClient.ListJobsFullAsync"/>. It is both the echo of what was asked
/// for and the record of what happened: the parameters come back beside the sizes, the timings and
/// the state.
/// </para>
/// <para>
/// <b>Two fields here are not in upstream's struct, and both were found by probing rather than by
/// reading.</b> <see cref="BillId"/> and <see cref="Packaging"/> appear in every response
/// <c>batch.list_jobs</c> and <c>batch.get_job_details</c> returned to #39, and upstream models
/// neither. #39's Definition of done asks for them anyway — "a field silently dropped in
/// deserialisation is invisible until someone needs it" — and a serde struct is exactly where such
/// a drop hides, because unmatched properties are skipped without complaint in both languages.
/// </para>
/// <para>
/// <b><see cref="Start"/> and <see cref="End"/> are two properties rather than a
/// <see cref="DateTimeRange"/>, matching the wire and matching
/// <see cref="DatasetRange"/>.</b> They arrive as sibling JSON properties, not as a nested object,
/// so a single range property would need a converter over the whole job.
/// <see cref="ToDateTimeRange"/> is the narrowing, named for the same reason
/// <see cref="DatasetRange.ToDateTimeRange"/> is.
/// </para>
/// <para>
/// <b>The optional properties are optional because the API has not decided them yet</b>, not
/// because they are rare. A job is answered the moment it is submitted, long before it has a cost,
/// a record count or a finish time; every <see langword="null"/> below reads as "not yet" rather
/// than "never".
/// </para>
/// </remarks>
public sealed record BatchJob
{
    /// <summary>The job's unique identifier — <c>XNAS-20260825-6T3F5G5TYH</c>.</summary>
    /// <remarks>
    /// What <see cref="BatchClient.GetJobDetailsAsync"/>, <see cref="BatchClient.ListFilesAsync"/>
    /// and <see cref="DownloadParams.JobId"/> all name a job by, and the directory a download is
    /// written into.
    /// </remarks>
    public required string Id { get; init; }

    /// <summary>The identifier of the user who submitted the job.</summary>
    public string? UserId { get; init; }

    /// <summary>The identifier of the bill this job was charged to, or <see langword="null"/>.</summary>
    /// <remarks>
    /// Not modelled by upstream. Every job #39 saw carried it as <c>null</c> — including jobs that
    /// had completed and been priced — so its populated spelling is unmeasured and it is typed as
    /// the string the wire would carry rather than as a guess at more structure.
    /// </remarks>
    public string? BillId { get; init; }

    /// <summary>What the job cost in US dollars, or <see langword="null"/> until it is priced.</summary>
    /// <remarks>
    /// <see langword="decimal"/> rather than upstream's <c>f64</c>, matching
    /// <see cref="MetadataClient.GetCostAsync"/>: a price is a decimal quantity, and binary floating
    /// point cannot represent one exactly.
    /// </remarks>
    public decimal? CostUsd { get; init; }

    /// <summary>The dataset code the job requested.</summary>
    public required string Dataset { get; init; }

    /// <summary>The symbols the job requested.</summary>
    /// <remarks>
    /// The API echoes this as a bare string, comma-joined for more than one, and as
    /// <see cref="DatabentoDotNet.Symbols.AllWireValue"/> for the whole dataset. See
    /// <see cref="Json.SymbolsJsonConverter"/>, which reads all four shapes the field is documented
    /// to take.
    /// </remarks>
    public required Symbols Symbols { get; init; }

    /// <summary>The symbology <see cref="Symbols"/> was expressed in.</summary>
    public required SType StypeIn { get; init; }

    /// <summary>The symbology the job's records name instruments in.</summary>
    public required SType StypeOut { get; init; }

    /// <summary>The record schema the job requested.</summary>
    public required Schema Schema { get; init; }

    /// <summary>The inclusive start of the requested range.</summary>
    public required Instant Start { get; init; }

    /// <summary>The exclusive end of the requested range.</summary>
    public required Instant End { get; init; }

    /// <summary>The maximum number of records the job requested, or <see langword="null"/> for no limit.</summary>
    public ulong? Limit { get; init; }

    /// <summary>The encoding the job's files are written in.</summary>
    public required Encoding Encoding { get; init; }

    /// <summary>The compression the job's files are written with.</summary>
    /// <remarks>
    /// Arrives as JSON <c>null</c> rather than <c>"none"</c> when there is none; see
    /// <see cref="Json.CompressionJsonConverter"/>.
    /// </remarks>
    public required Compression Compression { get; init; }

    /// <summary>Whether prices are written at their true scale rather than as fixed-precision integers.</summary>
    public required bool PrettyPx { get; init; }

    /// <summary>Whether timestamps are written as ISO 8601 strings rather than as nanoseconds.</summary>
    public required bool PrettyTs { get; init; }

    /// <summary>Whether each text-encoded record carries a symbol field.</summary>
    public required bool MapSymbols { get; init; }

    /// <summary>Whether the job's output is split into one file per raw symbol.</summary>
    public required bool SplitSymbols { get; init; }

    /// <summary>The interval the job's output is split into separate files at.</summary>
    /// <remarks>
    /// Arrives as JSON <c>null</c> rather than <c>"none"</c> when there is no time-based split; see
    /// <see cref="Json.SplitDurationJsonConverter"/>.
    /// </remarks>
    public required SplitDuration SplitDuration { get; init; }

    /// <summary>The size in bytes each file is split at, or <see langword="null"/> for no size split.</summary>
    public ulong? SplitSize { get; init; }

    /// <summary>How the job's files are packaged for delivery, or <see langword="null"/> for none.</summary>
    /// <remarks>
    /// Not modelled by upstream, and not sent by <see cref="SubmitJobParams"/> either — upstream
    /// posts no <c>packaging</c> field, so nothing this library submits can populate it. Captured
    /// because the API returns it on every job. Like <see cref="BillId"/>, every value #39 measured
    /// was <c>null</c>, so its populated spelling is unmeasured and it is typed accordingly.
    /// </remarks>
    public string? Packaging { get; init; }

    /// <summary>How the job's files are delivered.</summary>
    public required Delivery Delivery { get; init; }

    /// <summary>How many records the job produced, or <see langword="null"/> until it has run.</summary>
    public ulong? RecordCount { get; init; }

    /// <summary>The size in bytes the job was billed on, or <see langword="null"/> until it has run.</summary>
    /// <remarks>
    /// The size of the raw binary data behind the job, which is what
    /// <see cref="MetadataClient.GetBillableSizeAsync"/> quotes in advance — not the size of the
    /// files produced, which is <see cref="ActualSize"/>.
    /// </remarks>
    public ulong? BilledSize { get; init; }

    /// <summary>The total size in bytes of the job's output, or <see langword="null"/> until it has run.</summary>
    public ulong? ActualSize { get; init; }

    /// <summary>
    /// The total size in bytes of the job's output including its metadata files, or
    /// <see langword="null"/> until it has run.
    /// </summary>
    /// <remarks>
    /// The number to expect a full <see cref="BatchClient.DownloadAsync"/> to write, since a
    /// download takes every file <see cref="BatchClient.ListFilesAsync"/> reports and that includes
    /// the packaged <c>manifest.json</c>, <c>metadata.json</c> and <c>condition.json</c>.
    /// </remarks>
    public ulong? PackageSize { get; init; }

    /// <summary>How far the job has got.</summary>
    public required JobState State { get; init; }

    /// <summary>When Databento received the job.</summary>
    /// <remarks><c>ts_received</c> on the wire, and what <see cref="ListJobsParams.Since"/> filters on.</remarks>
    [JsonPropertyName("ts_received")]
    public required Instant ReceivedTimestamp { get; init; }

    /// <summary>When the job was queued, or <see langword="null"/> if it has not been yet.</summary>
    [JsonPropertyName("ts_queued")]
    public Instant? QueuedTimestamp { get; init; }

    /// <summary>When processing began, or <see langword="null"/> if it has not yet.</summary>
    [JsonPropertyName("ts_process_start")]
    public Instant? ProcessStartTimestamp { get; init; }

    /// <summary>When processing finished, or <see langword="null"/> if it has not yet.</summary>
    [JsonPropertyName("ts_process_done")]
    public Instant? ProcessDoneTimestamp { get; init; }

    /// <summary>When the job's files stop being downloadable, or <see langword="null"/> if not yet known.</summary>
    /// <remarks>
    /// After this instant the job's state becomes <see cref="JobState.Expired"/> and its files can
    /// no longer be fetched. #39 measured roughly thirty days from submission.
    /// </remarks>
    [JsonPropertyName("ts_expiration")]
    public Instant? ExpirationTimestamp { get; init; }

    /// <summary>How far through processing the job is, from 0 to 100, or <see langword="null"/>.</summary>
    /// <remarks>
    /// Upstream carries <c>#[serde(default)]</c> here (<c>batch.rs:574-575</c>) because a
    /// just-submitted job's response omits the field entirely rather than sending <c>null</c>. That
    /// is why this property is not <see langword="required"/>: an absent field and a
    /// <see langword="null"/> one must both read as "not yet", and only a non-required property
    /// does that.
    /// </remarks>
    public byte? Progress { get; init; }

    /// <summary>
    /// Narrows <see cref="Start"/> and <see cref="End"/> into the range type the rest of this
    /// library takes.
    /// </summary>
    /// <remarks>
    /// Named rather than implicit, for the reason <see cref="DatasetRange.ToDateTimeRange"/> gives.
    /// The result is the range the job was submitted with, so it can be handed straight to
    /// <see cref="MetadataClient.GetCostAsync"/> to ask what the same request would cost again.
    /// </remarks>
    /// <returns>The requested range: inclusive start, exclusive end.</returns>
    public DateTimeRange ToDateTimeRange() => DateTimeRange.Between(Start, End);
}
