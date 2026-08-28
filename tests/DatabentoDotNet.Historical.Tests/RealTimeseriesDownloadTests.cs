using DatabentoDotNet.Dbn;
using NodaTime;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// Opt-in tests that download <b>real</b> market data from Databento, and therefore <b>cost
/// money</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This class exists because <see cref="RealHistoricalApiTests"/> may not contain it.</b> That
/// class carries a guarantee — nothing in it spends anything, so a key alone is enough to run it —
/// and its own remarks say plainly that a test which quietly grows a download is a test which
/// quietly grows a bill. Keeping the billable calls in a separate type is what keeps that promise
/// checkable by reading the file list. <c>RealGatewaySessionTests</c> stands in the same relation
/// to <c>RealGatewaySmokeTests</c> for M2.
/// </para>
/// <para>
/// <b>Two gates, not one.</b> <c>Category=Historical</c> is filtered out of CI by name, and every
/// test here additionally requires <see cref="HistoricalCredentials.RequestVariable"/> — the flag
/// that shipped with the harness in #44 and, until this file, had no consumer. A configured key
/// means "this developer can reach the API", which is not the same as consent to spend on every
/// <c>dotnet test</c>. CLAUDE.md states the rule: <em>no test spends without its own opt-in</em>.
/// </para>
/// <para>
/// <b>What it costs, measured before it was written.</b> The window below is one nanosecond wide
/// over a schema that produces one bar a day, so a run downloads a single 56-byte record.
/// <c>metadata.get_cost</c> priced it at <c>$0.000009909272</c> — and
/// <see cref="TheDownloadIsPricedBeforeItIsTaken"/> asks that same question again at run time, so
/// the bill is checked rather than remembered.
/// </para>
/// <para>
/// <b>Why <c>ohlcv-1d</c> rather than the configured schema.</b> These tests measure where the
/// range's <c>end</c> falls, and that needs a schema whose records land on a known instant: a daily
/// bar is stamped at exactly UTC midnight. Against <c>trades</c> the same pair of windows returns
/// nothing either way and the experiment cannot run at all. Dataset, symbol and date stay
/// configurable.
/// </para>
/// </remarks>
[Trait("Category", "Historical")]
public class RealTimeseriesDownloadTests
{
    /// <summary>Gate for every <c>SkipUnless</c> in this class: a key <b>and</b> consent to spend.</summary>
    public static bool IsRequestAllowed => HistoricalCredentials.IsRequestAllowed;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static HistoricalClient Client() =>
        new() { ApiKey = HistoricalCredentials.ApiKey };

    /// <summary>Midnight UTC on the configured day — the instant a daily bar is stamped with.</summary>
    private static Instant Midnight => DateRange.OnDay(HistoricalCredentials.Date).ToDateTimeRange().Start;

    private static Duration OneNanosecond => Duration.FromNanoseconds(1L);

    private static GetRangeParams DailyBars(DateTimeRange range) =>
        new()
        {
            Dataset = HistoricalCredentials.Dataset,
            Symbols = Symbols.From(HistoricalCredentials.Symbol),
            Schema = Schema.Ohlcv1D,
            DateTimeRange = range,
        };

    /// <summary>
    /// <b>The whole point of this file.</b> <c>timeseries.get_range</c> reads the range's
    /// <c>end</c> as exclusive — asked of the server rather than inherited from upstream's
    /// documentation, which says so at <c>timeseries.rs:175</c> and is right.
    /// </summary>
    /// <remarks>
    /// <para>
    /// #45 was a documented prior that turned out false, and #46 exists because a prior that turns
    /// out true is still not a probe. So this is measured on the record's own timestamp: the bar is
    /// returned when the window <em>starts</em> on it and withheld when the window <em>ends</em> on
    /// it, which no other reading of the boundary produces.
    /// </para>
    /// <para>
    /// The mock could not settle this. It serves whatever body it is handed, so it agrees with
    /// whatever the client asks for — including a wrong reading of <c>end</c>.
    /// </para>
    /// </remarks>
    [Fact(SkipUnless = nameof(IsRequestAllowed), Skip = HistoricalCredentials.RequestSkipReason)]
    public async Task GetRange_ReadsTheRangeEndAsExclusive()
    {
        await using var client = Client();

        await using (var atTheBar = await client.Timeseries.GetRangeAsync(
            DailyBars(DateTimeRange.Between(Midnight, Midnight + OneNanosecond)), Cancel))
        {
            var records = await ReadAllAsync(atTheBar);

            Assert.True(
                records.Count == 1,
                $"Expected exactly one {Schema.Ohlcv1D.ToWireString()} bar stamped at {Midnight}, and "
                + $"got {records.Count}. Without it this test measures nothing — check that "
                + $"{HistoricalCredentials.Symbol} traded on {HistoricalCredentials.Date}.");

            // The bar's own timestamp is the boundary, so the two windows below sit exactly one
            // nanosecond either side of it rather than approximately so. Compared through the
            // library's own conversion, which is what actually went on the wire.
            Assert.Equal(
                DateTimeRange.Between(Midnight, Midnight + OneNanosecond).StartUnixNanoseconds,
                (long)records[0]);
        }

        await using var endingAtTheBar = await client.Timeseries.GetRangeAsync(
            DailyBars(DateTimeRange.Between(Midnight - OneNanosecond, Midnight)), Cancel);

        Assert.Empty(await ReadAllAsync(endingAtTheBar));
    }

