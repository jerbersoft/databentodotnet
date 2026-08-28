using DatabentoDotNet.Dbn;
using NodaTime;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// Tests for <see cref="BatchClient"/>'s five request-shaped endpoints, reached through
/// <see cref="HistoricalClient.Batch"/>.
/// </summary>
/// <remarks>
/// The transport-level pair to <c>BatchParamsTests</c>: the same renderings, but after they have
/// been through <see cref="HistoricalClient.SendAsync"/> and Kestrel, which is where an encoding
/// applied twice or not at all becomes visible. <c>BatchDownloadTests</c> owns the sixth endpoint,
/// which is large enough to be its own file.
/// </remarks>
public sealed class BatchClientTests
{
    private static readonly DateTimeRange Range = DateTimeRange.Between(
        Instant.FromUtc(2023, 6, 14, 0, 0, 0), Instant.FromUtc(2023, 6, 17, 0, 0, 0));

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// The whole submitted-job rendering as it crosses the wire, in upstream's field order, with
    /// nothing on the query string.
    /// </summary>
    [Fact]
    public async Task SubmitJob_PostsEveryParameterInTheFormAndLeavesTheQueryEmpty()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post("batch.submit_job", MockHistoricalResponse.Json(BatchFixture.JobJson));
        await using var client = ClientFor(gateway);

        var job = await client.Batch.SubmitJobAsync(Job(), Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(BatchFixture.JobId, job.Id);

        var request = gateway.Requests[0];
        Assert.Equal("POST", request.Method);
        Assert.Equal(MockHistoricalGateway.PathFor("batch.submit_job"), request.Path);
        Assert.Empty(request.Query);
        Assert.Equal("XNAS.ITCH", request.Form["dataset"]);
        Assert.Equal("trades", request.Form["schema"]);
        Assert.Equal("dbn", request.Form["encoding"]);
        Assert.Equal("zstd", request.Form["compression"]);
        Assert.Equal("false", request.Form["pretty_px"]);
        Assert.Equal("false", request.Form["pretty_ts"]);
        Assert.Equal("false", request.Form["map_symbols"]);
        Assert.Equal("false", request.Form["split_symbols"]);
        Assert.Equal("download", request.Form["delivery"]);
        Assert.Equal("raw_symbol", request.Form["stype_in"]);
        Assert.Equal("instrument_id", request.Form["stype_out"]);
        Assert.Equal("TSLA", request.Form["symbols"]);
        Assert.Equal("1686700800000000000", request.Form["start"]);
        Assert.Equal("1686960000000000000", request.Form["end"]);
        Assert.Equal("day", request.Form["split_duration"]);
    }

    /// <summary>
    /// The submitted body is exactly upstream's, byte for byte and in its order — which is the
    /// cheapest way to tell this rendering apart from a plausible one.
    /// </summary>
    /// <remarks>
    /// <see cref="RecordedRequest.Body"/> rather than <see cref="RecordedRequest.Form"/>, because
    /// the decoded view loses both the order and the percent-encoding.
    /// </remarks>
    [Fact]
    public async Task SubmitJob_SendsTheFieldsInUpstreamsOrder()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post("batch.submit_job", MockHistoricalResponse.Json(BatchFixture.JobJson));
        await using var client = ClientFor(gateway);

        await client.Batch.SubmitJobAsync(Job(), Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(
            "dataset=XNAS.ITCH&schema=trades&encoding=dbn&compression=zstd&pretty_px=false"
            + "&pretty_ts=false&map_symbols=false&split_symbols=false&delivery=download"
            + "&stype_in=raw_symbol&stype_out=instrument_id&symbols=TSLA"
            + "&start=1686700800000000000&end=1686960000000000000&split_duration=day",
            System.Text.Encoding.UTF8.GetString(gateway.Requests[0].Body.Span));
    }

    /// <summary>
    /// A comma-separated symbol list arrives as one <c>%2C</c>-escaped value rather than as a
    /// repeated field — the case <see cref="RecordedRequest.RawQuery"/>'s remarks single out for
    /// the query string, made here for the form body.
    /// </summary>
    [Fact]
    public async Task SubmitJob_EscapesTheCommaBetweenSymbols()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post("batch.submit_job", MockHistoricalResponse.Json(BatchFixture.JobJson));
        await using var client = ClientFor(gateway);

        await client.Batch.SubmitJobAsync(Job() with { Symbols = Symbols.From(["TSLA", "MSFT"]) }, Cancel);

