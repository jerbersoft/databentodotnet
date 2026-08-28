using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using DatabentoDotNet.Historical.Internal;

namespace DatabentoDotNet.Historical;

/// <summary>
/// The <c>batch.*</c> endpoints: jobs that produce files on Databento's side, and the transfer of
/// those files to this one.
/// </summary>
/// <remarks>
/// <para>
/// Reached through <see cref="HistoricalClient.Batch"/> rather than constructed. Port of upstream's
/// <c>BatchClient</c> (<c>batch.rs:33-36</c>), which holds a mutable borrow of the outer client;
/// this holds a reference, there being no borrow checker to satisfy.
/// </para>
/// <para>
/// <b>One method here costs money: <see cref="SubmitJobAsync"/>.</b> Listing, inspecting and
/// downloading are free, and downloading stays free however many times it is repeated — a job is
/// billed once, when it is submitted, and its files remain fetchable until
/// <see cref="BatchJob.ExpirationTimestamp"/>. Upstream marks exactly one of its methods with a
/// cost warning and #39 confirmed the asymmetry against the live API. So a download may be
/// retried, resumed after a crash, or run again from scratch without a second charge, which is
/// what makes the resumption logic below worth having rather than merely tidy.
/// </para>
/// <para>
/// <b>Batch versus <c>timeseries.get_range</c>.</b> Both move market data and they suit different
/// shapes of request: <see cref="TimeseriesClient.GetRangeAsync"/> streams a range this process
/// decodes as it arrives, while a batch job runs on Databento's side, may produce hundreds of
/// files, survives the process that asked for it, and can be fetched days later. Ranges too large
/// to wait on, or wanted as CSV or JSON, are batch jobs; anything a program consumes as it reads
/// is a range.
/// </para>
/// </remarks>
public sealed class BatchClient
{
    /// <summary>
    /// How many times a file transfer is retried after a mid-stream failure before it gives up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Upstream's <c>MAX_RETRIES</c> (<c>batch.rs:252</c>), and its counting rule with it: the
    /// counter measures <em>consecutive</em> failures, resetting whenever an attempt manages to
    /// write a byte before failing. Six failures in a row give up; six failures spread across an
    /// hour of otherwise healthy progress do not.
    /// </para>
    /// <para>
    /// <b>#39's own porting notes said the opposite of this, and reading upstream settled it.</b>
    /// The note read: "upstream's retry counter is shared across the whole file rather than reset
    /// on progress, so a long download that fails six times over an hour gives up; decide whether
    /// to keep that and write down which." Upstream does reset it —
    /// <c>if retries &gt; 0 { retries = 0; info!("Resumed download"); }</c> at
    /// <c>batch.rs:308-311</c>, on the first chunk to arrive after a retry — so there was no
    /// decision to make and nothing to depart from. The behaviour described in the note is the one
    /// neither library has.
    /// </para>
    /// </remarks>
    public const int MaximumRetries = 5;

    /// <summary>The protocol whose URL a download uses. The API also offers <c>ftp</c>.</summary>
    private const string HttpsProtocol = "https";

    /// <summary>The one checksum algorithm this library can compute.</summary>
    private const string Sha256Algorithm = "sha256";

    /// <summary>
    /// How much of a response body is moved per read. The <see cref="Stream.CopyToAsync(Stream)"/>
    /// default, chosen here explicitly because the loop is written out rather than delegated —
    /// every chunk has to pass through the hasher on its way to disk.
    /// </summary>
    private const int TransferBufferSize = 81_920;

    private readonly HistoricalClient _client;

    internal BatchClient(HistoricalClient client) => _client = client;

