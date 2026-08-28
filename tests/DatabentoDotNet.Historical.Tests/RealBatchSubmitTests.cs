using DatabentoDotNet.Dbn;
using NodaTime;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// The one opt-in test that <b>submits a batch job</b> to Databento.
/// </summary>
/// <remarks>
/// <para>
/// <b>This class exists because <see cref="RealBatchApiTests"/> may not contain it.</b> Every other
/// <c>batch.*</c> endpoint is free — a job is billed once at submission and its files stay
/// fetchable for nothing afterwards — so that class runs behind a key alone. Submission is the
/// exception, and keeping it in a separate type is what keeps the free class's promise checkable by
/// reading the file list. <c>RealTimeseriesDownloadTests</c> stands in the same relation to
/// <c>RealHistoricalApiTests</c>, and <c>RealGatewaySessionTests</c> to
/// <c>RealGatewaySmokeTests</c> for M2.
/// </para>
/// <para>
/// <b>Two gates, not one.</b> <c>Category=Historical</c> is filtered out of CI by name, and this
/// test additionally requires <see cref="HistoricalCredentials.RequestVariable"/>. A configured key
/// means "this developer can reach the API", which is not consent to spend on every
/// <c>dotnet test</c>. CLAUDE.md states the rule: <em>no test spends without its own opt-in</em>.
/// </para>
/// <para>
/// <b>And a third gate that is not an environment variable: the job is priced, and the test fails
/// without submitting if the price is not zero.</b> The window below is Databento's free sample
/// data — one day of <c>XNAS.ITCH</c>, one symbol, daily bars — which
/// <c>metadata.get_cost</c> quotes at <c>0.000000000000</c>. That is asserted at run time rather
/// than trusted, so a change to Databento's sample-data policy stops this test rather than being
/// discovered on an invoice.
/// </para>
/// <para>
/// <b>Why the window is hard-coded where the rest of the suite is configurable.</b>
/// <see cref="HistoricalCredentials"/>' dataset and symbol are whatever a developer set, and a
/// submitted job over an arbitrary window costs whatever that window costs. The same argument
/// <c>RealTimeseriesDownloadTests</c> makes for pinning <c>ohlcv-1d</c>: a test that needs a
/// specific property of its input cannot take the input from configuration.
/// </para>
/// <para>
/// <b>It still leaves something behind.</b> A submitted job is a record in the account for about
/// thirty days, free or not. That is the cost of testing a submission at all, and it is why this
/// runs only when asked twice.
/// </para>
/// </remarks>
[Trait("Category", "Historical")]
public class RealBatchSubmitTests
{
    /// <summary>Gate for this class: a key <b>and</b> consent to spend.</summary>
    public static bool IsRequestAllowed => HistoricalCredentials.IsRequestAllowed;

    /// <summary>Databento's free sample dataset.</summary>
    private const string SampleDataset = "XNAS.ITCH";

    /// <summary>A symbol that trades on <see cref="SampleDay"/>.</summary>
    private const string SampleSymbol = "MSFT";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>The day the sample data covers.</summary>
    private static LocalDate SampleDay => new(2022, 6, 10);

    /// <summary>
    /// A submitted job comes back echoing what was asked for, and this library can read all of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The response is the point.</b> <see cref="BatchFixture.JobJson"/> was recorded from
    /// <c>batch.get_job_details</c>, which describes a job that has <em>finished</em>; a job that
    /// has just been submitted is the same type with most of it still empty, and it is the shape
    /// <see cref="BatchClient.SubmitJobAsync"/> actually returns. Nothing else in the suite reads
    /// one — the mock replays a finished job, because a finished job is what was recorded.
    /// </para>
    /// <para>
    /// Which also makes this the first place a freshly-submitted job's <see cref="JobState"/> is
    /// seen. <b>Measured, it is <see cref="JobState.Queued"/></b> — one of the four upstream knows,
    /// so this response is not itself what the seven-state widening is for. The assertion stays a
    /// set rather than that one value: how far a job gets before its response is written is
    /// Databento's business and not a promise, and pinning it would make this test fail on a faster
    /// day rather than on a real change.
    /// </para>
    /// </remarks>
    [Fact(SkipUnless = nameof(IsRequestAllowed), Skip = HistoricalCredentials.RequestSkipReason)]
    public async Task SubmitJob_SubmitsAJobThatWasPricedAtNothingFirst()
    {
        var request = SampleJob();

        await using var client = new HistoricalClient { ApiKey = HistoricalCredentials.ApiKey };

        // Priced through the conversion the parameters carry, so the quote covers this exact
        // request rather than one assembled beside it.
        var cost = await client.Metadata.GetCostAsync(request.ToQuery(), Cancel);

        Assert.True(
            cost == 0m,
            $"The sample window quoted {cost} USD rather than nothing, so this test is not free any "
            + "more and has not submitted anything. Databento's sample-data policy has changed, or "
            + "the window above no longer falls inside it.");

        var job = await client.Batch.SubmitJobAsync(request, Cancel);

        Assert.NotEmpty(job.Id);
        Assert.Equal(SampleDataset, job.Dataset);
        Assert.Equal(Symbols.From(SampleSymbol), job.Symbols);
        Assert.Equal(Schema.Ohlcv1D, job.Schema);
        Assert.Equal(request.DateTimeRange.Start, job.Start);
        Assert.Equal(request.DateTimeRange.End, job.End);
        Assert.Equal(Encoding.Csv, job.Encoding);
        Assert.Equal(SType.RawSymbol, job.StypeIn);
        Assert.Equal(SType.InstrumentId, job.StypeOut);
        Assert.Equal(Delivery.Download, job.Delivery);

        Assert.Contains(
            job.State,
            new[] { JobState.Received, JobState.Queued, JobState.Processing, JobState.Finalizing, JobState.Done });

        // Submitted moments ago, so its receipt time is recent and its processing has not finished.
        Assert.True(job.ReceivedTimestamp > Instant.FromUtc(2020, 1, 1, 0, 0, 0));
        Assert.Null(job.ProcessDoneTimestamp);

        // And it is fetchable by id, which is what a caller does next.
        var fetched = await client.Batch.GetJobDetailsAsync(job.Id, Cancel);
        Assert.Equal(job.Id, fetched.Id);
    }

    /// <summary>
    /// The sample job: CSV, uncompressed, no splitting — the smallest thing worth submitting.
    /// </summary>
    /// <remarks>
    /// CSV rather than DBN because the <c>pretty_*</c> flags below are text-encoding options and
    /// this is the one place they can be exercised against the real API at all. Compression is left
    /// at <see cref="Compression.None"/> so the resulting job matches the shape
    /// <see cref="BatchFixture.JobJson"/> recorded, <c>null</c> compression included.
    /// </remarks>
    private static SubmitJobParams SampleJob() => new()
    {
        Dataset = SampleDataset,
        Symbols = Symbols.From(SampleSymbol),
        Schema = Schema.Ohlcv1D,
        DateTimeRange = DateRange.OnDay(SampleDay).ToDateTimeRange(),
        Encoding = Encoding.Csv,
        Compression = Compression.None,
        PrettyPx = true,
        PrettyTs = true,
        SplitDuration = SplitDuration.None,
    };
}