        gateway.ThrowIfRejected();
        Assert.Contains(
            "symbols=TSLA%2CMSFT",
            System.Text.Encoding.UTF8.GetString(gateway.Requests[0].Body.Span),
            StringComparison.Ordinal);
        Assert.Equal("TSLA,MSFT", gateway.Requests[0].Form["symbols"]);
    }

    /// <summary>
    /// A combination the API rejects never reaches the wire: the guard runs while the form is being
    /// rendered, which is before a request exists.
    /// </summary>
    [Fact]
    public async Task SubmitJob_RefusesAnInvalidCombinationWithoutSendingAnything()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post("batch.submit_job", MockHistoricalResponse.Json(BatchFixture.JobJson));
        await using var client = ClientFor(gateway);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.Batch.SubmitJobAsync(Job() with { PrettyPx = true }, Cancel));

        Assert.Empty(gateway.Requests);
    }

    /// <summary>
    /// The short listing asks for the short form, which is what makes its three-field response type
    /// correct.
    /// </summary>
    [Fact]
    public async Task ListJobs_AsksForTheShortForm()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get("batch.list_jobs", MockHistoricalResponse.Json(BatchFixture.JobSummaryListJson));
        await using var client = ClientFor(gateway);

        var summaries = await client.Batch.ListJobsAsync(cancellationToken: Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(4, summaries.Count);

        var request = gateway.Requests[0];
        Assert.Equal("GET", request.Method);
        Assert.Equal(MockHistoricalGateway.PathFor("batch.list_jobs"), request.Path);
        Assert.Equal("true", request.Query["short"]);
        Assert.Empty(request.Form);
    }

    /// <summary>Filters travel on the query string, ahead of the <c>short</c> flag.</summary>
    [Fact]
    public async Task ListJobs_SendsItsFiltersAheadOfTheShortFlag()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get("batch.list_jobs", MockHistoricalResponse.Json(BatchFixture.JobSummaryListJson));
        await using var client = ClientFor(gateway);

        await client.Batch.ListJobsAsync(
            new ListJobsParams
            {
                States = [JobState.Done, JobState.Finalizing],
                Since = Instant.FromUtc(2026, 8, 27, 0, 0, 0),
            },
            Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(
            "?states=done%2Cfinalizing&since=1787788800000000000&short=true",
            gateway.Requests[0].RawQuery);
    }

    /// <summary>A null filter set sends the flag and nothing else.</summary>
    [Fact]
    public async Task ListJobs_SendsOnlyTheShortFlagWhenNothingIsFiltered()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get("batch.list_jobs", MockHistoricalResponse.Json("[]"));
        await using var client = ClientFor(gateway);

        Assert.Empty(await client.Batch.ListJobsAsync(cancellationToken: Cancel));

        gateway.ThrowIfRejected();
        Assert.Equal("?short=true", gateway.Requests[0].RawQuery);
    }

    /// <summary>
    /// The full listing sends no <c>short</c> at all, which #39 confirmed against the live API
    /// returns the whole job.
    /// </summary>
    /// <remarks>
    /// The <c>#pragma</c> is the point of the test rather than an inconvenience: the method carries
    /// <see cref="ObsoleteAttribute"/>, this repo treats warnings as errors, and a caller who
    /// reaches for it gets the same compiler error a reader of upstream's <c>#[deprecated]</c>
    /// would. It is suppressed here, narrowly, because the endpoint still answers and the port has
    /// to keep working.
    /// </remarks>
    [Fact]
    public async Task ListJobsFull_SendsNoShortFlagAndReadsWholeJobs()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get("batch.list_jobs", MockHistoricalResponse.Json($"[{BatchFixture.JobJson}]"));
        await using var client = ClientFor(gateway);

#pragma warning disable CS0618 // Deprecated upstream in 0.60.0; ported, and therefore tested.
        var jobs = await client.Batch.ListJobsFullAsync(cancellationToken: Cancel);
#pragma warning restore CS0618

        gateway.ThrowIfRejected();
        Assert.Equal("XNAS.ITCH", Assert.Single(jobs).Dataset);
        Assert.Equal(string.Empty, gateway.Requests[0].RawQuery);
        Assert.DoesNotContain("short", gateway.Requests[0].Query.Keys);
    }

    /// <summary>Both listings share a slug, and differ only by that one flag.</summary>
    [Fact]
    public async Task BothListings_UseTheSameSlug()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get("batch.list_jobs", MockHistoricalResponse.Json("[]"));
        await using var client = ClientFor(gateway);

        await client.Batch.ListJobsAsync(cancellationToken: Cancel);
#pragma warning disable CS0618 // See ListJobsFull_SendsNoShortFlagAndReadsWholeJobs.
        await client.Batch.ListJobsFullAsync(cancellationToken: Cancel);
