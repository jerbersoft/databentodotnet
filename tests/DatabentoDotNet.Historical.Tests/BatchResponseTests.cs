using System.Text.Json;
using System.Text.Json.Serialization;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Historical.Json;
using NodaTime;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// The three <c>batch.*</c> response types, read from bodies the live API actually sent.
/// </summary>
/// <remarks>
/// <para>
/// <b>This file does not have <c>MetadataResponseTests</c>' weakness.</b> That file says of itself
/// that its bodies are hand-written, so a misread key name would sit in both the fixture and the
/// DTO and they would agree. The bodies here were recorded from <c>hist.databento.com</c> during
/// #39 and transcribed — see <see cref="BatchFixture"/> — so a key this library spells wrongly
/// fails here rather than in an opt-in test somebody has to remember to run.
/// </para>
/// <para>
/// It is also where #39's "a job description round-trips from the documented JSON with every field
/// populated, including the ones nothing else reads" is discharged: a field silently dropped in
/// deserialization is invisible until someone needs it, and the shape of that mistake — an
/// unmatched property skipped without complaint — is identical in serde and in
/// <see cref="System.Text.Json"/>.
/// </para>
/// </remarks>
public sealed partial class BatchResponseTests
{
    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
        Converters = [
            typeof(SchemaJsonConverter),
            typeof(STypeJsonConverter),
            typeof(EncodingJsonConverter),
            typeof(CompressionJsonConverter),
            typeof(SymbolsJsonConverter),
            typeof(InstantJsonConverter),
            typeof(JobStateJsonConverter),
            typeof(SplitDurationJsonConverter),
            typeof(DeliveryJsonConverter),
        ])]
    [JsonSerializable(typeof(BatchJob))]
    [JsonSerializable(typeof(List<BatchJob>))]
    [JsonSerializable(typeof(List<BatchJobSummary>))]
    [JsonSerializable(typeof(List<BatchFileDescription>))]
    // Uniquely named, per the CS8785 note on MetadataResponseTests.ResponseJson: the generator
    // keys its file names off the context's simple name, so two nested contexts sharing one name
    // collide across different outer types.
    private sealed partial class BatchResponseJson : JsonSerializerContext
    {
    }

    /// <summary>
    /// Every one of a job's thirty-three fields, from the body <c>batch.get_job_details</c>
    /// returned.
    /// </summary>
    /// <remarks>
    /// Asserted field by field rather than by comparing to a constructed <see cref="BatchJob"/>,
    /// because a record's equality would let two fields swapped in both the expectation and the
    /// reader pass. The two upstream does not model — <see cref="BatchJob.BillId"/> and
    /// <see cref="BatchJob.Packaging"/> — are asserted here too, at the <c>null</c> the API sent;
    /// <see cref="BillIdAndPackaging_AreReadWhenTheApiPopulatesThem"/> covers the other case.
    /// </remarks>
    [Fact]
    public void BatchJob_ReadsEveryFieldOfARecordedResponse()
    {
        var job = Job(BatchFixture.JobJson);

        Assert.Equal("XNAS-20260825-6T3F5G5TYH", job.Id);
        Assert.Equal("W7KFYTCU", job.UserId);
        Assert.Null(job.BillId);
        Assert.Equal(0m, job.CostUsd);
        Assert.Equal("XNAS.ITCH", job.Dataset);
        Assert.Equal(Symbols.From("MSFT"), job.Symbols);
        Assert.Equal(SType.RawSymbol, job.StypeIn);
        Assert.Equal(SType.InstrumentId, job.StypeOut);
        Assert.Equal(Schema.Ohlcv1M, job.Schema);
        Assert.Equal(Instant.FromUtc(2022, 6, 10, 12, 30, 0), job.Start);
        Assert.Equal(Instant.FromUtc(2022, 6, 10, 14, 0, 0), job.End);
        Assert.Equal(1000UL, job.Limit);
        Assert.Equal(Encoding.Csv, job.Encoding);
        Assert.Equal(Compression.None, job.Compression);
        Assert.False(job.PrettyPx);
        Assert.False(job.PrettyTs);
        Assert.True(job.MapSymbols);
        Assert.False(job.SplitSymbols);
        Assert.Equal(SplitDuration.None, job.SplitDuration);
        Assert.Null(job.SplitSize);
        Assert.Null(job.Packaging);
        Assert.Equal(Delivery.Download, job.Delivery);
        Assert.Equal(90UL, job.RecordCount);
        Assert.Equal(5040UL, job.BilledSize);
        Assert.Equal(5040UL, job.ActualSize);
        Assert.Equal(10578UL, job.PackageSize);
        Assert.Equal(JobState.Done, job.State);
        Assert.Equal(
            Instant.FromUtc(2026, 8, 25, 18, 58, 13) + Duration.FromNanoseconds(23_009_000L),
            job.ReceivedTimestamp);
        Assert.Equal(
            Instant.FromUtc(2026, 8, 25, 18, 58, 33) + Duration.FromNanoseconds(44_278_000L),
            job.QueuedTimestamp);
        Assert.Equal(
            Instant.FromUtc(2026, 8, 25, 18, 58, 43) + Duration.FromNanoseconds(81_175_000L),
            job.ProcessStartTimestamp);
        Assert.Equal(
            Instant.FromUtc(2026, 8, 25, 18, 58, 44) + Duration.FromNanoseconds(96_437_000L),
            job.ProcessDoneTimestamp);
        Assert.Equal(Instant.FromUtc(2026, 9, 24, 19, 0, 0), job.ExpirationTimestamp);
        Assert.Equal((byte)100, job.Progress);
    }

    /// <summary>
    /// The range the job was submitted with, narrowed the way
    /// <see cref="DatasetRange.ToDateTimeRange"/> narrows its own.
    /// </summary>
    [Fact]
    public void BatchJob_NarrowsItsStartAndEndIntoARange()
    {
        var range = Job(BatchFixture.JobJson).ToDateTimeRange();

        Assert.Equal(Instant.FromUtc(2022, 6, 10, 12, 30, 0), range.Start);
        Assert.Equal(Instant.FromUtc(2022, 6, 10, 14, 0, 0), range.End);
    }

    /// <summary>
    /// A timestamp with true nanosecond digits survives, which is the whole reason this repo bans
    /// the BCL date types — a <c>DateTime</c> tick is 100 ns and would truncate the last two.
    /// </summary>
    [Fact]
    public void BatchJob_KeepsNanosecondPrecisionInItsTimestamps()
    {
        var job = Job(BatchFixture.JobJson.Replace(
            "\"ts_received\":\"2026-08-25T18:58:13.023009000Z\"",
            "\"ts_received\":\"2026-08-25T18:58:13.123456789Z\"",
            StringComparison.Ordinal));

        Assert.Equal(
            Instant.FromUtc(2026, 8, 25, 18, 58, 13) + Duration.FromNanoseconds(123_456_789L),
            job.ReceivedTimestamp);
    }

    /// <summary>
    /// The two fields upstream's <c>BatchJob</c> has no room for are read when the API populates
    /// them — which is what "a field silently dropped in deserialisation is invisible until
    /// someone needs it" means in practice.
    /// </summary>
    /// <remarks>
    /// Every job #39 saw carried both as <c>null</c>, so these values are invented and the test is
    /// about the plumbing rather than about the spelling of a real bill id. Without the properties
    /// there is nothing to notice: an unmatched JSON property is skipped in silence.
    /// </remarks>
    [Fact]
    public void BillIdAndPackaging_AreReadWhenTheApiPopulatesThem()
    {
        var job = Job(BatchFixture.JobJson
            .Replace("\"bill_id\":null", "\"bill_id\":\"BILL-42\"", StringComparison.Ordinal)
            .Replace("\"packaging\":null", "\"packaging\":\"zip\"", StringComparison.Ordinal));

        Assert.Equal("BILL-42", job.BillId);
        Assert.Equal("zip", job.Packaging);
    }

    /// <summary>
    /// A just-submitted job omits <c>progress</c> entirely rather than sending <c>null</c>, which
    /// is why that property is not <see langword="required"/>.
    /// </summary>
    /// <remarks>
    /// Upstream carries <c>#[serde(default)]</c> for the same reason. Both spellings have to read
    /// as "not yet", and only a non-required property does that for the absent one.
    /// </remarks>
    [Fact]
    public void Progress_ReadsAsNullWhetherItIsAbsentOrNull()
    {
        Assert.Null(Job(BatchFixture.JobJson
            .Replace(",\"progress\":100", string.Empty, StringComparison.Ordinal)).Progress);

        Assert.Null(Job(BatchFixture.JobJson
            .Replace("\"progress\":100", "\"progress\":null", StringComparison.Ordinal)).Progress);
    }

    /// <summary>
    /// <c>compression</c> and <c>split_duration</c> both spell their "none" as JSON <c>null</c>,
    /// and both properties are <see langword="required"/> — which works only because their
    /// converters turn a <c>null</c> token into a value.
    /// </summary>
    /// <remarks>
    /// The string spellings are asserted beside the <c>null</c>s, because a converter that read
    /// <c>null</c> correctly and lost the string would pass a test that only checked the finding.
    /// </remarks>
    [Fact]
    public void CompressionAndSplitDuration_ReadNullAsNoneAndStringsAsThemselves()
    {
        var asNull = Job(BatchFixture.JobJson);
        Assert.Equal(Compression.None, asNull.Compression);
        Assert.Equal(SplitDuration.None, asNull.SplitDuration);

        var asStrings = Job(BatchFixture.JobJson
            .Replace("\"compression\":null", "\"compression\":\"zstd\"", StringComparison.Ordinal)
            .Replace("\"split_duration\":null", "\"split_duration\":\"week\"", StringComparison.Ordinal));

        Assert.Equal(Compression.Zstd, asStrings.Compression);
        Assert.Equal(SplitDuration.Week, asStrings.SplitDuration);
    }

    /// <summary>
    /// The four shapes a symbol set arrives in, plus the comma-joined string upstream's reader does
    /// not split.
    /// </summary>
    /// <remarks>
    /// The bare string is what #39 measured; the rest are the shapes upstream's untagged helper
    /// declares. The comma case is the departure: a comma is forbidden inside a symbol, so
    /// upstream's one-element list holding <c>"AAPL,MSFT"</c> could not be built here at all.
    /// </remarks>
    [Fact]
    public void Symbols_ReadEveryShapeTheFieldTakes()
    {
        Assert.Equal(Symbols.From("MSFT"), SymbolsFrom("\"MSFT\""));
        Assert.Equal(Symbols.From(["AAPL", "MSFT"]), SymbolsFrom("\"AAPL,MSFT\""));
        Assert.Equal(Symbols.All, SymbolsFrom("\"ALL_SYMBOLS\""));
        Assert.Equal(Symbols.From(["AAPL", "MSFT"]), SymbolsFrom("[\"AAPL\",\"MSFT\"]"));
        Assert.Equal(Symbols.All, SymbolsFrom("[\"ALL_SYMBOLS\"]"));
        Assert.Equal(Symbols.FromIds(3403), SymbolsFrom("3403"));
        Assert.Equal(Symbols.FromIds([1, 2, 3]), SymbolsFrom("[1,2,3]"));
    }

    /// <summary>
    /// The whole-dataset sentinel reads as <see cref="Symbols.All"/> and not as a symbol named
    /// <c>ALL_SYMBOLS</c> — a distinction with a wire consequence, since
    /// <see cref="Symbols.Kind"/> is what <c>SubmitJobParams</c> refuses to split by raw symbol.
    /// </summary>
    [Fact]
    public void Symbols_ReadTheSentinelAsTheWholeDatasetRatherThanAsASymbol()
    {
        Assert.Equal(SymbolsKind.All, SymbolsFrom("\"ALL_SYMBOLS\"").Kind);
        Assert.Equal(SymbolsKind.All, SymbolsFrom("[\"ALL_SYMBOLS\"]").Kind);
        Assert.Equal(SymbolsKind.Symbols, SymbolsFrom("[\"ALL_SYMBOLS\",\"MSFT\"]").Kind);
    }

    /// <summary>
    /// A symbol set that names nothing, or that mixes symbols with ids, is a decode failure rather
    /// than a value — there is no such thing as an empty <see cref="Symbols"/>, so the alternative
    /// is a value that throws somewhere with less context.
    /// </summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("\"\"")]
    [InlineData("[\"MSFT\",3403]")]
    [InlineData("{}")]
    [InlineData("true")]
    public void Symbols_RefuseAShapeThatNamesNothingUsable(string json) =>
        Assert.Throws<JsonException>(() => SymbolsFrom(json));

    /// <summary>
    /// A state, encoding, compression, schema, symbology type or delivery this library cannot name
    /// throws, rather than reading as whichever member happens to be zero.
    /// </summary>
    /// <remarks>
    /// Every one of those zero values is an ordinary member — <see cref="JobState.Received"/>,
    /// <see cref="Encoding.Dbn"/>, <see cref="Compression.None"/>, <see cref="Schema.Mbo"/>,
    /// <see cref="Delivery.Download"/> — so a silent fallback would be indistinguishable from real
    /// data. Throwing is also what makes Databento adding a value visible.
    /// </remarks>
    [Theory]
    [InlineData("\"state\":\"done\"", "\"state\":\"cancelled\"")]
    [InlineData("\"encoding\":\"csv\"", "\"encoding\":\"parquet\"")]
    [InlineData("\"compression\":null", "\"compression\":\"gzip\"")]
    [InlineData("\"schema\":\"ohlcv-1m\"", "\"schema\":\"ohlcv-1ns\"")]
    [InlineData("\"stype_in\":\"raw_symbol\"", "\"stype_in\":\"cusip\"")]
    [InlineData("\"delivery\":\"download\"", "\"delivery\":\"s3\"")]
    [InlineData("\"split_duration\":null", "\"split_duration\":\"fortnight\"")]
    public void AnUnknownEnumValue_IsADecodeFailure(string original, string replacement) =>
        Assert.Throws<JsonException>(
            () => Job(BatchFixture.JobJson.Replace(original, replacement, StringComparison.Ordinal)));

    /// <summary>The unknown state's message names it, so the fix is obvious from the failure.</summary>
    [Fact]
    public void AnUnknownJobState_NamesItselfAndTheSevenTheApiKnows()
    {
        var thrown = Assert.Throws<JsonException>(
            () => Job(BatchFixture.JobJson.Replace(
                "\"state\":\"done\"", "\"state\":\"cancelled\"", StringComparison.Ordinal)));

        Assert.Contains("cancelled", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("finalizing", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>The short form: three fields, and nothing else read from a longer body.</summary>
    [Fact]
    public void BatchJobSummary_ReadsTheThreeFieldsTheShortFormCarries()
    {
        var summaries = JsonSerializer.Deserialize(
            BatchFixture.JobSummaryListJson, BatchResponseJson.Default.ListBatchJobSummary);

        Assert.NotNull(summaries);
        Assert.Equal(4, summaries.Count);
        Assert.Equal("XNAS-20260825-WEF7BHTY4S", summaries[0].Id);
        Assert.Equal(JobState.Done, summaries[0].State);
        Assert.Equal(
            Instant.FromUtc(2026, 8, 25, 18, 58, 13) + Duration.FromNanoseconds(15_707_000L),
            summaries[0].ReceivedTimestamp);
        Assert.Equal("XNAS-20260828-MBMBD89WX7", summaries[3].Id);
    }

    /// <summary>
    /// A file description, including the protocol map — two entries, of which this library uses
    /// one.
    /// </summary>
    [Fact]
    public void BatchFileDescription_ReadsTheProtocolMapAndNotJustTheHttpsEntry()
    {
        var files = JsonSerializer.Deserialize(
            BatchFixture.FileListJson, BatchResponseJson.Default.ListBatchFileDescription);

        Assert.NotNull(files);
        Assert.Equal(2, files.Count);

        var condition = files[0];
        Assert.Equal("condition.json", condition.Filename);
        Assert.Equal(122UL, condition.Size);
        Assert.Equal(
            "sha256:ce5db37329231c02e6b3535878aa9bb57136d9ebacc1e9fa8db611f5b1e08531", condition.Hash);
        Assert.Equal(["https", "ftp"], condition.Urls.Keys);
        Assert.StartsWith("https://api.databento.com/v0/batch/download/", condition.Urls["https"], StringComparison.Ordinal);
        Assert.StartsWith("ftp://", condition.Urls["ftp"], StringComparison.Ordinal);
    }

    /// <summary>
    /// The download URL names a host that is not the API's, which is the fact
    /// <see cref="HistoricalClient.GetPathAsync"/> exists to handle and the reason it keeps only
    /// the path.
    /// </summary>
    /// <remarks>
    /// Recorded, not asserted for its own sake: if Databento ever moves the files onto the API
    /// host, this test says so and the note on <see cref="BatchFileDescription.Urls"/> can be
    /// simplified rather than quietly becoming wrong.
    /// </remarks>
    [Fact]
    public void TheDownloadUrlsHost_IsNotTheApisHost()
    {
        var files = JsonSerializer.Deserialize(
            BatchFixture.FileListJson, BatchResponseJson.Default.ListBatchFileDescription);

        var url = new Uri(files![0].Urls["https"], UriKind.Absolute);

        Assert.Equal("api.databento.com", url.Host);
        Assert.NotEqual(HistoricalGateway.Bo1.ToUri().Host, url.Host);
        Assert.Equal(
            $"/v0/{BatchFixture.DownloadSlug(BatchFixture.ConditionFilename)}", url.AbsolutePath);
    }

    /// <summary>A body missing one of a job's required fields is a decode failure, not a default.</summary>
    [Theory]
    [InlineData("\"id\":\"XNAS-20260825-6T3F5G5TYH\",")]
    [InlineData("\"dataset\":\"XNAS.ITCH\",")]
    [InlineData("\"state\":\"done\",")]
    [InlineData("\"encoding\":\"csv\",")]
    public void AMissingRequiredField_IsADecodeFailure(string field) =>
        Assert.Throws<JsonException>(
            () => Job(BatchFixture.JobJson.Replace(field, string.Empty, StringComparison.Ordinal)));

    private static BatchJob Job(string json) =>
        JsonSerializer.Deserialize(json, BatchResponseJson.Default.BatchJob)
        ?? throw new InvalidOperationException("The fixture deserialized to null.");

    private static Symbols SymbolsFrom(string symbolsJson) =>
        Job(BatchFixture.JobJson.Replace(
            "\"symbols\":\"MSFT\"", $"\"symbols\":{symbolsJson}", StringComparison.Ordinal)).Symbols;
}
