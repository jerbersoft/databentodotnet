using DatabentoDotNet.Dbn;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// Tests for <see cref="BatchClient.DownloadAsync"/> — resumption, the three size cases, the
/// checksum, the retries, and the two departures from upstream.
/// </summary>
/// <remarks>
/// <para>
/// The largest single piece of #39, and the reason ROADMAP.md §5 rejected an
/// <c>HttpMessageHandler</c> stub for this milestone's test double: a fake handler never opens a
/// socket, so it cannot answer <c>Range: bytes=N-</c> with <c>206 Partial Content</c> and cannot
/// reset a connection mid-body. Both are load-bearing here.
/// </para>
/// <para>
/// <b>Every download in this file goes to the loopback gateway even though every file URL in the
/// fixture names <c>api.databento.com</c>.</b> That is not incidental — it is the whole reason
/// <see cref="HistoricalClient.GetPathAsync"/> keeps only the URL's path. A client that followed
/// the URL as given would leave the harness entirely and take the API key to a host no test
/// configured, and every one of these tests would fail with a connection error rather than
/// silently passing.
/// </para>
/// </remarks>
public sealed class BatchDownloadTests
{
    /// <summary>
    /// How long a poll waits before giving up. Generous, because it bounds a failure rather than a
    /// success: every wait here completes in milliseconds when the code is right.
    /// </summary>
    private const int PollTimeoutMilliseconds = 15_000;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>Every file in the job lands under <c>{OutputDirectory}/{JobId}/</c>, in list order.</summary>
    [Fact]
    public async Task Download_WritesEveryFileOfTheJobIntoItsOwnDirectory()
    {
        var condition = BatchFixture.Utf8("""[{"date":"2022-06-10","condition":"available"}]""");
        var data = BatchFixture.PatternedBody(4096);

        using var directory = BatchFixture.NewDirectory();
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);

        gateway.Get("batch.list_files", MockHistoricalResponse.Json(TwoFileListJson(condition, data)));
        gateway.Get(BatchFixture.DownloadSlug(BatchFixture.ConditionFilename), MockHistoricalResponse.Binary(condition));
        gateway.Get(BatchFixture.DownloadSlug(BatchFixture.DataFilename), MockHistoricalResponse.Binary(data));

        await using var client = ClientFor(gateway);

        var paths = await client.Batch.DownloadAsync(Parameters(directory), Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(
            [directory.FileAt(BatchFixture.ConditionFilename), directory.FileAt(BatchFixture.DataFilename)],
            paths);
        Assert.Equal(condition, await File.ReadAllBytesAsync(paths[0], Cancel));
        Assert.Equal(data, await File.ReadAllBytesAsync(paths[1], Cancel));
    }

    /// <summary>
    /// <b>#39's definition of done: resumption proved by interruption, not by a header
    /// appearing.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The interruption is a real one — the first download is cancelled part-way, which is what a
    /// killed process does to a transfer — and the partial file it leaves is measured off disk
    /// rather than assumed. A second client then downloads into the same directory and the
    /// resulting file is byte-identical to the whole body.
    /// </para>
    /// <para>
    /// The two requests are asserted apart: the first carried no <c>Range</c>, and the second
    /// carried <c>Range: bytes=N-</c> for exactly the N bytes the interruption left behind.
    /// <see cref="Download_DoesNotRefetchTheBytesAlreadyOnDisk"/> proves the other half — that the
    /// first N bytes were not fetched again — from the file's contents rather than from a header.
    /// </para>
    /// <para>
    /// <b>The first transfer is parked rather than merely large, and that was a measured
    /// correction.</b> An earlier version raced a 512 KB body against the cancellation and lost:
    /// loopback delivered the whole file in under 56 ms, so the token fired after the last write
    /// and there was nothing partial to resume. Holding the response open on a gate that is never
    /// opened removes the race entirely — the transfer <em>cannot</em> finish, so the cancellation
    /// is guaranteed to interrupt it rather than merely likely to.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Download_ResumesAnInterruptedTransferAcrossAClientRestart()
    {
        // Written before the gate, and comfortably past the 80 KB write buffer so that some of it
        // has reached disk while the transfer is still open — which is what the poll below waits
        // for. The rest of the buffer is flushed when the cancelled stream is disposed.
        const int beforeTheGate = 200_000;

        var body = BatchFixture.PatternedBody(512 * 1024);
        var path = default(string);

        // Never completed. The gateway's wait is linked to the request being aborted, so
        // cancelling the client is what releases it.
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var directory = BatchFixture.NewDirectory();
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);