#pragma warning restore CS0618

        gateway.ThrowIfRejected();
        Assert.Equal(2, gateway.Requests.Count);
        Assert.All(
            gateway.Requests,
            request => Assert.Equal(MockHistoricalGateway.PathFor("batch.list_jobs"), request.Path));
    }

    /// <summary>The job id travels on the query string, and the response is one whole job.</summary>
    [Fact]
    public async Task GetJobDetails_SendsTheJobIdAndReadsAWholeJob()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get("batch.get_job_details", MockHistoricalResponse.Json(BatchFixture.JobJson));
        await using var client = ClientFor(gateway);

        var job = await client.Batch.GetJobDetailsAsync(BatchFixture.JobId, Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(BatchFixture.JobId, job.Id);
        Assert.Equal(JobState.Done, job.State);
        Assert.Equal(BatchFixture.JobId, gateway.Requests[0].Query["job_id"]);
        Assert.Equal(MockHistoricalGateway.PathFor("batch.get_job_details"), gateway.Requests[0].Path);
    }

    /// <summary>The file list, with the protocol map intact.</summary>
    [Fact]
    public async Task ListFiles_SendsTheJobIdAndReadsEveryFile()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get("batch.list_files", MockHistoricalResponse.Json(BatchFixture.FileListJson));
        await using var client = ClientFor(gateway);

        var files = await client.Batch.ListFilesAsync(BatchFixture.JobId, Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(
            [BatchFixture.ConditionFilename, BatchFixture.DataFilename],
            files.Select(file => file.Filename));
        Assert.Equal(BatchFixture.JobId, gateway.Requests[0].Query["job_id"]);
    }

    /// <summary>
    /// The two rejections a bad job id produces, each served by the endpoint that really produces
    /// it.
    /// </summary>
    /// <remarks>
    /// The pairing is the assertion. <c>get_job_details</c> answers <c>404 batch_job_not_found</c>
    /// to an id it cannot find whatever it looks like; <c>list_files</c> checks the id's shape
    /// first and answers <c>400</c> with the API's <em>simple</em> error body for a malformed one.
    /// #39 assumed they agreed and documented the wrong one for
    /// <see cref="BatchClient.GetJobDetailsAsync"/> — <c>RealBatchApiTests</c> is what found it,
    /// and this is the same measurement served locally so the mapping stays covered in CI.
    /// </remarks>
    [Fact]
    public async Task TheTwoLookupEndpoints_SurfaceTheDifferentRejectionsTheApiSends()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(
            "batch.get_job_details",
            MockHistoricalResponse.BusinessError(
                404,
                "batch_job_not_found",
                "Job 'NOPE-123' not found.",
                "https://databento.com/docs/api-reference-historical/batch/batch-submit-job"));
        gateway.Get(
            "batch.list_files",
            MockHistoricalResponse.SimpleError(
                400, "Bad Request: Invalid value for parameter `job_id`, was 'NOPE-123'."));
        await using var client = ClientFor(gateway);

        var details = await Assert.ThrowsAsync<DatabentoApiException>(
            () => client.Batch.GetJobDetailsAsync("NOPE-123", Cancel));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, details.StatusCode);
        Assert.Equal("batch_job_not_found", details.Case);

        var files = await Assert.ThrowsAsync<DatabentoApiException>(
            () => client.Batch.ListFilesAsync("NOPE-123", Cancel));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, files.StatusCode);
        Assert.Null(files.Case);
        Assert.Contains("job_id", files.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The business-error shape, whose <c>case</c> the API uses for an unknown job state — the very
    /// response that told #39 the seven states.
    /// </summary>
    [Fact]
    public async Task ListJobs_SurfacesTheBusinessErrorCaseForAnUnknownState()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(
            "batch.list_jobs",
            MockHistoricalResponse.BusinessError(
                400,
                "batch_job_state_invalid",
                "Invalid job state 'bogus', use any of ['received', 'queued', 'processing', "
                + "'finalizing', 'done', 'expired', 'purged'].",
                "https://databento.com/docs/api-reference-historical/batch/batch-submit-job"));
        await using var client = ClientFor(gateway);

        var thrown = await Assert.ThrowsAsync<DatabentoApiException>(
            () => client.Batch.ListJobsAsync(cancellationToken: Cancel));

        Assert.Equal("batch_job_state_invalid", thrown.Case);
        Assert.Contains("finalizing", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The facade is built once and handed back, so a caller holding a reference to it across
    /// requests is holding the same object — the property the lazy field exists for.
    /// </summary>
    [Fact]
    public async Task TheBatchFacade_IsCachedOnTheClient()
    {
        await using var client = new HistoricalClient { ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey) };

        Assert.Same(client.Batch, client.Batch);
    }

    /// <summary>Every method guards its arguments before building a request.</summary>
    [Fact]
    public async Task EveryEndpoint_GuardsItsArguments()
    {
        await using var client = new HistoricalClient { ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey) };

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.Batch.SubmitJobAsync(null!, Cancel));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.Batch.DownloadAsync(null!, Cancel));

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.Batch.GetJobDetailsAsync(string.Empty, Cancel));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.Batch.GetJobDetailsAsync(null!, Cancel));
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.Batch.ListFilesAsync(string.Empty, Cancel));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.Batch.ListFilesAsync(null!, Cancel));
    }

    private static HistoricalClient ClientFor(MockHistoricalGateway gateway) => new()
    {
        ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey),
        BaseUrl = gateway.BaseUrl,
    };

    private static SubmitJobParams Job() => new()
    {
        Dataset = "XNAS.ITCH",
        Symbols = Symbols.From("TSLA"),
        Schema = Schema.Trades,
        DateTimeRange = Range,
    };
}
