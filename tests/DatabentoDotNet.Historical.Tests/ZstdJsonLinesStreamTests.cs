using System.Text.Json;
using System.Text.Json.Serialization;
using NodaTime;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// Tests for <see cref="HistoricalClient.ReadZstdJsonLinesStreamAsync"/> and
/// <see cref="HistoricalClient.SendZstdJsonLinesStreamAsync"/> — the streaming half of the
/// zstd-JSONL pair, which yields rows as they decompress instead of collecting them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three of these assert a property of the <em>transfer</em> rather than of the values</b>, and
/// none of them could be written against an <c>HttpMessageHandler</c> stub: that no row is
/// materialised ahead of the consumer, that breaking out of the loop closes the socket, and that a
/// cancellation part-way leaves the client usable. All three are conclusions about what crossed a
/// real connection, so they are asserted from the gateway's side —
/// <see cref="MockHistoricalGateway.ClientHungUp"/> — rather than by inspecting the objects the
/// code under test just created.
/// </para>
/// <para>
/// <b>Two more pin the streaming reader to the buffering one instead of asserting it in
/// isolation.</b> Blank-line tolerance and the <c>null</c>-literal rejection are meant to be the
/// same behaviour reached by two routes; asserting each separately would let them drift apart one
/// edit at a time and have both files' tests stay green. Running both readers over the same body
/// and comparing their answers cannot.
/// </para>
/// <para>
/// The buffering reader's own three tests are in <c>HistoricalClientTests</c> and are deliberately
/// untouched: this issue adds a path, it does not replace one.
/// </para>
/// </remarks>
public partial class ZstdJsonLinesStreamTests
{
    private const string ListDatasets = "metadata.list_datasets";
    private const string ListSchemas = "metadata.list_schemas";

    /// <summary>
    /// How long a test waits for the gateway to notice a client that went away.
    /// </summary>
    /// <remarks>
    /// The success path takes about two seconds, and the two seconds are the client's:
    /// <c>SocketsHttpHandler</c> answers the disposal of an unfinished response by trying to drain
    /// the rest of the body so the connection can be pooled, and closes the socket only when that
    /// drain times out. See <see cref="MockHistoricalGateway.ClientHungUp"/>. This budget is
    /// therefore five times what a passing run needs, which is the margin a CI runner gets.
    /// </remarks>
    private const int HangUpBudgetMillis = 10_000;

