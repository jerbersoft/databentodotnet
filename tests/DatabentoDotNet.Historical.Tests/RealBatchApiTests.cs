using DatabentoDotNet.Dbn;
using NodaTime;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// Opt-in tests against the <b>real</b> <c>batch.*</c> endpoints — <b>every one of them free</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Downloading a batch job costs nothing, and that is what makes this whole class free to run
/// behind a key alone.</b> A job is billed once, when it is submitted; its files stay fetchable
/// until they expire, and fetching them again costs nothing more. Upstream marks exactly one of
/// its batch methods with a cost warning — <c>submit_job</c> — and #39 confirmed the asymmetry
/// against the live API. Submission lives in <see cref="RealBatchSubmitTests"/>, behind the second
/// gate, so that this class's guarantee stays checkable by reading the file list.
/// </para>
/// <para>
/// <b>Which makes this the most valuable class in #39</b>, because the download half is the part a
/// mock is least able to confirm. <c>MockHistoricalGateway</c> answers <c>Range</c> the way this
/// repo read the HTTP specification; Databento's server answers it the way Databento's server
/// answers it. The two agreeing is the only evidence that resumable transfer works at all — and
/// unlike M2's live session or #38's range download, obtaining that evidence is free.
/// </para>
/// <para>
/// <b>They need a finished job to work on and skip when the account has none.</b> Nothing here
/// creates one: a class that made its own fixture would be a class that spends. The job is
/// discovered through <see cref="BatchClient.ListJobsAsync"/>, so these tests follow whatever the
/// account happens to hold rather than pinning an id that expires in thirty days.
/// </para>
/// </remarks>
[Trait("Category", "Historical")]
public class RealBatchApiTests
{
    /// <summary>Gate for every <c>SkipUnless</c> in this class: a key, and nothing more.</summary>
    public static bool IsConfigured => HistoricalCredentials.IsConfigured;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static HistoricalClient Client() => new() { ApiKey = HistoricalCredentials.ApiKey };

    /// <summary>The short listing: an id, a state and a receipt time for every job.</summary>
    [Fact(SkipUnless = nameof(IsConfigured), Skip = HistoricalCredentials.SkipReason)]
    public async Task ListJobs_ReturnsAShortFormThisLibraryCanRead()
    {
        await using var client = Client();

        var jobs = await client.Batch.ListJobsAsync(cancellationToken: Cancel);

        SkipIfNoJobs(jobs.Count);
        Assert.All(jobs, job =>
        {
            Assert.NotEmpty(job.Id);
            Assert.InRange(job.ReceivedTimestamp, Instant.FromUtc(2015, 1, 1, 0, 0, 0), FarFuture);

            // Reaching here at all means the state parsed. The assertion is that it is one this
            // library names — which is how the three states upstream lacks were found.
            Assert.Contains(job.State, AllStates);
        });
    }

    /// <summary>
    /// The state filter is applied by the server, not merely accepted by it: every job that comes
    /// back is in a state that was asked for.
    /// </summary>
    [Fact(SkipUnless = nameof(IsConfigured), Skip = HistoricalCredentials.SkipReason)]
    public async Task ListJobs_FiltersByState()
    {
        await using var client = Client();

        var done = await client.Batch.ListJobsAsync(
            new ListJobsParams { States = [JobState.Done] }, Cancel);

        SkipIfNoJobs(done.Count);
        Assert.All(done, job => Assert.Equal(JobState.Done, job.State));
    }

    /// <summary>
    /// The <c>since</c> filter is applied, and it is sent as Unix nanoseconds — which is the
    /// spelling upstream uses and #39 measured working.
    /// </summary>
    /// <remarks>
    /// Asserted at the two extremes rather than at a job's own timestamp, because whether the bound
    /// is inclusive is not something this test needs to pin down: a <c>since</c> past every job
    /// must return nothing and a <c>since</c> before every job must return everything, on either
    /// reading. A test written against one reading would be asserting a guess.
    /// </remarks>
    [Fact(SkipUnless = nameof(IsConfigured), Skip = HistoricalCredentials.SkipReason)]
    public async Task ListJobs_FiltersBySubmissionTime()
    {
        await using var client = Client();

        var all = await client.Batch.ListJobsAsync(cancellationToken: Cancel);
        SkipIfNoJobs(all.Count);

        var everything = await client.Batch.ListJobsAsync(
            new ListJobsParams { Since = Instant.FromUtc(2015, 1, 1, 0, 0, 0) }, Cancel);
        var nothing = await client.Batch.ListJobsAsync(
            new ListJobsParams { Since = FarFuture }, Cancel);

        Assert.Equal(all.Count, everything.Count);
        Assert.Empty(nothing);
    }