    /// <summary>
    /// An empty result is a well-formed stream, not an error: <c>200</c>, a metadata block, no
    /// records. The API's own warning header says it found nothing, and the client logs it rather
    /// than throwing.
    /// </summary>
    /// <remarks>
    /// The metadata block still echoes the requested range verbatim — so a caller cannot read it to
    /// learn what came back, only what was asked for. That is asserted here because it is the sort
    /// of thing a reader would otherwise assume the other way round.
    /// </remarks>
    [Fact(SkipUnless = nameof(IsRequestAllowed), Skip = HistoricalCredentials.RequestSkipReason)]
    public async Task GetRange_WithNoRecordsInRange_ReturnsAnEmptyStreamRatherThanFailing()
    {
        var range = DateTimeRange.Between(Midnight - OneNanosecond, Midnight);

        await using var client = Client();
        await using var reader = await client.Timeseries.GetRangeAsync(DailyBars(range), Cancel);

        Assert.Empty(await ReadAllAsync(reader));
        Assert.Equal(HistoricalCredentials.Dataset, reader.Metadata.Dataset);
        Assert.Equal(Schema.Ohlcv1D, reader.Metadata.Schema);
    }

    /// <summary>
    /// <see cref="GetRangeParams.ToQuery"/> prices the request that is about to be sent, and the
    /// price is what the download actually costs.
    /// </summary>
    /// <remarks>
    /// This is the property the conversion exists for, and the only test that can check it: the
    /// mock returns whatever cost it is told to. It also keeps this file's own bill honest — a
    /// change that made these tests download materially more would fail here first.
    /// </remarks>
    [Fact(SkipUnless = nameof(IsRequestAllowed), Skip = HistoricalCredentials.RequestSkipReason)]
    public async Task TheDownloadIsPricedBeforeItIsTaken()
    {
        var request = DailyBars(DateTimeRange.Between(Midnight, Midnight + OneNanosecond));

        await using var client = Client();

        var cost = await client.Metadata.GetCostAsync(request.ToQuery(), Cancel);
        var billableBytes = await client.Metadata.GetBillableSizeAsync(request.ToQuery(), Cancel);

        Assert.InRange(cost, 0m, 0.01m);
        Assert.InRange(billableBytes, 1UL, 4096UL);

        await using var reader = await client.Timeseries.GetRangeAsync(request, Cancel);
        Assert.Single(await ReadAllAsync(reader));
    }

    /// <summary>
    /// The file <see cref="TimeseriesClient.GetRangeToFileAsync"/> writes is the server's bytes,
    /// and re-opening it needs no second request.
    /// </summary>
    /// <remarks>
    /// Upstream re-encodes here; this library copies, because it has no record encoder and
    /// deliberately will not. The observable difference is that the file holds the version the API
    /// sent — see that method's remarks. Asserting the reopened file yields the same records as the
    /// live download is what makes "a byte copy is enough" a checked claim rather than a
    /// comfortable one.
    /// </remarks>
    [Fact(SkipUnless = nameof(IsRequestAllowed), Skip = HistoricalCredentials.RequestSkipReason)]
    public async Task GetRangeToFile_WritesAFileThatReopensToTheSameRecords()
    {
        var request = DailyBars(DateTimeRange.Between(Midnight, Midnight + OneNanosecond));
        var path = Path.Combine(Path.GetTempPath(), $"dbn-real-{Guid.NewGuid():N}", "range.dbn.zst");

        try
        {
            await using var client = Client();

            IReadOnlyList<ulong> written;
            await using (var reader = await client.Timeseries.GetRangeToFileAsync(request, path, Cancel))
            {
                written = await ReadAllAsync(reader);
            }

            Assert.Single(written);

            // The body is zstd-framed DBN as served, so the file starts with the Zstandard frame
            // magic rather than with "DBN" — nothing decoded it on the way to disk.
            var bytes = await File.ReadAllBytesAsync(path, Cancel);
            Assert.Equal(new byte[] { 0x28, 0xB5, 0x2F, 0xFD }, bytes[..4]);

            await using var reopened = await TimeseriesClient.OpenFileAsync(path, cancellationToken: Cancel);
            Assert.Equal(written, await ReadAllAsync(reopened));
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
    /// Reads every record's <c>ts_event</c>. Small by construction — every window in this class is
    /// one nanosecond wide.
    /// </summary>
    private static async Task<IReadOnlyList<ulong>> ReadAllAsync(TimeseriesReader reader)
    {
        var timestamps = new List<ulong>();

        while (true)
        {
            while (reader.TryNextRecord(out var record))
            {
                timestamps.Add(record.Header.TsEvent);
            }

            if (await reader.FillBufferAsync(Cancel) == 0)
            {
                return timestamps;
            }
        }
    }
}