        gateway.Get("batch.list_files", MockHistoricalResponse.Json(OneFile(body)));
        gateway.Get(
            BatchFixture.DownloadSlug(BatchFixture.DataFilename),
            MockHistoricalResponse.DroppedThenResumable(body, beforeTheGate, parked.Task));

        long interrupted;
        await using (var first = ClientFor(gateway))
        {
            using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
            var download = first.Batch.DownloadAsync(Parameters(directory), interrupt.Token);

            path = directory.FileAt(BatchFixture.DataFilename);
            await WaitUntilAsync(() => LengthOf(path) >= 80 * 1024, "the transfer to reach disk");

            await interrupt.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);

            interrupted = LengthOf(path);
        }

        Assert.InRange(interrupted, 1, body.Length - 1);

        // A second client, as a restarted process would be, into the same output directory.
        await using (var second = ClientFor(gateway))
        {
            await second.Batch.DownloadAsync(Parameters(directory), Cancel);
        }

        gateway.ThrowIfRejected();
        Assert.Equal(body, await File.ReadAllBytesAsync(path, Cancel));

        var downloads = DownloadRequests(gateway);
        Assert.Equal(2, downloads.Count);
        Assert.False(downloads[0].Headers.ContainsKey("Range"));
        Assert.Equal($"bytes={interrupted}-", downloads[1].Headers["Range"]);
    }

    /// <summary>
    /// The bytes already on disk are not fetched again — proved from the file's contents rather
    /// than from the <c>Range</c> header, which is what #39 asks for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The trick is that the served body's first 2,048 bytes are <b>wrong</b> while its tail is
    /// right, and the published checksum is the one of the <b>correct</b> whole file. A client that
    /// resumes gets only the tail, joins it to the correct prefix already on disk, and verifies. A
    /// client that re-fetched from zero would overwrite the good prefix with the bad one and fail
    /// the checksum.
    /// </para>
    /// <para>
    /// So the assertion is not "a header was sent" but "the file on disk is the correct one, which
    /// is only reachable by not re-fetching". A header can be sent and ignored; this cannot.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Download_DoesNotRefetchTheBytesAlreadyOnDisk()
    {
        const int alreadyHave = 2048;

        var correct = BatchFixture.PatternedBody(8192);
        var served = (byte[])correct.Clone();
        Array.Clear(served, 0, alreadyHave);

        using var directory = BatchFixture.NewDirectory();
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);

        gateway.Get(
            "batch.list_files",
            MockHistoricalResponse.Json(BatchFixture.OneFileListJson(
                BatchFixture.DataFilename,
                served,
                advertisedHash: $"sha256:{BatchFixture.Sha256Of(correct)}")));
        gateway.Get(BatchFixture.DownloadSlug(BatchFixture.DataFilename), MockHistoricalResponse.Binary(served));

        var path = directory.FileAt(BatchFixture.DataFilename);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, correct[..alreadyHave], Cancel);

        await using var client = ClientFor(gateway);
        await client.Batch.DownloadAsync(Parameters(directory), Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(correct, await File.ReadAllBytesAsync(path, Cancel));
        Assert.Equal($"bytes={alreadyHave}-", Assert.Single(DownloadRequests(gateway)).Headers["Range"]);
    }

    /// <summary>
    /// A file already the right size is left alone, and <b>no request is made for it at all</b> —
    /// which is the assertion, since a client that re-fetched and overwrote would leave the same
    /// bytes behind.
    /// </summary>
    [Fact]
    public async Task Download_SkipsAFileThatIsAlreadyCompleteWithoutRequestingIt()
    {
        var body = BatchFixture.PatternedBody(4096);

        using var directory = BatchFixture.NewDirectory();
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);

        gateway.Get("batch.list_files", MockHistoricalResponse.Json(OneFile(body)));
        gateway.Get(BatchFixture.DownloadSlug(BatchFixture.DataFilename), MockHistoricalResponse.Binary(body));

        var path = directory.FileAt(BatchFixture.DataFilename);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, body, Cancel);

        await using var client = ClientFor(gateway);
        var paths = await client.Batch.DownloadAsync(Parameters(directory), Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(path, Assert.Single(paths));
        Assert.Empty(DownloadRequests(gateway));
        Assert.Equal(body, await File.ReadAllBytesAsync(path, Cancel));
    }

    /// <summary>
    /// A file larger than the API says it should be is an error rather than something to truncate:
    /// it is not this library's place to decide somebody else's larger file is wrong.
    /// </summary>
    [Fact]
    public async Task Download_RefusesAFileLargerThanExpectedAndLeavesItAlone()
    {
        var body = BatchFixture.PatternedBody(4096);
        var larger = BatchFixture.PatternedBody(5000, seed: 7);

        using var directory = BatchFixture.NewDirectory();
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);

        gateway.Get("batch.list_files", MockHistoricalResponse.Json(OneFile(body)));
        gateway.Get(BatchFixture.DownloadSlug(BatchFixture.DataFilename), MockHistoricalResponse.Binary(body));

        var path = directory.FileAt(BatchFixture.DataFilename);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, larger, Cancel);

        await using var client = ClientFor(gateway);

        var thrown = await Assert.ThrowsAsync<IOException>(
            () => client.Batch.DownloadAsync(Parameters(directory), Cancel));

        Assert.Contains(BatchFixture.DataFilename, thrown.Message, StringComparison.Ordinal);
        Assert.Empty(DownloadRequests(gateway));
        Assert.Equal(larger, await File.ReadAllBytesAsync(path, Cancel));
    }

    /// <summary>
    /// <b>#39's other definition of done: a corrupted body fails the download.</b> The harness
    /// serves bytes that do not match the hash it advertises, and this is the test that would have
    /// passed against upstream's behaviour of logging a warning and returning success.
    /// </summary>
    /// <remarks>
    /// The file is left on disk, which the exception says. It is evidence, and deleting it would
    /// also delete a resumable transfer that failed for some reason other than corruption.
    /// </remarks>
    [Fact]
    public async Task Download_ThrowsWhenTheBodyDoesNotMatchItsAdvertisedChecksum()
    {
        var body = BatchFixture.PatternedBody(4096);
        var otherBody = BatchFixture.PatternedBody(4096, seed: 3);

        using var directory = BatchFixture.NewDirectory();
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);

        gateway.Get(
            "batch.list_files",
            MockHistoricalResponse.Json(BatchFixture.OneFileListJson(
                BatchFixture.DataFilename,
                body,
                advertisedHash: $"sha256:{BatchFixture.Sha256Of(otherBody)}")));
        gateway.Get(BatchFixture.DownloadSlug(BatchFixture.DataFilename), MockHistoricalResponse.Binary(body));

        await using var client = ClientFor(gateway);

        var thrown = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.Batch.DownloadAsync(Parameters(directory), Cancel));

        Assert.Contains(BatchFixture.DataFilename, thrown.Message, StringComparison.Ordinal);
        Assert.Contains(BatchFixture.Sha256Of(otherBody), thrown.Message, StringComparison.Ordinal);
        Assert.Contains(BatchFixture.Sha256Of(body), thrown.Message, StringComparison.Ordinal);

        // Left for inspection, and not retried: a checksum failure is not a transient one.
        Assert.True(File.Exists(directory.FileAt(BatchFixture.DataFilename)));
        Assert.Single(DownloadRequests(gateway));
    }

    /// <summary>
    /// A checksum algorithm this library cannot compute is skipped rather than failed — upstream's
    /// behaviour, and the right one — but it is logged, because it silently downgrades the
    /// guarantee the test above just established.
    /// </summary>
    [Fact]
    public async Task Download_SkipsVerificationForAnUnknownAlgorithmAndSaysSo()
    {
        var body = BatchFixture.PatternedBody(4096);
        var logs = new RecordingLoggerFactory();

        using var directory = BatchFixture.NewDirectory();
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);

        gateway.Get(
            "batch.list_files",
            MockHistoricalResponse.Json(BatchFixture.OneFileListJson(
                BatchFixture.DataFilename, body, advertisedHash: "blake3:00ff00ff")));
        gateway.Get(BatchFixture.DownloadSlug(BatchFixture.DataFilename), MockHistoricalResponse.Binary(body));

        await using var client = new HistoricalClient
        {
            ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey),
            BaseUrl = gateway.BaseUrl,
            LoggerFactory = logs,
        };

        await client.Batch.DownloadAsync(Parameters(directory), Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(body, await File.ReadAllBytesAsync(directory.FileAt(BatchFixture.DataFilename), Cancel));

        var warning = Assert.Single(logs.EntriesWith(4));
        Assert.Contains("blake3", warning.Message, StringComparison.Ordinal);
        Assert.Contains(BatchFixture.DataFilename, warning.Message, StringComparison.Ordinal);
    }

    /// <summary>A hash with no algorithm at all is an error in both libraries.</summary>
    [Fact]
    public async Task Download_RefusesAChecksumWithNoAlgorithm()
    {
        var body = BatchFixture.PatternedBody(64);

        using var directory = BatchFixture.NewDirectory();
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);

        gateway.Get(
            "batch.list_files",
            MockHistoricalResponse.Json(BatchFixture.OneFileListJson(
                BatchFixture.DataFilename, body, advertisedHash: "deadbeef")));

        await using var client = ClientFor(gateway);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.Batch.DownloadAsync(Parameters(directory), Cancel));
    }

    /// <summary>
    /// <b>The defect found while porting: a retried transfer still verifies.</b> Upstream builds
    /// one hasher outside its retry loop and re-reads the partial file into it on every attempt, so
    /// after any retry the bytes on disk have been hashed twice and the digest cannot match.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bug is invisible upstream, where a mismatch is only a warning. Here it would be fatal —
    /// and it would fire on exactly the resumed transfers #39 exists to make work. This test fails
    /// with an <see cref="InvalidDataException"/> against the faithful port and passes against the
    /// fixed one, which is the only difference between them.
    /// </para>
    /// <para>
    /// The connection drops after 1 KB and the retry carries a <c>Range</c>, so the second request
    /// takes the harness's <c>206</c> branch rather than its drop branch — one registered route,
    /// two answers, decided by the client's own header.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Download_RetriesAMidStreamFailureAndStillVerifiesTheChecksum()
    {
        var body = BatchFixture.PatternedBody(8192);

        using var directory = BatchFixture.NewDirectory();
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);

        gateway.Get("batch.list_files", MockHistoricalResponse.Json(OneFile(body)));
        gateway.Get(
            BatchFixture.DownloadSlug(BatchFixture.DataFilename),
            MockHistoricalResponse.DroppedThenResumable(body, 1024, Task.Delay(250, Cancel)));

        await using var client = ClientFor(gateway);
        await client.Batch.DownloadAsync(Parameters(directory), Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(body, await File.ReadAllBytesAsync(directory.FileAt(BatchFixture.DataFilename), Cancel));

        var downloads = DownloadRequests(gateway);
        Assert.Equal(2, downloads.Count);
        Assert.False(downloads[0].Headers.ContainsKey("Range"));
        Assert.Equal("bytes=1024-", downloads[1].Headers["Range"]);
    }

    /// <summary>
    /// A transfer that keeps failing gives up after <see cref="BatchClient.MaximumRetries"/>
    /// retries — six attempts in all, which is upstream's counting rule.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The response resets before a single byte, so no attempt makes any progress and the counter
    /// never resets. That is the only shape in which the limit is reachable at an exact count —
    /// and finding that out was worth a paragraph.
    /// </para>
    /// <para>
    /// <b>An earlier version of this test dropped after 512 bytes and asserted six requests; it
    /// took thirty-three.</b> How many of a 512-byte prefix clear the socket before the reset is a
    /// race, so the partial file's length wobbled from attempt to attempt, and any attempt that
    /// happened to land a byte further than the last one counted as progress and cleared the
    /// counter. The download still terminated — the wobble is bounded by the file's size — but the
    /// count it terminated at was never going to be six. Dropping at zero removes the race
    /// entirely.
    /// </para>
    /// <para>
    /// <see cref="Download_KeepsGoingWhileAFailingTransferIsStillAdvancing"/> pins the opposite
    /// direction. Between them the rule is bounded from both sides: progress resets the counter,
    /// and its absence does not.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Download_GivesUpAfterTheRetryLimit()
    {
        var body = BatchFixture.PatternedBody(4096);

        using var directory = BatchFixture.NewDirectory();
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);

        gateway.Get("batch.list_files", MockHistoricalResponse.Json(OneFile(body)));
        gateway.Get(
            BatchFixture.DownloadSlug(BatchFixture.DataFilename),
            MockHistoricalResponse.Dropped(body, 0));

        await using var client = ClientFor(gateway);

        var thrown = await Record.ExceptionAsync(
            () => client.Batch.DownloadAsync(Parameters(directory), Cancel));

        Assert.NotNull(thrown);
        Assert.True(
            thrown is IOException or HttpRequestException,
            $"A transfer that keeps dying should surface the transport failure; it threw {thrown.GetType()}.");
        Assert.Equal(BatchClient.MaximumRetries + 1, DownloadRequests(gateway).Count);
    }

    /// <summary>
    /// A transfer that keeps failing but keeps advancing is not given up on, however many times it
    /// fails — the counter measures <em>consecutive</em> failures, which is upstream's rule.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The link here dies every single time, a little further along on each attempt, so the file
    /// needs eight failures to finish and the budget is five retries. A client that never reset
    /// would stop on the sixth; this one finishes.
    /// </para>
    /// <para>
    /// <b>This is the test that would have caught the reset rule being dead code</b>, which it was:
    /// the first implementation asked a byte counter the transfer incremented, and the transfer
    /// throws, so the counter was never assigned and the reset never fired. Nothing else in this
    /// file notices, because every other response either succeeds on the retry or fails identically
    /// forever — see <see cref="Download_GivesUpAfterTheRetryLimit"/>, which pins the opposite
    /// direction: no lasting progress, no reset.
    /// </para>
    /// <para>
    /// The request count is asserted as a lower bound rather than exactly. Each answer delivers
    /// <em>at most</em> a step before the reset, and whether the last bytes of a step clear the
    /// socket ahead of the reset is a property of two TCP stacks; fewer arriving means more
    /// requests, never fewer. What matters is that it took more failures than the budget allows.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Download_KeepsGoingWhileAFailingTransferIsStillAdvancing()
    {
        const int step = 1024;

        var body = BatchFixture.PatternedBody(step * 8);

        using var directory = BatchFixture.NewDirectory();
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);

        gateway.Get("batch.list_files", MockHistoricalResponse.Json(OneFile(body)));
        gateway.Get(
            BatchFixture.DownloadSlug(BatchFixture.DataFilename),
            MockHistoricalResponse.DroppedAtAdvancingOffsets(body, step));

        await using var client = ClientFor(gateway);
        await client.Batch.DownloadAsync(Parameters(directory), Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(body, await File.ReadAllBytesAsync(directory.FileAt(BatchFixture.DataFilename), Cancel));

        var downloads = DownloadRequests(gateway);
        Assert.True(
            downloads.Count > BatchClient.MaximumRetries + 1,
            $"The transfer finished in {downloads.Count} requests, which a client that never reset "
            + $"its retry counter could also have managed — the budget is {BatchClient.MaximumRetries} "
            + "retries. This test only discriminates when it takes more than that.");

        // Every attempt after the first resumed rather than starting over, which is what made the
        // file advance at all.
        Assert.False(downloads[0].Headers.ContainsKey("Range"));
        Assert.All(downloads.Skip(1), request => Assert.StartsWith(
            "bytes=", request.Headers["Range"], StringComparison.Ordinal));
    }

    /// <summary>
    /// A transfer that delivers the last byte and <em>then</em> fails leaves a complete file, and
    /// that file is still checked against its checksum before the download reports success.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one route by which bytes this library wrote could reach a caller unverified. The
    /// size-equal case returns without a request — that is what makes re-running a finished
    /// download free — so a file completed by a failed transfer would take the same exit and skip
    /// the checksum that <see cref="Download_ThrowsWhenTheBodyDoesNotMatchItsAdvertisedChecksum"/>
    /// establishes. Upstream has the hole and does not notice, a mismatch there being a warning.
    /// </para>
    /// <para>
    /// The body served is not the body whose hash is published, so a client that verified reports
    /// it and a client that took the free exit returns a path to bad data.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Download_VerifiesAFileCompletedByATransferThatThenFailed()
    {
        const int step = 1024;

        var body = BatchFixture.PatternedBody(step * 8);
        var otherBody = BatchFixture.PatternedBody(step * 8, seed: 5);

        using var directory = BatchFixture.NewDirectory();
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);

        gateway.Get(
            "batch.list_files",
            MockHistoricalResponse.Json(BatchFixture.OneFileListJson(
                BatchFixture.DataFilename,
                body,
                advertisedHash: $"sha256:{BatchFixture.Sha256Of(otherBody)}")));
        gateway.Get(
            BatchFixture.DownloadSlug(BatchFixture.DataFilename),
            MockHistoricalResponse.DroppedAtAdvancingOffsets(body, step));

        await using var client = ClientFor(gateway);

        var thrown = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.Batch.DownloadAsync(Parameters(directory), Cancel));

        Assert.Contains(BatchFixture.DataFilename, thrown.Message, StringComparison.Ordinal);
        Assert.Equal(
            body.Length, new FileInfo(directory.FileAt(BatchFixture.DataFilename)).Length);
    }

    /// <summary>
    /// A server that answers a <c>Range</c> request with the whole file is recovered from, not
    /// appended to — and it is logged, because it means resumption is silently not working.
    /// </summary>
    /// <remarks>
    /// <b>Upstream has no equivalent check.</b> It opens the output in append mode before it looks
    /// at the status, so the whole file lands on top of the partial one; the result is longer than
    /// expected and fails its checksum, which upstream reports as a warning and returns success
    /// for. Answering <c>200</c> to a <c>Range</c> request is not a server bug — the header is a
    /// request, not a requirement — so this recovers rather than throwing.
    /// </remarks>
    [Fact]
    public async Task Download_StartsOverWhenTheServerIgnoresTheRangeHeader()
    {
        var body = BatchFixture.PatternedBody(4096);
        var logs = new RecordingLoggerFactory();

        using var directory = BatchFixture.NewDirectory();
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);

        gateway.Get("batch.list_files", MockHistoricalResponse.Json(OneFile(body)));
        gateway.Get(
            BatchFixture.DownloadSlug(BatchFixture.DataFilename),
            MockHistoricalResponse.BinaryIgnoringRange(body));

        var path = directory.FileAt(BatchFixture.DataFilename);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, body[..1024], Cancel);

        await using var client = new HistoricalClient
        {
            ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey),
            BaseUrl = gateway.BaseUrl,
            LoggerFactory = logs,
        };

        await client.Batch.DownloadAsync(Parameters(directory), Cancel);

        gateway.ThrowIfRejected();

        // The file is the right length and the right bytes, which appending would have made
        // neither of.
        Assert.Equal(body, await File.ReadAllBytesAsync(path, Cancel));
        Assert.Contains("1024", Assert.Single(logs.EntriesWith(6)).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Files transfer at the same time, which is the second departure from upstream's sequential
    /// loop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Overlap is proved rather than timed.</b> Every file's response writes a prefix and then
    /// waits on one shared gate before resetting, so all three transfers are still open when the
    /// test sees three download requests recorded. A sequential download would record one, wait
    /// for it to finish, and never get there — the poll below would time out and say so, rather
    /// than a stopwatch reading differently on a loaded machine.
    /// </para>
    /// <para>
    /// Opening the gate resets all three connections, each retries with a <c>Range</c>, and each
    /// completes — so this also covers the combination of the two departures with the retry path.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Download_TransfersFilesAtTheSameTime()
    {
        var bodies = Enumerable.Range(0, 3)
            .Select(i => BatchFixture.PatternedBody(4096, seed: i))
            .ToList();
        var names = new[] { "part-0.csv", "part-1.csv", "part-2.csv" };

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var directory = BatchFixture.NewDirectory();
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);

        gateway.Get("batch.list_files", MockHistoricalResponse.Json(ManyFileListJson(names, bodies)));
        for (var i = 0; i < names.Length; i++)
        {
            gateway.Get(
                BatchFixture.DownloadSlug(names[i]),
                MockHistoricalResponse.DroppedThenResumable(bodies[i], 512, gate.Task));
        }

        await using var client = ClientFor(gateway);

        var download = client.Batch.DownloadAsync(
            Parameters(directory) with { MaximumConcurrency = 3 }, Cancel);

        await WaitUntilAsync(
            () => DownloadRequests(gateway).Count == 3,
            "all three transfers to be open at once — a sequential download never gets here");

        gate.SetResult();
        var paths = await download;

        gateway.ThrowIfRejected();
        Assert.Equal(3, paths.Count);
        for (var i = 0; i < names.Length; i++)
        {
            Assert.Equal(bodies[i], await File.ReadAllBytesAsync(paths[i], Cancel));
        }
    }

    /// <summary>One named file is fetched, and the job's other files are not.</summary>
    [Fact]
    public async Task Download_FetchesOnlyTheFileItWasAskedFor()
    {
        var condition = BatchFixture.Utf8("""[{"date":"2022-06-10"}]""");
        var data = BatchFixture.PatternedBody(4096);

        using var directory = BatchFixture.NewDirectory();
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);

        gateway.Get("batch.list_files", MockHistoricalResponse.Json(TwoFileListJson(condition, data)));
        gateway.Get(BatchFixture.DownloadSlug(BatchFixture.ConditionFilename), MockHistoricalResponse.Binary(condition));
        gateway.Get(BatchFixture.DownloadSlug(BatchFixture.DataFilename), MockHistoricalResponse.Binary(data));

        await using var client = ClientFor(gateway);

        var paths = await client.Batch.DownloadAsync(
            Parameters(directory) with { Filename = BatchFixture.DataFilename }, Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(directory.FileAt(BatchFixture.DataFilename), Assert.Single(paths));
        Assert.False(File.Exists(directory.FileAt(BatchFixture.ConditionFilename)));
        Assert.Equal(
            $"/v0/{BatchFixture.DownloadSlug(BatchFixture.DataFilename)}",
            Assert.Single(DownloadRequests(gateway)).Path);
    }

    /// <summary>
    /// A name the job does not have is refused against the file list rather than turned into a URL
    /// and a 404 — upstream's rule, and #39's porting note.
    /// </summary>
    [Fact]
    public async Task Download_RefusesAFilenameTheJobDoesNotHave()
    {
        var body = BatchFixture.PatternedBody(64);

        using var directory = BatchFixture.NewDirectory();
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);

        gateway.Get("batch.list_files", MockHistoricalResponse.Json(OneFile(body)));

        await using var client = ClientFor(gateway);

        var thrown = await Assert.ThrowsAsync<ArgumentException>(
            () => client.Batch.DownloadAsync(
                Parameters(directory) with { Filename = "not-in-the-job.csv" }, Cancel));

        // The message lists what the job does have, so the caller can fix the call without a
        // second round trip.
        Assert.Contains(BatchFixture.DataFilename, thrown.Message, StringComparison.Ordinal);
        Assert.Empty(DownloadRequests(gateway));
    }

    /// <summary>
    /// A server-supplied name carrying a path is refused rather than written. Upstream joins it
    /// unchecked; <see cref="Path.Combine(string, string)"/> with a rooted second argument discards
    /// the first entirely.
    /// </summary>
    [Theory]
    [InlineData("../escaped.csv")]
    [InlineData("nested/file.csv")]
    [InlineData("..")]
    [InlineData("/etc/hosts")]
    public async Task Download_RefusesAServerSuppliedNameThatIsAPath(string filename)
    {
        var body = BatchFixture.PatternedBody(64);

        using var directory = BatchFixture.NewDirectory();
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);

        gateway.Get(
            "batch.list_files",
            MockHistoricalResponse.Json(BatchFixture.OneFileListJson(filename, body)));

        await using var client = ClientFor(gateway);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.Batch.DownloadAsync(Parameters(directory), Cancel));

        Assert.Empty(DownloadRequests(gateway));
    }

    /// <summary>
    /// A file offering no <c>https</c> URL is an error, as upstream's
    /// <c>Missing https URL for batch file</c> is. Nothing here speaks FTP.
    /// </summary>
    [Fact]
    public async Task Download_RefusesAFileWithNoHttpsUrl()
    {
        using var directory = BatchFixture.NewDirectory();
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);

        gateway.Get(
            "batch.list_files",
            MockHistoricalResponse.Json(
                """
                [{"filename":"xnas-itch-20220610.ohlcv-1m.csv","size":4,"hash":"sha256:00",
                  "urls":{"ftp":"ftp://ftp.databento.com/W7KFYTCU/job/file.csv"}}]
                """));

        await using var client = ClientFor(gateway);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.Batch.DownloadAsync(Parameters(directory), Cancel));

        Assert.Contains("ftp", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>The job's directory is created; an existing file at that path is an error.</summary>
    [Fact]
    public async Task Download_RefusesAnOutputDirectoryHoldingAFileWhereTheJobDirectoryBelongs()
    {
        using var directory = BatchFixture.NewDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, BatchFixture.JobId), "not a directory", Cancel);

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        await using var client = ClientFor(gateway);

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.Batch.DownloadAsync(Parameters(directory), Cancel));

        Assert.Empty(gateway.Requests);
    }

    /// <summary>
    /// Every download reaches the loopback gateway, even though every URL in the fixture names
    /// <c>api.databento.com</c> — which is what keeps the API key on the host the caller
    /// configured.
    /// </summary>
    /// <remarks>
    /// Stated as its own test because every other test here depends on it silently. If the client
    /// ever started following the absolute URL, they would all fail with connection errors and the
    /// cause would not be obvious from any of them.
    /// </remarks>
    [Fact]
    public async Task Download_UsesOnlyTheUrlsPathAndNotItsHost()
    {
        var body = BatchFixture.PatternedBody(256);

        using var directory = BatchFixture.NewDirectory();
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);

        gateway.Get("batch.list_files", MockHistoricalResponse.Json(OneFile(body)));
        gateway.Get(BatchFixture.DownloadSlug(BatchFixture.DataFilename), MockHistoricalResponse.Binary(body));

        await using var client = ClientFor(gateway);
        await client.Batch.DownloadAsync(Parameters(directory), Cancel);

        gateway.ThrowIfRejected();

        // The fixture's URL host, which the request did not go to.
        Assert.StartsWith("https://api.databento.com", BatchFixture.DownloadHost, StringComparison.Ordinal);
        Assert.NotEqual("api.databento.com", gateway.BaseUrl.Host);

        var request = Assert.Single(DownloadRequests(gateway));
        Assert.Equal($"/v0/{BatchFixture.DownloadSlug(BatchFixture.DataFilename)}", request.Path);
    }

    /// <summary>
    /// Downloading the same job twice does the work once: the second call finds every file already
    /// complete and makes no download request.
    /// </summary>
    /// <remarks>
    /// Worth its own test because it is the property that makes a download safe to retry after a
    /// crash — and because it is free, a job being billed at submission rather than at transfer.
    /// </remarks>
    [Fact]
    public async Task Download_RunAgainDoesNothingAndCostsNothing()
    {
        var body = BatchFixture.PatternedBody(4096);

        using var directory = BatchFixture.NewDirectory();
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);

        gateway.Get("batch.list_files", MockHistoricalResponse.Json(OneFile(body)));
        gateway.Get(BatchFixture.DownloadSlug(BatchFixture.DataFilename), MockHistoricalResponse.Binary(body));

        await using var client = ClientFor(gateway);

        var first = await client.Batch.DownloadAsync(Parameters(directory), Cancel);
        Assert.Single(DownloadRequests(gateway));

        var second = await client.Batch.DownloadAsync(Parameters(directory), Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(first, second);
        Assert.Single(DownloadRequests(gateway));
    }

    private static List<RecordedRequest> DownloadRequests(MockHistoricalGateway gateway) =>
        gateway.Requests
            .Where(request => request.Path.StartsWith("/v0/batch/download/", StringComparison.Ordinal))
            .ToList();

    private static long LengthOf(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length : 0;
    }

    /// <summary>
    /// Polls <paramref name="condition"/> until it holds, and fails naming
    /// <paramref name="what"/> if it never does.
    /// </summary>
    /// <remarks>
    /// A poll rather than a signal because what is being waited for happens inside the client and
    /// the gateway rather than in the test. The timeout is what turns a regression into a named
    /// failure instead of a hung run.
    /// </remarks>
    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = Environment.TickCount64 + PollTimeoutMilliseconds;

        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                Assert.Fail($"Timed out after {PollTimeoutMilliseconds} ms waiting for {what}.");
            }

            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    private static HistoricalClient ClientFor(MockHistoricalGateway gateway) => new()
    {
        ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey),
        BaseUrl = gateway.BaseUrl,
    };

    private static DownloadParams Parameters(TemporaryDirectory directory) => new()
    {
        OutputDirectory = directory.Path,
        JobId = BatchFixture.JobId,
    };

    private static string OneFile(byte[] body) =>
        BatchFixture.OneFileListJson(BatchFixture.DataFilename, body);

    private static string TwoFileListJson(byte[] condition, byte[] data) =>
        ManyFileListJson(
            [BatchFixture.ConditionFilename, BatchFixture.DataFilename],
            [condition, data]);


    private static string ManyFileListJson(
        IReadOnlyList<string> names,
        List<byte[]> bodies)
    {
        var entries = names.Select((name, i) =>
            BatchFixture.OneFileListJson(name, bodies[i]).Trim('[', ']'));

        return $"[{string.Join(',', entries)}]";
    }
}