    /// <summary>Submits a batch job and returns the API's description of it.</summary>
    /// <remarks>
    /// <para>
    /// Port of upstream's <c>submit_job</c> (<c>batch.rs:67-93</c>). <b>This costs money</b>, and
    /// it commits to the whole range at once: unlike a stream, a submitted job cannot be stopped
    /// part-way to limit what is billed. Price it first with
    /// <see cref="MetadataClient.GetCostAsync"/>, handing it
    /// <see cref="SubmitJobParams.ToQuery"/> so the quote covers the request actually being sent.
    /// </para>
    /// <para>
    /// The returned job is answered immediately, long before it has run, so most of
    /// <see cref="BatchJob"/>'s optional properties are <see langword="null"/> at this point. Watch
    /// it with <see cref="GetJobDetailsAsync"/> until <see cref="BatchJob.State"/> reaches
    /// <see cref="JobState.Done"/>, then fetch its files with <see cref="DownloadAsync"/>.
    /// </para>
    /// </remarks>
    /// <param name="parameters">What to produce, over what range, in what encoding.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The submitted job.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="parameters"/> asks for a combination the API rejects — see
    /// <see cref="SubmitJobParams.ToFormParameters"/>.
    /// </exception>
    /// <exception cref="DatabentoApiException">The API answered with a non-success status.</exception>
    public async Task<BatchJob> SubmitJobAsync(
        SubmitJobParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        return await _client.SendJsonAsync(
            HttpMethod.Post,
            Slug("submit_job"),
            parameters.ToFormParameters(),
            BatchJson.Default.BatchJob,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists previous jobs, returning the id, state and receipt time of each.</summary>
    /// <remarks>
    /// <para>
    /// Port of upstream's <c>list_jobs</c> (<c>batch.rs:107-118</c>), which asks for the short form
    /// by sending <c>short=true</c> alongside the filters. Fetch the rest of a job with
    /// <see cref="GetJobDetailsAsync"/>.
    /// </para>
    /// <para>
    /// <b>Prefer this over <see cref="ListJobsFullAsync"/>, which the API is retiring</b> — see
    /// that method. The short form is the one that will keep working.
    /// </para>
    /// </remarks>
    /// <param name="parameters">
    /// The state and submission-time filters, or <see langword="null"/> to filter nothing.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>One summary per job, newest last, as the API orders them.</returns>
    /// <exception cref="DatabentoApiException">The API answered with a non-success status.</exception>
    public async Task<IReadOnlyList<BatchJobSummary>> ListJobsAsync(
        ListJobsParams? parameters = null,
        CancellationToken cancellationToken = default) =>
        await _client.SendJsonAsync(
            HttpMethod.Get,
            Slug("list_jobs"),
            ListJobsQuery(parameters, shortForm: true),
            BatchJson.Default.ListBatchJobSummary,
            cancellationToken).ConfigureAwait(false);

    /// <summary>Lists previous jobs, returning every field of each.</summary>
    /// <remarks>
    /// <para>
    /// Port of upstream's <c>list_jobs_full</c> (<c>batch.rs:131-137</c>), <b>including its
    /// deprecation</b>. Upstream deprecated it in 0.60.0 — the version this library ports — with
    /// the note that "the <c>batch.list_jobs</c> endpoint will stop returning full job details at a
    /// future date; use <c>list_jobs()</c> and <c>get_job_details()</c> instead". A doc comment
    /// alone would not carry that to a caller, so it is an attribute here and their compiler tells
    /// them.
    /// </para>
    /// <para>
    /// It is ported at all because the endpoint still answers today — #39 confirmed both forms
    /// against the live API — and a library that silently dropped a working endpoint would send a
    /// caller who needs it back to raw HTTP.
    /// </para>
    /// </remarks>
    /// <param name="parameters">
    /// The state and submission-time filters, or <see langword="null"/> to filter nothing.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>One full job per entry, newest last, as the API orders them.</returns>
    /// <exception cref="DatabentoApiException">The API answered with a non-success status.</exception>
    [Obsolete(
        "The batch.list_jobs endpoint will stop returning full job details at a future date. Use "
        + "ListJobsAsync and GetJobDetailsAsync instead. Deprecated upstream in databento-rs 0.60.0.")]
    public async Task<IReadOnlyList<BatchJob>> ListJobsFullAsync(
        ListJobsParams? parameters = null,
        CancellationToken cancellationToken = default) =>
        await _client.SendJsonAsync(
            HttpMethod.Get,
            Slug("list_jobs"),
            ListJobsQuery(parameters, shortForm: false),
            BatchJson.Default.ListBatchJob,
            cancellationToken).ConfigureAwait(false);

    /// <summary>Fetches everything the API knows about one job.</summary>
    /// <remarks>
    /// Port of upstream's <c>get_job_details</c> (<c>batch.rs:164-172</c>). This is how a submitted
    /// job is watched: poll it until <see cref="BatchJob.State"/> is <see cref="JobState.Done"/>,
    /// reading <see cref="BatchJob.Progress"/> on the way. It costs nothing to call.
    /// </remarks>
    /// <param name="jobId">The job's <see cref="BatchJob.Id"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The job.</returns>
    /// <exception cref="ArgumentException"><paramref name="jobId"/> is null or empty.</exception>
    /// <exception cref="DatabentoApiException">
    /// The API answered with a non-success status — <c>404</c> with
    /// <see cref="DatabentoApiException.Case"/> <c>batch_job_not_found</c> for an id this account
    /// does not have, whatever the id looks like. <b>That differs from
    /// <see cref="ListFilesAsync"/>, and #39 got it wrong before measuring it.</b> See that method.
    /// </exception>
    public async Task<BatchJob> GetJobDetailsAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        return await _client.SendJsonAsync(
            HttpMethod.Get,
            Slug("get_job_details"),
            [new KeyValuePair<string, string>("job_id", jobId)],
            BatchJson.Default.BatchJob,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists the files one job produced.</summary>
    /// <remarks>
    /// <para>
    /// Port of upstream's <c>list_files</c> (<c>batch.rs:179-187</c>). Free to call, and the only
    /// source of a file's download URL — <see cref="DownloadAsync"/> calls it rather than composing
    /// a URL from a filename, which is upstream's rule and #39's porting note both.
    /// </para>
    /// <para>
    /// <b>The list includes three files Databento packages with every job</b> — <c>manifest.json</c>,
    /// <c>metadata.json</c> and <c>condition.json</c> — beside the data itself, which #39 confirmed
    /// against four separate jobs. A full download fetches all of them.
    /// </para>
    /// <para>
    /// <b>This endpoint checks the shape of a job id and <see cref="GetJobDetailsAsync"/> does
    /// not</b>, which is a difference #39 assumed away and then measured. A malformed id —
    /// <c>NOPE-123</c> — is a <c>400</c> here carrying the API's <em>simple</em> error body, while
    /// a well-formed id for a job that does not exist is a <c>404</c> carrying the business one,
    /// <c>batch_job_not_found</c>. <see cref="GetJobDetailsAsync"/> answers <c>404</c> to both.
    /// </para>
    /// <para>
    /// The porting lesson is CLAUDE.md's, restated: probe the endpoint you are about to describe,
    /// not the one next to it. A single probe of <c>NOPE-123</c> against <em>this</em> endpoint had
    /// already been written into <see cref="GetJobDetailsAsync"/>'s documentation as its behaviour,
    /// and it was wrong — the same shape of mistake as #45, and caught the same way, by a test that
    /// calls the real API.
    /// </para>
    /// </remarks>
    /// <param name="jobId">The job's <see cref="BatchJob.Id"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>One description per file.</returns>
    /// <exception cref="ArgumentException"><paramref name="jobId"/> is null or empty.</exception>
    /// <exception cref="DatabentoApiException">
    /// The API answered with a non-success status — <c>400</c> for a malformed job id, <c>404</c>
    /// for a well-formed one this account does not have.
    /// </exception>
    public async Task<IReadOnlyList<BatchFileDescription>> ListFilesAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        return await _client.SendJsonAsync(
            HttpMethod.Get,
            Slug("list_files"),
            [new KeyValuePair<string, string>("job_id", jobId)],
            BatchJson.Default.ListBatchFileDescription,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads a job's files into <c>{OutputDirectory}/{JobId}/</c>, resuming any that are
    /// partly there already.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of upstream's <c>download</c> and <c>download_file</c> (<c>batch.rs:195-322</c>).
    /// Downloading costs nothing, so everything below is free to repeat.
    /// </para>
    /// <para>
    /// <b>Three cases decide what happens to a file already on disk</b>, and they are upstream's
    /// (<c>check_if_exists</c>, <c>batch.rs:325-368</c>). A file <em>shorter</em> than
    /// <see cref="BatchFileDescription.Size"/> resumes: the bytes already there are fed through the
    /// checksum and the request carries <c>Range: bytes=N-</c>. A file of <em>exactly</em> that size
    /// is left alone and no request is made at all. A file <em>longer</em> than it is an error — it
    /// is not this library's place to decide that somebody else's larger file is wrong and truncate
    /// it.
    /// </para>
    /// <para>
    /// <b>Two departures from upstream, both named on #39, and one defect found while porting.</b>
    /// </para>
    /// <para>
    /// <b>1. A checksum mismatch throws.</b> Upstream hashes the file and, on a mismatch, logs a
    /// warning and returns success (<c>verify_hash</c>, <c>batch.rs:370-383</c>) — so a corrupt
    /// download is reported by a log line the caller may not be listening to, and the path it
    /// returns points at bad data. This throws <see cref="InvalidDataException"/> naming the file.
    /// <b>The partial file is left on disk</b> rather than deleted: it is evidence, and deleting it
    /// would also delete a resumable transfer that failed for some reason other than corruption.
    /// A caller who wants a clean retry deletes it themselves.
    /// </para>
    /// <para>
    /// <b>2. Files transfer in parallel</b>, bounded by
    /// <see cref="DownloadParams.MaximumConcurrency"/>, where upstream's loop is sequential.
    /// Per-file behaviour is untouched; see that property.
    /// </para>
    /// <para>
    /// <b>3. The hasher is rebuilt on every attempt, and upstream's is not.</b> Upstream creates one
    /// hasher outside its retry loop and calls <c>check_if_exists</c> inside it, which re-reads the
    /// whole partial file into that same hasher — so after any retry the bytes already on disk have
    /// been hashed twice and the final digest cannot match. The bug is invisible upstream because a
    /// mismatch there is only a warning; it would be fatal here, where a mismatch throws, and it
    /// would fire on exactly the resumed transfers this issue exists to make work. Each attempt
    /// below gets its own hasher, seeded once with whatever is on disk.
    /// </para>
    /// <para>
    /// <b>A file that was already complete before the call is trusted on its size alone</b>, as it
    /// is upstream: no request is made for it and its checksum is not recomputed, which is what
    /// makes re-running a finished download free. A file this call <em>completed</em> is verified
    /// even when the transfer that completed it reported failure — see
    /// <see cref="DownloadFileAsync"/>, since a reset arriving after the final chunk is exactly
    /// that case.
    /// </para>
    /// <para>
    /// <b>A checksum this library cannot compute is skipped, not failed</b>, which is upstream's
    /// behaviour and the right one — an unrecognised algorithm means Databento has added one, not
    /// that the data is bad. It is logged, because it silently downgrades the guarantee point 1
    /// just strengthened. See <c>Internal/HistoricalLog.cs</c>.
    /// </para>
    /// </remarks>
    /// <param name="parameters">Which job, where to put it, and how many files at once.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>
    /// The path of every file written or already present, in the order
    /// <see cref="ListFilesAsync"/> reported them — or the single path, when
    /// <see cref="DownloadParams.Filename"/> named one.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <see cref="DownloadParams.OutputDirectory"/> holds a file where the job's directory belongs,
    /// or <see cref="DownloadParams.Filename"/> names a file the job does not have.
    /// </exception>
    /// <exception cref="InvalidDataException">A file's contents did not match its checksum.</exception>
    /// <exception cref="IOException">
    /// A file on disk is larger than the API says it should be, or the transfer failed more than
    /// <see cref="MaximumRetries"/> times in a row.
    /// </exception>
    /// <exception cref="DatabentoApiException">The API answered with a non-success status.</exception>
    public async Task<IReadOnlyList<string>> DownloadAsync(
        DownloadParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var jobDirectory = Path.Combine(parameters.OutputDirectory, parameters.JobId);
        if (File.Exists(jobDirectory))
        {
            throw new ArgumentException(
                $"'{jobDirectory}' exists and is a file, so the job's files cannot be written "
                + "there. Choose a different output directory.",
                nameof(parameters));
        }

        Directory.CreateDirectory(jobDirectory);

        var files = await ListFilesAsync(parameters.JobId, cancellationToken).ConfigureAwait(false);

        if (parameters.Filename is { } wanted)
        {
            var file = files.FirstOrDefault(f => string.Equals(f.Filename, wanted, StringComparison.Ordinal))
                ?? throw new ArgumentException(
                    $"The job '{parameters.JobId}' has no file named '{wanted}'. Its files are: "
                    + $"{string.Join(", ", files.Select(f => f.Filename))}.",
                    nameof(parameters));

            return [await DownloadFileAsync(file, jobDirectory, cancellationToken).ConfigureAwait(false)];
        }

        // Written by index rather than collected, so the returned order is the order the API
        // listed the files in however the transfers happen to interleave.
        var paths = new string[files.Count];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, files.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = parameters.MaximumConcurrency,
                CancellationToken = cancellationToken,
            },
            async (index, token) =>
                paths[index] = await DownloadFileAsync(files[index], jobDirectory, token).ConfigureAwait(false))
            .ConfigureAwait(false);

        return paths;
    }

    /// <summary>
    /// Transfers one file, resuming it if it is partly on disk and verifying it when it is whole.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Upstream's <c>download_file</c> (<c>batch.rs:245-322</c>) with <c>check_if_exists</c> folded
    /// into the top of the retry loop, which is where upstream calls it too. See
    /// <see cref="DownloadAsync"/> for the three size cases and the three departures.
    /// </para>
    /// <para>
    /// <b>Progress is measured as the file growing, not as bytes handed to a write call</b>, and
    /// the distinction is the difference between a working reset rule and a dead one. The obvious
    /// shape — a counter the transfer increments, read in the <c>catch</c> — cannot work: the
    /// transfer throws, so the counter is never assigned and the reset never fires, leaving a limit
    /// of six failures over the whole life of the file. That is precisely the behaviour #39's
    /// porting notes wrongly attributed to upstream. Comparing the file's length before and after
    /// the attempt has no such hole, and it asks the better question anyway: bytes that reached a
    /// buffer and not the disk are not progress a resumed transfer can build on.
    /// </para>
    /// <para>
    /// <b>It also terminates where upstream's literal rule does not.</b> Upstream resets on the
    /// first chunk received after a retry, so a server that ignores <c>Range</c> and always dies at
    /// the same offset resets upstream's counter forever — every attempt receives chunks, and every
    /// attempt starts again from zero. Here that attempt leaves the file no longer than it found
    /// it, so the counter advances and the download gives up.
    /// </para>
    /// </remarks>
    private async Task<string> DownloadFileAsync(
        BatchFileDescription file,
        string jobDirectory,
        CancellationToken cancellationToken)
    {
        var name = SafeFilename(file.Filename);
        var path = Path.Combine(jobDirectory, name);
        var expectedSize = (long)file.Size;
        var requestPath = RequestPathFor(file, name);
        var expectedHash = ExpectedHash(file, name);

        var retries = 0;
        var attempted = false;

        while (true)
        {
            var onDisk = ExistingLength(path);

            if (onDisk == expectedSize)
            {
                // Upstream's Ordering::Equal: already here, and no request is made at all. That is
                // what makes re-running a completed download free of network traffic as well as of
                // charge — and why a file that was already complete is trusted on its size, as it
                // is upstream. Re-hashing every byte of a job that is already on disk would make
                // the cheap case the expensive one.
                if (attempted)
                {
                    // Unless this call is what completed it. A transfer can deliver the last byte
                    // and still fail — a connection reset after the final chunk does exactly that —
                    // and arriving here from that is the one route by which bytes this library
                    // wrote would otherwise go unverified. See DownloadAsync's first departure:
                    // a checksum mismatch throws, and a hole the retry path can drive through
                    // would leave that guarantee true only of transfers that ended tidily.
                    await VerifyOnDiskAsync(path, name, expectedHash, cancellationToken).ConfigureAwait(false);
                }

                return path;
            }

            if (onDisk > expectedSize)
            {
                throw new IOException(
                    $"The batch file '{name}' is already on disk with {onDisk} bytes, which is more "
                    + $"than the {expectedSize} the API says it has. Something other than this "
                    + "download wrote it; it is left alone rather than truncated.");
            }

            var before = Math.Max(onDisk, 0);
            attempted = true;

            try
            {
                await TransferAsync(requestPath, path, name, onDisk, expectedHash, cancellationToken)
                    .ConfigureAwait(false);

                return path;
            }
            catch (Exception e) when (IsTransient(e, cancellationToken))
            {
                // Upstream's counting rule: an attempt that got somewhere clears the count, so the
                // limit is on consecutive failures rather than on failures over the file's life.
                // Progress is measured off disk rather than from a byte counter, for the reason
                // this method's remarks give.
                var after = ExistingLength(path);
                if (after > before)
                {
                    retries = 0;
                }

                if (retries >= MaximumRetries)
                {
                    throw;
                }

                retries++;
                HistoricalLog.DownloadRetry(_client.Logger, e, name, after, retries, MaximumRetries);
            }
        }
    }

    /// <summary>One attempt at a transfer: request, write, hash, verify.</summary>
    /// <remarks>
    /// Returns nothing. Whether the attempt made progress is the caller's question and it answers
    /// it from the file itself — see <see cref="DownloadFileAsync"/>.
    /// </remarks>
    private async Task TransferAsync(
        string requestPath,
        string path,
        string name,
        long onDisk,
        string? expectedHash,
        CancellationToken cancellationToken)
    {
        var headers = onDisk > 0
            ? new[]
            {
                new KeyValuePair<string, string>(
                    "Range", $"bytes={onDisk.ToString(CultureInfo.InvariantCulture)}-"),
            }
            : null;

        using var response = await _client
            .GetPathAsync(requestPath, headers, cancellationToken)
            .ConfigureAwait(false);

        // A server may answer a Range request with the whole file — the header is a request, not a
        // requirement — and appending to a partial file in that case silently produces a longer,
        // corrupt one. Upstream opens its output in append mode before it looks at the status and
        // has no equivalent check.
        var resuming = onDisk > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (onDisk > 0 && !resuming)
        {
            HistoricalLog.ResumeNotHonoured(_client.Logger, name, onDisk);
            onDisk = 0;
        }

        using var hasher = expectedHash is null
            ? null
            : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        if (hasher is not null && onDisk > 0)
        {
            await HashExistingAsync(path, onDisk, hasher, cancellationToken).ConfigureAwait(false);
        }

        var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (body.ConfigureAwait(false))
        {
            var output = new FileStream(
                path,
                onDisk > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                TransferBufferSize,
                useAsync: true);

            await using (output.ConfigureAwait(false))
            {
                var buffer = new byte[TransferBufferSize];
                int read;
                while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hasher?.AppendData(buffer, 0, read);
                }
            }
        }

        if (hasher is not null && expectedHash is not null)
        {
            var actual = Convert.ToHexStringLower(hasher.GetHashAndReset());
            if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The batch file '{name}' does not match the checksum the API published for it: "
                    + $"expected sha256 {expectedHash}, computed {actual}. The file is left at "
                    + $"'{path}' for inspection rather than deleted.");
            }
        }
    }

    /// <summary>
    /// Hashes a whole file on disk and throws if it does not match
    /// <paramref name="expectedHash"/>.
    /// </summary>
    /// <remarks>
    /// Reached only when a transfer completed the file but reported failure, which a connection
    /// reset after the last chunk does. It does nothing when the checksum is one this library
    /// cannot compute, which is the same skip <see cref="TransferAsync"/> makes.
    /// </remarks>
    private static async Task VerifyOnDiskAsync(
        string path,
        string name,
        string? expectedHash,
        CancellationToken cancellationToken)
    {
        if (expectedHash is null)
        {
            return;
        }

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await HashExistingAsync(path, ExistingLength(path), hasher, cancellationToken).ConfigureAwait(false);

        var actual = Convert.ToHexStringLower(hasher.GetHashAndReset());
        if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The batch file '{name}' does not match the checksum the API published for it: "
                + $"expected sha256 {expectedHash}, computed {actual}. The file is left at "
                + $"'{path}' for inspection rather than deleted.");
        }
    }

    /// <summary>
    /// Feeds the <paramref name="length"/> bytes already on disk through
    /// <paramref name="hasher"/>, so a resumed transfer's digest covers the whole file.
    /// </summary>
    /// <remarks>
    /// Upstream's <c>check_if_exists</c> does this too (<c>batch.rs:344-355</c>) — into a hasher
    /// that already holds those bytes from the failed attempt. See <see cref="DownloadAsync"/>,
    /// point 3.
    /// </remarks>
    private static async Task HashExistingAsync(
        string path,
        long length,
        IncrementalHash hasher,
        CancellationToken cancellationToken)
    {
        var existing = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, TransferBufferSize, useAsync: true);

        await using (existing.ConfigureAwait(false))
        {
            var buffer = new byte[TransferBufferSize];
            var remaining = length;

            while (remaining > 0)
            {
                var wanted = (int)Math.Min(buffer.Length, remaining);
                var read = await existing.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    // The file shrank between being measured and being read. Whatever did that,
                    // continuing would hash fewer bytes than the Range header already asked the
                    // server to skip, and the mismatch would be reported as corruption.
                    throw new IOException(
                        $"'{path}' was {length} bytes when the transfer resumed and is shorter now. "
                        + "Something else is writing to it.");
                }

                hasher.AppendData(buffer, 0, read);
                remaining -= read;
            }
        }
    }

    /// <summary>The file's length, or <c>-1</c> when it is not there.</summary>
    private static long ExistingLength(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length : -1;
    }

    /// <summary>
    /// Whether <paramref name="e"/> is the kind of mid-transfer failure a retry can recover from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A dropped connection surfaces as an <see cref="IOException"/> or as the
    /// <see cref="HttpRequestException"/> wrapping one — which is not itself an
    /// <see cref="IOException"/>, so both have to be named. Everything else is left alone: a
    /// <see cref="DatabentoApiException"/> means the API refused the request and will refuse it
    /// again, and a checksum failure means the bytes arrived and were wrong, which retrying would
    /// only re-download.
    /// </para>
    /// <para>
    /// <b>The checksum failure needs no clause of its own, and that was measured rather than
    /// assumed.</b> <see cref="InvalidDataException"/> derives from
    /// <see cref="SystemException"/>, not from <see cref="IOException"/> — its name and namespace
    /// both suggest otherwise — so it falls outside the test above already. An explicit exclusion
    /// would be dead code that read as load-bearing.
    /// </para>
    /// <para>
    /// Cancellation is excluded explicitly, because a token firing mid-read can surface as an
    /// <see cref="IOException"/> rather than as an <see cref="OperationCanceledException"/>, and
    /// retrying a cancelled download would ignore the caller.
    /// </para>
    /// </remarks>
    private static bool IsTransient(Exception e, CancellationToken cancellationToken) =>
        e is IOException or HttpRequestException && !cancellationToken.IsCancellationRequested;

    /// <summary>
    /// The path to fetch a file from: the <c>https</c> URL's path, with its host discarded.
    /// </summary>
    /// <remarks>
    /// Upstream's <c>urls.get("https")</c> then <c>get_with_path(url.path())</c>
    /// (<c>batch.rs:216-221</c>, <c>client.rs:128-137</c>). See
    /// <see cref="HistoricalClient.GetPathAsync"/> for why only the path is kept, and
    /// <see cref="BatchFileDescription.Urls"/> for what #39 measured about the two hosts.
    /// </remarks>
    private static string RequestPathFor(BatchFileDescription file, string name)
    {
        if (!file.Urls.TryGetValue(HttpsProtocol, out var url) || string.IsNullOrEmpty(url))
        {
            throw new InvalidOperationException(
                $"The batch file '{name}' has no https download URL. The API offered: "
                + $"{string.Join(", ", file.Urls.Keys)}.");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            throw new InvalidOperationException(
                $"The batch file '{name}' has an https URL this library cannot parse.");
        }

        // AbsolutePath is already percent-encoded, so it composes onto the base URL without being
        // escaped again — which would turn every %20 into %2520.
        return parsed.AbsolutePath;
    }

    /// <summary>
    /// The expected digest, or <see langword="null"/> when the algorithm is one this library
    /// cannot compute.
    /// </summary>
    /// <remarks>
    /// Upstream splits on the first colon and treats an unknown algorithm as a reason to skip
    /// verification rather than to fail (<c>batch.rs:255-266</c>); a hash with no colon at all is
    /// an error in both. The skip is logged here — see <see cref="DownloadAsync"/> for why.
    /// </remarks>
    private string? ExpectedHash(BatchFileDescription file, string name)
    {
        var separator = file.Hash.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0)
        {
            throw new InvalidOperationException(
                $"The batch file '{name}' has a checksum this library cannot read. A checksum is "
                + "'{algorithm}:{hex}'.");
        }

        var algorithm = file.Hash[..separator];
        if (!string.Equals(algorithm, Sha256Algorithm, StringComparison.Ordinal))
        {
            HistoricalLog.ChecksumSkipped(_client.Logger, name, algorithm);
            return null;
        }

        return file.Hash[(separator + 1)..];
    }

    /// <summary>
    /// Checks that a server-supplied filename names a file inside the job's directory and nothing
    /// else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not a port — upstream joins the name onto the output directory unchecked</b>
    /// (<c>batch.rs:227-232</c>). The name is the only value in this library that a caller neither
    /// wrote nor chose: it arrives in a response body, and <see cref="Path.Combine(string, string)"/>
    /// with a rooted second argument discards the first entirely, so <c>/etc/hosts</c> would be
    /// written to <c>/etc/hosts</c> rather than under the job's directory. A relative name with
    /// <c>..</c> segments escapes it just as effectively.
    /// </para>
    /// <para>
    /// This is not a live vulnerability — the names come from Databento — but it is one response
    /// body away from being one, and the check costs a comparison per file.
    /// <see cref="HistoricalClient.PathFor"/>'s own remarks single out a batch file's path as "the
    /// one place a caller passes something it did not write".
    /// </para>
    /// </remarks>
    private static string SafeFilename(string filename)
    {
        if (string.IsNullOrEmpty(filename)
            || Path.IsPathRooted(filename)
            || filename.Contains('/', StringComparison.Ordinal)
            || filename.Contains('\\', StringComparison.Ordinal)
            || filename == "."
            || filename == "..")
        {
            throw new InvalidOperationException(
                $"The API named a batch file '{filename}', which is not a plain file name. It is "
                + "refused rather than written, because a name carrying a path would decide where "
                + "the file lands.");
        }

        return filename;
    }

    /// <summary>
    /// The query <c>batch.list_jobs</c> takes: the caller's filters, then the <c>short</c> flag
    /// that picks which response shape comes back.
    /// </summary>
    /// <remarks>
    /// The flag is added here rather than on <see cref="ListJobsParams"/> because it is not a
    /// filter — see <see cref="ListJobsParams.ToQueryParameters"/>. Upstream appends it the same
    /// way, on <c>list_jobs</c> only (<c>batch.rs:112-115</c>); <c>list_jobs_full</c> sends no
    /// <c>short</c> at all, and #39 confirmed omitting it returns the full form.
    /// </remarks>
    private static List<KeyValuePair<string, string>> ListJobsQuery(ListJobsParams? parameters, bool shortForm)
    {
        var query = new List<KeyValuePair<string, string>>(3);

        if (parameters is not null)
        {
            query.AddRange(parameters.ToQueryParameters());
        }

        if (shortForm)
        {
            query.Add(new KeyValuePair<string, string>("short", "true"));
        }

        return query;
    }

    /// <summary>
    /// The endpoint group's slug prefix, built the way <c>MetadataClient.Slug</c> and
    /// <c>SymbologyClient.Slug</c> build theirs (<c>batch.rs:329-337</c>).
    /// </summary>
    private static string Slug(string endpoint) => $"batch.{endpoint}";
}
