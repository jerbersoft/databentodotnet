using DatabentoDotNet.Dbn;
using NodaTime;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// Tests for <see cref="TimeseriesClient"/> and <see cref="TimeseriesReader"/> against
/// <see cref="MockHistoricalGateway"/>.
/// </summary>
/// <remarks>
/// <para>
/// The bodies are Databento's own — see <see cref="TimeseriesFixture"/> for why nothing here
/// encodes DBN, and for how a stream longer than the read buffer is built without one.
/// </para>
/// <para>
/// <b>What this file cannot settle.</b> The mock and the client were written from the same reading
/// of Databento's documentation, so a misreading of the request shape would sit in both and they
/// would agree. <c>RealHistoricalApiTests</c> is where <c>get_range</c> is asked of the real API,
/// behind its own opt-in because it is the first thing in this repo that spends money.
/// </para>
/// </remarks>
public sealed class TimeseriesClientTests
{
    private static readonly DateTimeRange Range = DateTimeRange.Between(
        Instant.FromUtc(2023, 7, 4, 0, 0, 0), Instant.FromUtc(2023, 7, 5, 0, 0, 0));

    private const string Slug = "timeseries.get_range";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// The whole request, through the transport and Kestrel: upstream's nine form fields in
    /// upstream's order, and the one <c>Accept</c> in the API that is not JSON.
    /// </summary>
    [Fact]
    public async Task GetRange_PostsUpstreamsFormAndAsksForOctetStream()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(Slug, MockHistoricalResponse.Binary(TimeseriesFixture.Compressed()));
        await using var client = ClientFor(gateway);

        await using (await client.Timeseries.GetRangeAsync(Params(), Cancel))
        {
        }

        gateway.ThrowIfRejected();
        var request = gateway.Requests[0];

        Assert.Equal("POST", request.Method);
        Assert.Equal(MockHistoricalGateway.PathFor(Slug), request.Path);
        Assert.Empty(request.Query);
        Assert.Equal("GLBX.MDP3", request.Form["dataset"]);
        Assert.Equal("trades", request.Form["schema"]);
        Assert.Equal("dbn", request.Form["encoding"]);
        Assert.Equal("zstd", request.Form["compression"]);
        Assert.Equal("raw_symbol", request.Form["stype_in"]);
        Assert.Equal("instrument_id", request.Form["stype_out"]);
        Assert.Equal("ESH4", request.Form["symbols"]);
        Assert.Equal("1688428800000000000", request.Form["start"]);
        Assert.Equal("1688515200000000000", request.Form["end"]);
        Assert.False(request.Form.ContainsKey("limit"));

        Assert.Contains(TimeseriesClient.BinaryMediaType, request.Headers["Accept"], StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>stype_out</c> is the field that made <see cref="GetRangeParams"/> a separate type from
    /// <see cref="MetadataQueryParams"/>, so a test that never varies it would not notice it being
    /// dropped on the way to the wire.
    /// </summary>
    [Fact]
    public async Task GetRange_SendsAStypeOutOtherThanTheDefault()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(Slug, MockHistoricalResponse.Binary(TimeseriesFixture.Compressed()));
        await using var client = ClientFor(gateway);

        await using (await client.Timeseries.GetRangeAsync(
            Params() with { StypeOut = SType.RawSymbol, Limit = 7 }, Cancel))
        {
        }

        gateway.ThrowIfRejected();
        Assert.Equal("raw_symbol", gateway.Requests[0].Form["stype_out"]);
        Assert.Equal("7", gateway.Requests[0].Form["limit"]);
    }

    /// <summary>
    /// The metadata block and every record, off a real vendored body served over a real socket.
    /// </summary>
    [Fact]
    public async Task GetRange_DecodesTheMetadataAndEveryRecord()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(Slug, MockHistoricalResponse.Binary(TimeseriesFixture.Compressed()));
        await using var client = ClientFor(gateway);

        await using var reader = await client.Timeseries.GetRangeAsync(Params(), Cancel);