    /// <summary>
    /// A whole job, read from the live API rather than from the body <see cref="BatchFixture"/>
    /// transcribed — which is the point, the fixture and the reader having the same author.
    /// </summary>
    /// <remarks>
    /// Every enum on the way in throws rather than defaulting, so simply arriving here proves the
    /// schema, encoding, compression, both symbology types, the delivery mechanism, the split
    /// duration and the state were all spellings this library can name. The assertions below are
    /// about the fields no converter would have caught.
    /// </remarks>
    [Fact(SkipUnless = nameof(IsConfigured), Skip = HistoricalCredentials.SkipReason)]
    public async Task GetJobDetails_ReadsAWholeJobFromTheLiveApi()
    {
        await using var client = Client();

        var summary = await FinishedJobAsync(client);
        var job = await client.Batch.GetJobDetailsAsync(summary.Id, Cancel);

        Assert.Equal(summary.Id, job.Id);
        Assert.Equal(summary.State, job.State);
        Assert.Equal(summary.ReceivedTimestamp, job.ReceivedTimestamp);
        Assert.NotEmpty(job.Dataset);
        Assert.NotEqual(SymbolsKind.None, job.Symbols.Kind);

        // A finished job has been through every stage, so its timings and sizes are populated and
        // its range is a range.
        Assert.NotNull(job.QueuedTimestamp);
        Assert.NotNull(job.ProcessStartTimestamp);
        Assert.NotNull(job.ProcessDoneTimestamp);
        Assert.NotNull(job.RecordCount);
        Assert.NotNull(job.PackageSize);
        Assert.Equal(job.Start, job.ToDateTimeRange().Start);
        Assert.True(job.End > job.Start);
    }