    private static readonly string[] Rows =
    [
        """{"dataset":"GLBX.MDP3"}""",
        """{"dataset":"XNAS.ITCH"}""",
        """{"dataset":"OPRA.PILLAR"}""",
    ];

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReadZstdJsonLinesStreamAsync_YieldsEveryLineInTheOrderItArrived()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListDatasets, MockHistoricalResponse.ZstdJsonLines(Rows));

        await using var client = ClientFor(gateway);
        using var response = await client.SendAsync(
            HttpMethod.Get, ListDatasets, parameters: null, cancellationToken: Cancel);

        var seen = new List<string?>();
        await foreach (var row in HistoricalClient
            .ReadZstdJsonLinesStreamAsync(response, StreamTestJson.Default.DatasetRow, Cancel))
        {
            seen.Add(row.Dataset);
        }

        gateway.ThrowIfRejected();
        Assert.Equal(["GLBX.MDP3", "XNAS.ITCH", "OPRA.PILLAR"], seen);
    }

    [Fact]
    public async Task SendZstdJsonLinesStreamAsync_SendsAndStreamsInOneCall()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListDatasets, MockHistoricalResponse.ZstdJsonLines(Rows));

        await using var client = ClientFor(gateway);

        var seen = new List<string?>();
        await foreach (var row in client.SendZstdJsonLinesStreamAsync(
            HttpMethod.Get, ListDatasets, parameters: null, StreamTestJson.Default.DatasetRow, Cancel))
        {
            seen.Add(row.Dataset);
        }

        gateway.ThrowIfRejected();
        Assert.Equal(["GLBX.MDP3", "XNAS.ITCH", "OPRA.PILLAR"], seen);
    }

    /// <summary>
    /// The two readers skip the same lines, checked against each other rather than against a list
    /// typed out twice.
    /// </summary>
    [Fact]
    public async Task BothReaders_SkipBlankAndWhitespaceLinesIdentically()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);

        // ZstdJsonLines writes a newline after every element, so "" is an empty line and "   " a
        // whitespace-only one — the two shapes the buffering reader's remarks call out, and the
        // frame ends "…}\n\n" the way a line-oriented writer leaves it.
        gateway.Get(
            ListDatasets,
            MockHistoricalResponse.ZstdJsonLines(
                Rows[0], "   ", Rows[1], "\t", Rows[2], string.Empty));

        await using var client = ClientFor(gateway);

        using var forBuffering = await client.SendAsync(
            HttpMethod.Get, ListDatasets, parameters: null, cancellationToken: Cancel);
        var buffered = await HistoricalClient.ReadZstdJsonLinesAsync(
            forBuffering, StreamTestJson.Default.DatasetRow, Cancel);

        using var forStreaming = await client.SendAsync(
            HttpMethod.Get, ListDatasets, parameters: null, cancellationToken: Cancel);
        var streamed = new List<string?>();
        await foreach (var row in HistoricalClient
            .ReadZstdJsonLinesStreamAsync(forStreaming, StreamTestJson.Default.DatasetRow, Cancel))
        {
            streamed.Add(row.Dataset);
        }

        gateway.ThrowIfRejected();
        Assert.Equal(buffered.Select(row => row.Dataset), streamed);
        Assert.Equal(["GLBX.MDP3", "XNAS.ITCH", "OPRA.PILLAR"], streamed);
    }

    /// <summary>
    /// A line that is the JSON literal <c>null</c> is refused the same way by both readers, down to
    /// the message.
    /// </summary>
    /// <remarks>
    /// The message is compared rather than merely the exception type, because the type is the part
    /// that is hard to get wrong. Two readers that both threw <see cref="JsonException"/> but
    /// described the failure differently would still be two behaviours, and a caller writing a log
    /// line or a support ticket would see the difference before we did.
    /// </remarks>
    [Fact]
    public async Task BothReaders_RejectANullLiteralLineWithTheSameMessage()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListDatasets, MockHistoricalResponse.ZstdJsonLines(Rows[0], "null", Rows[1]));

        await using var client = ClientFor(gateway);

        using var forBuffering = await client.SendAsync(
            HttpMethod.Get, ListDatasets, parameters: null, cancellationToken: Cancel);
        var buffering = await Assert.ThrowsAsync<JsonException>(() =>
            HistoricalClient.ReadZstdJsonLinesAsync(forBuffering, StreamTestJson.Default.DatasetRow, Cancel));

        using var forStreaming = await client.SendAsync(
            HttpMethod.Get, ListDatasets, parameters: null, cancellationToken: Cancel);
        var streaming = await Assert.ThrowsAsync<JsonException>(async () =>
        {
            await foreach (var row in HistoricalClient
                .ReadZstdJsonLinesStreamAsync(forStreaming, StreamTestJson.Default.DatasetRow, Cancel))
            {
                Assert.NotNull(row);
            }
        });

        gateway.ThrowIfRejected();
        Assert.Equal(buffering.Message, streaming.Message);
        Assert.Contains("JSON literal 'null'", streaming.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <see langword="null"/> argument faults at the call, not at the first
    /// <c>MoveNextAsync</c> — the reason both public methods are ordinary methods wrapping a
    /// private iterator rather than iterators themselves.
    /// </summary>
    /// <remarks>
    /// Without the split, neither of these would throw here at all: an iterator method runs no part
    /// of its body until something enumerates it, so a caller who built the enumerable and dropped
    /// it would get silence. That is the failure mode this asserts against, and it is why the test
    /// never enumerates.
    /// </remarks>
    [Fact]
    public async Task StreamingReaders_ValidateTheirArgumentsAtTheCall()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        await using var client = ClientFor(gateway);

        using var response = new HttpResponseMessage();

        Assert.Throws<ArgumentNullException>(() =>
            HistoricalClient.ReadZstdJsonLinesStreamAsync(null!, StreamTestJson.Default.DatasetRow, Cancel));
        Assert.Throws<ArgumentNullException>(() =>
            HistoricalClient.ReadZstdJsonLinesStreamAsync<DatasetRow>(response, null!, Cancel));
        Assert.Throws<ArgumentNullException>(() =>
            client.SendZstdJsonLinesStreamAsync(
                null!, ListDatasets, parameters: null, StreamTestJson.Default.DatasetRow, Cancel));
        Assert.Throws<ArgumentException>(() =>
            client.SendZstdJsonLinesStreamAsync(
                HttpMethod.Get, string.Empty, parameters: null, StreamTestJson.Default.DatasetRow, Cancel));

        Assert.Empty(gateway.Requests);
    }

    /// <summary>
    /// Building the enumerable sends nothing: the request is issued from inside the iterator, which
    /// is what makes the response's lifetime the enumerator's.
    /// </summary>
    [Fact]
    public async Task SendZstdJsonLinesStreamAsync_NeverEnumerated_SendsNoRequest()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListDatasets, MockHistoricalResponse.ZstdJsonLines(Rows));

        await using var client = ClientFor(gateway);

        var rows = client.SendZstdJsonLinesStreamAsync(
            HttpMethod.Get, ListDatasets, parameters: null, StreamTestJson.Default.DatasetRow, Cancel);

        Assert.Empty(gateway.Requests);
        GC.KeepAlive(rows);
    }

    /// <summary>
    /// Breaking after the first row leaves the rest of the body unread: the gateway sends one
    /// decodable block and then holds the connection, and the loop still gets its row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The proof is that this test finishes.</b> The gateway has written exactly
    /// <c>FlushPoints[0]</c> bytes — one complete zstd block, which decompresses to line one and
    /// nothing else — and is parked on a signal that never comes. A reader that materialised even
    /// one row ahead of the consumer would be blocked inside the first <c>MoveNextAsync</c> waiting
    /// for bytes the server has not sent, and would sit there until
    /// <see cref="MockHistoricalGateway.Timeout"/> gave up.
    /// </para>
    /// <para>
    /// <b>The flush is what makes the prefix mean anything</b> — see
    /// <see cref="MockHistoricalResponse.ZstdJsonLinesFlushedPerLine"/>. A frame compressed in one
    /// shot has no decodable prefix at all, so the same test written against
    /// <see cref="MockHistoricalResponse.ZstdJsonLines"/> would hang whatever the reader did.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SendZstdJsonLinesStreamAsync_MaterialisesNoRowAheadOfTheConsumer()
    {
        var stalled = new TaskCompletionSource();
        await using var gateway = await StartStalledAfterFirstRowAsync(stalled.Task);
        await using var client = ClientFor(gateway);

        var seen = new List<string?>();
        await foreach (var row in client.SendZstdJsonLinesStreamAsync(
            HttpMethod.Get, ListDatasets, parameters: null, StreamTestJson.Default.DatasetRow, Cancel))
        {
            seen.Add(row.Dataset);
            break;
        }

        gateway.ThrowIfRejected();
        Assert.Equal(["GLBX.MDP3"], seen);
    }

    /// <summary>
    /// Breaking out of the enumeration disposes the response and the decompression stream, proved
    /// by the gateway watching the connection go rather than by looking at our own objects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same stalled response as the test above, read for its other half. The gateway is parked
    /// mid-transfer on a signal this test never completes, so the only thing that can end its wait
    /// is the client closing the socket — and the only thing that closes the socket is the
    /// <see langword="using"/> inside the iterator being unwound by
    /// <c>IAsyncEnumerator.DisposeAsync</c>, which <c>await foreach</c> runs on its way out of the
    /// <c>break</c>. A version of the client that hoisted the send out of the iterator would still
    /// pass every other test in this file and would fail this one.
    /// </para>
    /// <para>
    /// The client is deliberately still alive at the assertion: disposing it would close the
    /// connection too, and the test would then pass for the wrong reason.
    /// </para>
    /// <para>
    /// <b>The count is asserted, not only the latch.</b>
    /// <see cref="MockHistoricalGateway.ClientHungUp"/> is per-gateway and never resets, so on a
    /// gateway with a second route it would answer for whichever client went first. Reading
    /// <see cref="MockHistoricalGateway.ClientHungUpCount"/> either side of the enumeration says
    /// that <em>this</em> connection closed and that exactly one did, and it keeps saying so if
    /// somebody adds a route to <see cref="StartStalledAfterFirstRowAsync"/> later.
    /// </para>
    /// <para>
    /// <b>And it waits for the handler before reading it, which is the difference between an
    /// assertion and a coincidence.</b> The gateway notices this hang-up twice — the connection
    /// closing, and the handler checking the request's abort state on its way out — and collapses
    /// the pair to one. Those two land milliseconds apart, and the second lands <em>after</em> the
    /// client's own call has returned, so a test that reads the count the moment the loop exits
    /// reads it between them and sees <c>1</c> whether or not anything collapses anything. Written
    /// that way the assertion could not fail: removing the gateway's dedupe left the whole project
    /// green three runs out of three, and inserting a half-second sleep before the read turned it
    /// straight into <c>Expected 1, Actual 2</c>. <see cref="MockHistoricalGateway.Idle"/> is that
    /// sleep made deterministic — it is the handler having actually finished rather than a guess at
    /// how long it takes.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SendZstdJsonLinesStreamAsync_BreakingOut_ClosesTheConnection()
    {
        var stalled = new TaskCompletionSource();
        await using var gateway = await StartStalledAfterFirstRowAsync(stalled.Task);
        await using var client = ClientFor(gateway);

        Assert.Equal(0, gateway.ClientHungUpCount);
        Assert.False(gateway.ClientHungUp.IsCompleted);

        await foreach (var row in client.SendZstdJsonLinesStreamAsync(
            HttpMethod.Get, ListDatasets, parameters: null, StreamTestJson.Default.DatasetRow, Cancel))
        {
            Assert.Equal("GLBX.MDP3", row.Dataset);
            break;
        }

        var observed = await Task.WhenAny(gateway.ClientHungUp, Task.Delay(HangUpBudgetMillis, Cancel));

        gateway.ThrowIfRejected();
        Assert.True(
            observed == gateway.ClientHungUp,
            "Breaking out of the enumeration should dispose the response and close the connection, "
            + $"and the gateway saw nothing close within {HangUpBudgetMillis} ms. Either the "
            + "response outlived the enumerator or the send was hoisted out of the iterator.");

        // Only now is the count worth reading: the handler has run its own hang-up check and
        // finished. See this test's remarks for what reading it any earlier would prove.
        // Held in a local: Idle answers freshly each time it is asked, so comparing WhenAny's
        // result against a second read of the property compares a pending task with the completed
        // one the same property returns a moment later, and never matches.
        var idle = gateway.Idle;
        var settled = await Task.WhenAny(idle, Task.Delay(HangUpBudgetMillis, Cancel));
        Assert.True(
            settled == idle,
            $"The gateway's handler had not finished {HangUpBudgetMillis} ms after the client hung "
            + "up, so the hang-up count cannot be read yet.");

        // Read once. The message has to report the number that was asserted, not a second read of
        // a property another thread is free to move — which is the same mistake, one level down,
        // as comparing WhenAny's result against a second read of Idle.
        var hangUps = gateway.ClientHungUpCount;
        Assert.True(
            hangUps == 1,
            $"One client hung up once, so the gateway should have counted one — it counted {hangUps}. "
            + "Two means the connection closing and the handler's own abort check were both counted "
            + "for the same request, which is what the gateway's marker on HttpContext.Items exists "
            + "to collapse.");
    }

    /// <summary>
    /// Cancelling part-way through throws <see cref="OperationCanceledException"/> and leaves the
    /// client fit for the next request.
    /// </summary>
    /// <remarks>
    /// Both halves matter and the second is the one worth the test. A cancellation that tore down
    /// the shared <see cref="HttpClient"/> — or left a connection wedged in its pool — would show
    /// up nowhere except on the request after it, which is why this makes one against a second
    /// route on the same client instance rather than stopping at the throw.
    /// </remarks>
    [Fact]
    public async Task SendZstdJsonLinesStreamAsync_CancelledMidEnumeration_ThrowsAndLeavesTheClientUsable()
    {
        var stalled = new TaskCompletionSource();
        await using var gateway = await StartStalledAfterFirstRowAsync(stalled.Task);
        gateway.Get(ListSchemas, MockHistoricalResponse.ZstdJsonLines(Rows[1]));

        await using var client = ClientFor(gateway);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var seen = new List<string?>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var row in client.SendZstdJsonLinesStreamAsync(
                HttpMethod.Get, ListDatasets, parameters: null, StreamTestJson.Default.DatasetRow,
                cancellation.Token))
            {
                seen.Add(row.Dataset);

                // The gateway is holding the connection open with nothing more to give, so the next
                // MoveNextAsync is a read that will never complete on its own. Cancelling here is
                // therefore cancelling mid-enumeration in the only sense that is not a race.
                await cancellation.CancelAsync();
            }
        });

        Assert.Equal(["GLBX.MDP3"], seen);

        var afterwards = new List<string?>();
        await foreach (var row in client.SendZstdJsonLinesStreamAsync(
            HttpMethod.Get, ListSchemas, parameters: null, StreamTestJson.Default.DatasetRow, Cancel))
        {
            afterwards.Add(row.Dataset);
        }

        gateway.ThrowIfRejected();
        Assert.Equal(["XNAS.ITCH"], afterwards);
    }

    /// <summary>
    /// A gateway serving <see cref="Rows"/> as one flushed block per line, cut off after the first
    /// block and then held open until <paramref name="release"/> completes.
    /// </summary>
    /// <param name="release">
    /// What the gateway waits for before ending the transfer. Every caller here passes a task that
    /// is never completed, so the wait ends only when the client hangs up — which is the event
    /// under test.
    /// </param>
    /// <returns>The running gateway.</returns>
    private static async Task<MockHistoricalGateway> StartStalledAfterFirstRowAsync(Task release)
    {
        var flushed = MockHistoricalResponse.ZstdJsonLinesFlushedPerLine(Rows);
        Assert.Equal(Rows.Length, flushed.FlushPoints.Count);

        var gateway = await MockHistoricalGateway.StartAsync(Cancel);

        // Well under xunit's own budget, so a reader that reads ahead — and therefore waits for
        // bytes that are not coming — fails these tests in seconds with the gateway's own
        // truncated-frame answer rather than by stalling the run.
        gateway.Timeout = Duration.FromSeconds(5);
        gateway.Get(
            ListDatasets,
            MockHistoricalResponse.Dropped(flushed.Body, flushed.FlushPoints[0], release));

        return gateway;
    }

    /// <summary>One row of the JSONL body. See <c>HistoricalClientTests.DatasetRow</c>.</summary>
    private sealed class DatasetRow
    {
        /// <summary>The dataset's code — <c>GLBX.MDP3</c>.</summary>
        public string? Dataset { get; set; }
    }

    /// <summary>
    /// This file's own serialization context, nested and private for the reason
    /// <c>HistoricalClientTests.TestJson</c> gives: a fixture belonging to one file has no business
    /// claiming a name assembly-wide, and coupling two files through a shared
    /// <c>[JsonSerializable]</c> list buys nothing.
    /// </summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(DatasetRow))]
    private sealed partial class StreamTestJson : JsonSerializerContext
    {
    }

    private static HistoricalClient ClientFor(MockHistoricalGateway gateway) => new()
    {
        ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey),
        BaseUrl = gateway.BaseUrl,
    };
}