        Assert.Equal("GLBX.MDP3", reader.Metadata.Dataset);
        Assert.Equal(TimeseriesFixture.RecordCount, await CountAsync(reader));
    }

    /// <summary>
    /// The <see cref="IAsyncEnumerable{T}"/> surface yields the same records as the zero-copy pair
    /// it is written in terms of.
    /// </summary>
    [Fact]
    public async Task ReadRecordsAsync_YieldsTheSameRecordsAsTheManualLoop()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(Slug, MockHistoricalResponse.Binary(TimeseriesFixture.Compressed()));
        await using var client = ClientFor(gateway);

        await using var reader = await client.Timeseries.GetRangeAsync(Params(), Cancel);

        var timestamps = new List<ulong>();
        await foreach (var record in reader.ReadRecordsAsync(Cancel))
        {
            timestamps.Add(record.Header.TsEvent);
        }

        Assert.Equal(TimeseriesFixture.RecordCount, timestamps.Count);
        Assert.Equal(timestamps.OrderBy(value => value), timestamps);
    }

    /// <summary>
    /// <b>An empty range is a stream, not an error.</b> The real API answers one with <c>200</c>, a
    /// metadata block, no records, and a warning header — probed in #38. The reader yields nothing
    /// and throws nothing, and the warning reaches the log rather than the caller.
    /// </summary>
    [Fact]
    public async Task GetRange_WithNoRecords_YieldsNothingAndDoesNotThrow()
    {
        var source = TimeseriesFixture.Plain();
        var metadataOnly = TimeseriesFixture.Compress(
            source.AsSpan(0, TimeseriesFixture.MetadataLength(source)));

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(
            Slug,
            MockHistoricalResponse.Binary(metadataOnly)
                .WithWarnings("Warning: No data found for the request you submitted."));
        await using var client = ClientFor(gateway);

        await using var reader = await client.Timeseries.GetRangeAsync(Params(), Cancel);

        Assert.NotNull(reader.Metadata);
        Assert.Equal(0, await CountAsync(reader));
    }

    /// <summary>
    /// <b>A truncated download is an exception, not a short read.</b> The body is a valid zstd
    /// frame holding a DBN stream cut off inside its last record — so the decompressor is happy and
    /// only the decoder can notice. <see cref="DbnDecoder.TryNextRecord"/> would return the whole
    /// records and report success, which is the right call for a local fragment and the wrong one
    /// for a download.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(47)]
    public async Task GetRange_WhenTheStreamStopsInsideARecord_Throws(int missingBytes)
    {
        var (body, wholeRecords) = TimeseriesFixture.TruncatedMidRecord(missingBytes);

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(Slug, MockHistoricalResponse.Binary(body));
        await using var client = ClientFor(gateway);

        await using var reader = await client.Timeseries.GetRangeAsync(Params(), Cancel);

        var seen = 0;
        var thrown = await Assert.ThrowsAsync<DbnDecodeException>(async () =>
        {
            while (true)
            {
                while (reader.TryNextRecord(out _))
                {
                    seen++;
                }

                if (await reader.FillBufferAsync(Cancel) == 0)
                {
                    return;
                }
            }
        });

        // The records that did arrive are still handed over before the failure — the caller is told
        // the stream was cut, not denied what it already read.
        Assert.Equal(wholeRecords, seen);
        Assert.Contains("truncated", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>The control for the test above.</b> The same loop over an untruncated body must not
    /// throw — otherwise the assertion there would pass for any stream at all, which is the way a
    /// truncation test most easily fools itself.
    /// </summary>
    [Fact]
    public async Task GetRange_WhenTheStreamEndsBetweenRecords_DoesNotThrow()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(Slug, MockHistoricalResponse.Binary(TimeseriesFixture.Compressed()));
        await using var client = ClientFor(gateway);

        await using var reader = await client.Timeseries.GetRangeAsync(Params(), Cancel);

        Assert.Equal(TimeseriesFixture.RecordCount, await CountAsync(reader));
    }

    /// <summary>
    /// <b>Refilling before draining must not be mistaken for a truncation.</b> Opening a reader
    /// reads whole buffers, so on a body this small the metadata read already pulled every record
    /// in and left the source at its end. A caller who calls <see cref="TimeseriesReader.FillBufferAsync"/>
    /// first — a reasonable thing to do — therefore hits end-of-source with a buffer full of
    /// perfectly good records.
    /// </summary>
    /// <remarks>
    /// This is the test that makes the <c>_drained</c> half of the truncation condition
    /// load-bearing. Without it the check reads "ended with bytes left over", which this arrangement
    /// satisfies while nothing is wrong at all, and every small download would fail.
    /// </remarks>
    [Fact]
    public async Task FillBuffer_BeforeAnyRecordIsRead_DoesNotReportTruncation()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(Slug, MockHistoricalResponse.Binary(TimeseriesFixture.Compressed()));
        await using var client = ClientFor(gateway);

        await using var reader = await client.Timeseries.GetRangeAsync(Params(), Cancel);

        // No TryNextRecord yet: the reader has not been told the buffered bytes are incomplete,
        // and must not conclude it on its own.
        Assert.Equal(0, await reader.FillBufferAsync(Cancel));

        var seen = 0;
        while (reader.TryNextRecord(out _))
        {
            seen++;
        }

        Assert.Equal(TimeseriesFixture.RecordCount, seen);
    }

    /// <summary>
    /// The transport half of the same rule: a connection dropped mid-body fails the read rather
    /// than ending it quietly. A chunked response has no <c>Content-Length</c> to check against —
    /// probed in #38 — so this is the only thing that distinguishes a dropped download from a
    /// complete one.
    /// </summary>
    [Fact]
    public async Task GetRange_WhenTheConnectionDropsMidBody_Throws()
    {
        var body = TimeseriesFixture.Compress(TimeseriesFixture.Repeating(400).Bytes);

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(Slug, MockHistoricalResponse.Dropped(body, body.Length / 4));
        await using var client = ClientFor(gateway);

        await Assert.ThrowsAnyAsync<IOException>(async () =>
        {
            await using var reader = await client.Timeseries.GetRangeAsync(Params(), Cancel);
            await CountAsync(reader);
        });
    }

    /// <summary>
    /// <b>The file is the server's bytes, unaltered.</b> Upstream decodes and re-encodes; this
    /// copies. So the assertion is byte equality with the body served, which is a stronger claim
    /// than "it decodes to the same records" and the one that makes the file a faithful cache.
    /// </summary>
    [Fact]
    public async Task GetRangeToFile_WritesTheBodyVerbatimAndReadsBackTheSameRecords()
    {
        var served = TimeseriesFixture.Compressed();
        var path = Path.Combine(Path.GetTempPath(), $"dbn-{Guid.NewGuid():N}", "range.dbn.zst");

        try
        {
            await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
            gateway.Post(Slug, MockHistoricalResponse.Binary(served));
            await using var client = ClientFor(gateway);

            int fromFile;
            await using (var reader = await client.Timeseries.GetRangeToFileAsync(Params(), path, Cancel))
            {
                fromFile = await CountAsync(reader);
            }

            Assert.Equal(served, await File.ReadAllBytesAsync(path, Cancel));
            Assert.Equal(TimeseriesFixture.RecordCount, fromFile);

            // And the file re-opens without another request, which is most of the reason to write
            // one in the first place.
            await using var reopened = await TimeseriesClient.OpenFileAsync(path, cancellationToken: Cancel);
            Assert.Equal(TimeseriesFixture.RecordCount, await CountAsync(reopened));
            Assert.Single(gateway.Requests);
        }
        finally
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } directory && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// A stream whose metadata block never finishes is an error at <c>OpenAsync</c>, not a reader
    /// that yields nothing — there would be no metadata to hand back.
    /// </summary>
    [Fact]
    public async Task GetRange_WhenTheMetadataBlockIsCutShort_Throws()
    {
        var source = TimeseriesFixture.Plain();
        var half = TimeseriesFixture.Compress(source.AsSpan(0, TimeseriesFixture.MetadataLength(source) / 2));

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(Slug, MockHistoricalResponse.Binary(half));
        await using var client = ClientFor(gateway);

        await Assert.ThrowsAsync<DbnDecodeException>(
            () => client.Timeseries.GetRangeAsync(Params(), Cancel));
    }

    /// <summary>
    /// A refused request is a <see cref="DatabentoApiException"/>, the same as on every JSON
    /// endpoint — the binary <c>Accept</c> does not change the error path, because the API answers
    /// errors as JSON regardless.
    /// </summary>
    [Fact]
    public async Task GetRange_WhenTheApiRefuses_ThrowsWithTheCase()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(
            Slug,
            MockHistoricalResponse.BusinessError(
                422, "data_schema_not_available", "That schema is not available.", "https://databento.com/docs"));
        await using var client = ClientFor(gateway);

        var thrown = await Assert.ThrowsAsync<DatabentoApiException>(
            () => client.Timeseries.GetRangeAsync(Params(), Cancel));

        Assert.Equal("data_schema_not_available", thrown.Case);
    }

    /// <summary>Both entry points guard their arguments before building a request.</summary>
    [Fact]
    public async Task BothMethods_RejectNullParametersAndEmptyPaths()
    {
        await using var client = new HistoricalClient
        {
            ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey),
            BaseUrl = new Uri("http://127.0.0.1:1/"),
        };

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.Timeseries.GetRangeAsync(null!, Cancel));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.Timeseries.GetRangeToFileAsync(null!, "out.dbn.zst", Cancel));
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.Timeseries.GetRangeToFileAsync(Params(), string.Empty, Cancel));
    }

    /// <summary>
    /// Disposing the reader releases the response with it, so one <c>await using</c> is enough to
    /// return the connection. A leaked response would keep the socket out of the pool.
    /// </summary>
    [Fact]
    public async Task DisposingTheReader_IsIdempotentAndReleasesTheResponse()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Post(Slug, MockHistoricalResponse.Binary(TimeseriesFixture.Compressed()));
        await using var client = ClientFor(gateway);

        var reader = await client.Timeseries.GetRangeAsync(Params(), Cancel);
        await reader.DisposeAsync();
        await reader.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => reader.TryNextRecord(out _));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await reader.FillBufferAsync(Cancel));
    }

    /// <summary>Drains a reader through the zero-copy pair and counts what it yielded.</summary>
    private static async Task<int> CountAsync(TimeseriesReader reader)
    {
        var count = 0;
        while (true)
        {
            while (reader.TryNextRecord(out _))
            {
                count++;
            }

            if (await reader.FillBufferAsync(Cancel) == 0)
            {
                return count;
            }
        }
    }

    private static GetRangeParams Params() => new()
    {
        Dataset = "GLBX.MDP3",
        Symbols = Symbols.From("ESH4"),
        Schema = Schema.Trades,
        DateTimeRange = Range,
    };

    private static HistoricalClient ClientFor(MockHistoricalGateway gateway) => new()
    {
        ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey),
        BaseUrl = gateway.BaseUrl,
    };
}