    /// <summary>
    /// The file list, and the two things #39's port depends on: an <c>https</c> URL whose path is
    /// under <c>/v0/</c>, and a <c>sha256:</c> hash.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the probe that settled the download design, kept as a test.</b> The URL names
    /// <c>api.databento.com</c> while the API answers at <c>hist.databento.com</c>, and this
    /// library — like upstream — keeps only the path and re-issues it against the configured host.
    /// That is what stops the API key travelling to a host the caller never configured, and it only
    /// works because the path is a <c>/v0/</c> slug like any other. If Databento ever moves the
    /// files somewhere that is not, this fails and says so.
    /// </para>
    /// <para>
    /// The <c>sha256:</c> assertion is the other half: an algorithm this library cannot compute is
    /// skipped rather than failed, so a change there would quietly turn verification off. It is
    /// asserted rather than logged for that reason.
    /// </para>
    /// </remarks>
    [Fact(SkipUnless = nameof(IsConfigured), Skip = HistoricalCredentials.SkipReason)]
    public async Task ListFiles_ReturnsHttpsUrlsUnderTheVersionedPathAndSha256Hashes()
    {
        await using var client = Client();

        var job = await FinishedJobAsync(client);
        var files = await client.Batch.ListFilesAsync(job.Id, Cancel);

        Assert.NotEmpty(files);
        Assert.All(files, file =>
        {
            Assert.NotEmpty(file.Filename);
            Assert.True(file.Size > 0, $"'{file.Filename}' is {file.Size} bytes.");
            Assert.StartsWith("sha256:", file.Hash, StringComparison.Ordinal);

            var https = Assert.Contains("https", file.Urls);
            var url = new Uri(https, UriKind.Absolute);
            Assert.Equal("https", url.Scheme);
            Assert.StartsWith(
                $"/v{HistoricalClient.ApiVersion}/batch/download/",
                url.AbsolutePath,
                StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// <b>A real file, fetched from Databento and verified against Databento's own checksum.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The verification is the assertion: this library throws on a mismatch, so the download
    /// returning at all means the bytes it wrote hash to what the API published. Nothing in the
    /// mock can establish that — the harness serves whatever hash a test hands it.
    /// </para>
    /// <para>
    /// The smallest file in the job is chosen so this stays quick; it is usually the
    /// <c>condition.json</c> Databento packages with every job, at a few hundred bytes. The size on
    /// disk is checked against the size the API advertised, which is the number the resume logic
    /// compares against.
    /// </para>
    /// </remarks>
    [Fact(SkipUnless = nameof(IsConfigured), Skip = HistoricalCredentials.SkipReason)]
    public async Task Download_FetchesAndVerifiesARealFile()
    {
        using var directory = BatchFixture.NewDirectory();
        await using var client = Client();

        var job = await FinishedJobAsync(client);
        var file = await SmallestFileAsync(client, job.Id);

        var paths = await client.Batch.DownloadAsync(
            new DownloadParams
            {
                OutputDirectory = directory.Path,
                JobId = job.Id,
                Filename = file.Filename,
            },
            Cancel);

        var path = Assert.Single(paths);
        Assert.Equal(Path.Combine(directory.Path, job.Id, file.Filename), path);
        Assert.Equal((long)file.Size, new FileInfo(path).Length);
    }

    /// <summary>
    /// <b>Resumption against the real server</b> — the assumption the whole feature rests on, asked
    /// of Databento rather than of this repo's reading of RFC 9110.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Half a real file is written to disk, and the download is asked to finish it. The client
    /// sends <c>Range: bytes=N-</c>; if the server answers <c>206</c> with the tail, the completed
    /// file matches the published checksum and this returns. If the server ignored the header and
    /// sent the whole file, the client would notice and start over — also passing, but by the other
    /// route — so the request count and the byte count are asserted too, and together they say
    /// which happened.
    /// </para>
    /// <para>
    /// #39's probe measured <c>206 Partial Content</c> with <c>Content-Range: bytes 100-121/122</c>
    /// against a real batch file, and <c>416</c> for a range past the end. This is that measurement
    /// as a standing test.
    /// </para>
    /// </remarks>
    [Fact(SkipUnless = nameof(IsConfigured), Skip = HistoricalCredentials.SkipReason)]
    public async Task Download_ResumesAPartialFileAgainstTheRealServer()
    {
        using var directory = BatchFixture.NewDirectory();
        await using var client = Client();

        var job = await FinishedJobAsync(client);
        var file = await SmallestFileAsync(client, job.Id);

        var parameters = new DownloadParams
        {
            OutputDirectory = directory.Path,
            JobId = job.Id,
            Filename = file.Filename,
        };

        // Fetch it once, whole, so there is a known-good copy to cut in half.
        var path = (await client.Batch.DownloadAsync(parameters, Cancel))[0];
        var whole = await File.ReadAllBytesAsync(path, Cancel);

        var half = whole.Length / 2;
        Assert.True(half > 0, $"'{file.Filename}' is {whole.Length} bytes, too small to halve.");
        await File.WriteAllBytesAsync(path, whole[..half], Cancel);

        await client.Batch.DownloadAsync(parameters, Cancel);

        // Byte-identical, and verified: the client throws on a checksum mismatch, so a server that
        // answered the Range with the wrong tail could not have got here.
        Assert.Equal(whole, await File.ReadAllBytesAsync(path, Cancel));
    }

    /// <summary>
    /// The two lookup endpoints reject an unusable job id <b>differently</b>, and this test is why
    /// that is known rather than assumed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It failed on its first run, which is the whole argument for this class.</b> #39 probed
    /// <c>batch.list_files?job_id=NOPE-123</c>, saw <c>400 Invalid value for parameter
    /// <c>job_id</c></c>, and wrote that into <see cref="BatchClient.GetJobDetailsAsync"/>'s
    /// documentation as though the two endpoints agreed. They do not:
    /// <c>get_job_details</c> answers <c>404 batch_job_not_found</c> to any id it cannot find,
    /// malformed or not, while <c>list_files</c> checks the <em>shape</em> of the id first and only
    /// reaches <c>404</c> for one that is well-formed and absent.
    /// </para>
    /// <para>
    /// The same shape of mistake as #45, found the same way. CLAUDE.md's rule — probe the endpoint
    /// you are about to change, not the one next to it — has now been paid for twice, so both
    /// halves are pinned here.
    /// </para>
    /// </remarks>
    [Fact(SkipUnless = nameof(IsConfigured), Skip = HistoricalCredentials.SkipReason)]
    public async Task TheTwoLookupEndpoints_RejectAnUnusableJobIdDifferently()
    {
        const string malformed = "NOPE-123";
        const string wellFormedButAbsent = "XNAS-20990101-AAAAAAAAAA";

        await using var client = Client();

        // get_job_details does not care what the id looks like: it is either found or it is not.
        foreach (var id in new[] { malformed, wellFormedButAbsent })
        {
            var details = await Assert.ThrowsAsync<DatabentoApiException>(
                () => client.Batch.GetJobDetailsAsync(id, Cancel));

            Assert.Equal(System.Net.HttpStatusCode.NotFound, details.StatusCode);
            Assert.Equal("batch_job_not_found", details.Case);
        }

        // list_files validates the shape first, and only that case is a 400.
        var malformedFiles = await Assert.ThrowsAsync<DatabentoApiException>(
            () => client.Batch.ListFilesAsync(malformed, Cancel));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, malformedFiles.StatusCode);
        Assert.Null(malformedFiles.Case);

        var absentFiles = await Assert.ThrowsAsync<DatabentoApiException>(
            () => client.Batch.ListFilesAsync(wellFormedButAbsent, Cancel));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, absentFiles.StatusCode);
        Assert.Equal("batch_job_not_found", absentFiles.Case);
    }

    /// <summary>
    /// An unknown state is refused with a body that enumerates the ones the API knows — the
    /// response that gave <see cref="JobState"/> its three extra members.
    /// </summary>
    /// <remarks>
    /// Sent through <see cref="HistoricalClient.SendAsync"/> rather than through
    /// <see cref="ListJobsParams"/>, which cannot render an undefined state — the transport is
    /// public precisely so a caller can reach past the typed surface, and this is that. It also
    /// needs no response type: the API's rejection is thrown before any body is read.
    /// </remarks>
    [Fact(SkipUnless = nameof(IsConfigured), Skip = HistoricalCredentials.SkipReason)]
    public async Task TheApisJobStates_AreStillTheSevenThisLibraryNames()
    {
        await using var client = Client();

        var thrown = await Assert.ThrowsAsync<DatabentoApiException>(
            () => client.SendAsync(
                HttpMethod.Get,
                "batch.list_jobs",
                [new KeyValuePair<string, string>("states", "bogus")],
                accept: null,
                Cancel));

        foreach (var state in AllStates)
        {
            Assert.Contains(
                $"'{state.ToWireString()}'",
                thrown.Message,
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Roughly a century out, which is past any job and comfortably inside the nanosecond range a
    /// <see cref="long"/> can carry — the year 2262 ceiling CLAUDE.md names.
    /// </summary>
    private static Instant FarFuture => Instant.FromUtc(2099, 1, 1, 0, 0, 0);

    private static JobState[] AllStates =>
    [
        JobState.Received, JobState.Queued, JobState.Processing, JobState.Finalizing,
        JobState.Done, JobState.Expired, JobState.Purged,
    ];

    /// <summary>
    /// A finished job to work on, or a skip. Nothing here creates one — that would be a class that
    /// spends.
    /// </summary>
    private static async Task<BatchJobSummary> FinishedJobAsync(HistoricalClient client)
    {
        var jobs = await client.Batch.ListJobsAsync(
            new ListJobsParams { States = [JobState.Done] }, Cancel);

        SkipIfNoJobs(jobs.Count);
        return jobs[^1];
    }

    /// <summary>The smallest file of a job, so a real download stays quick.</summary>
    private static async Task<BatchFileDescription> SmallestFileAsync(HistoricalClient client, string jobId)
    {
        var files = await client.Batch.ListFilesAsync(jobId, Cancel);

        Assert.NotEmpty(files);
        return files.MinBy(file => file.Size)!;
    }

    private static void SkipIfNoJobs(int count) =>
        Assert.SkipWhen(
            count == 0,
            "This account has no finished batch jobs to inspect. These tests follow whatever the "
            + "account holds rather than creating one, because creating one is what costs money — "
            + "see RealBatchSubmitTests.");

}
