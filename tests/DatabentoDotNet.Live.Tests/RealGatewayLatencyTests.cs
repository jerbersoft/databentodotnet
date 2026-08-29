using DatabentoDotNet.Dbn;
using NodaTime;

namespace DatabentoDotNet.Live.Tests;

/// <summary>
/// The live end-to-end latency measurement (#65): the gateway's send timestamp to the moment the
/// caller has the record, over a real session, reported as percentiles.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one benchmark that cannot run in CI, and the second test in this repository that moves
/// billable data.</b> Latency is a property of the session, so <see cref="MockLiveGateway"/>
/// measures a loopback socket and the mock's own send loop — a number that is real, cheap, and
/// about something else entirely. ROADMAP.md §4 makes that argument for the protocol; this is the
/// same line drawn in a different unit.
/// </para>
/// <para>
/// <b>Almost nothing of the measurement lives here, on purpose.</b>
/// <see cref="LatencyMeasurement"/> holds the collection loop, the exclusion rule, the clock and
/// the report, and <c>LatencyMeasurementTests</c> drives all of it against the mock gateway on
/// every <c>dotnet test</c>. What is left in this file is the part only a real gateway can
/// supply: a session, and the latencies that come out of one. Debugging a report format during the
/// one run that needs an open market would be the wrong way round.
/// </para>
/// <para>
/// <b>Run it like this</b>, during market hours, with the session gate set:
/// </para>
/// <code>
/// DATABENTO_LIVE_SESSION=1 dotnet test tests/DatabentoDotNet.Live.Tests \
///   --filter "FullyQualifiedName~RealGatewayLatencyTests" \
///   --logger "console;verbosity=detailed"
/// </code>
/// <para>
/// The detailed logger is not optional: the deliverable is the report this writes to
/// <see cref="ITestOutputHelper"/>, and at default verbosity a passing test prints none of it.
/// </para>
/// <para>
/// <b>Reported, not asserted — with three exceptions, none of them a latency threshold.</b> #65
/// rules out asserting a latency bound and is right to: a threshold over a network path is a flake
/// generator, and this cannot run in CI to flake in anyway. What is asserted instead is that the
/// measurement is a measurement — that the session negotiated <c>ts_out</c>, that the local clock
/// ran forwards, and that the sample is large enough for the percentiles to mean what they are
/// labelled. A run that collected forty records and printed a p99 would be the confident wrong
/// number this codebase exists to prevent.
/// </para>
/// </remarks>
[Trait("Category", "Live")]
public class RealGatewayLatencyTests
{
    /// <summary>
    /// How many market-data records to measure before closing.
    /// </summary>
    /// <remarks>
    /// Enough that p99 sits well clear of the tail rather than on it, and small enough to be a few
    /// seconds of liquid trading. <see cref="CollectionBudget"/> is what actually bounds the bill;
    /// this bounds the report.
    /// </remarks>
    private const int RecordTarget = 20_000;

    /// <summary>The floor below which the percentiles are not reported as percentiles.</summary>
    private const int MinimumUsableSample = LatencyStatistics.MinimumSamplesForP99;

    /// <summary>
    /// How long to collect for. The first of this and <see cref="RecordTarget"/> to trip ends the
    /// session.
    /// </summary>
    private static readonly Duration CollectionBudget = Duration.FromSeconds(60);

    /// <summary>
    /// The hard stop on the whole test, handshake included. Larger than
    /// <see cref="CollectionBudget"/> so an overrun reads as a cancelled session rather than as a
    /// collection that merely stopped early.
    /// </summary>
    private static readonly Duration SessionBudget = Duration.FromSeconds(120);

    /// <summary>Gate for the <c>SkipUnless</c> below. Both halves must be satisfied.</summary>
    public static bool IsAllowed => LiveCredentials.IsSessionAllowed;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private readonly ITestOutputHelper _output;

    /// <summary>Creates the test, capturing xUnit's output sink for the report.</summary>
    /// <param name="output">Where the report is written.</param>
    public RealGatewayLatencyTests(ITestOutputHelper output) => _output = output;

    [Fact(SkipUnless = nameof(IsAllowed), Skip = LiveCredentials.SessionSkipReason)]
    public async Task Latency_AgainstTheRealGateway_ReportsPercentilesOverAMeasuredSample()
    {
        var symbols = LiveCredentials.Symbols;

        if (!WireStrings.TryParseSchema(LiveCredentials.Schema, out var schema))
        {
            Assert.Fail(
                $"{LiveCredentials.SchemaVariable}='{LiveCredentials.Schema}' is not a DBN schema this "
                + "build knows. Use a wire spelling such as 'trades' or 'mbp-1'.");
        }

        await using var client = new LiveClient
        {
            ApiKey = LiveCredentials.ApiKey,
            Dataset = LiveCredentials.Dataset,

            // The whole measurement hangs off this. Without it the gateway stamps no send time,
            // there is no gateway-side reading to subtract, and the headline series does not exist
            // — which is why the metadata is asserted below rather than trusted.
            SendTsOut = true,

            ConnectTimeout = Duration.FromSeconds(15),
            AuthTimeout = Duration.FromSeconds(15),

            // No heartbeat interval, and that is the opposite choice from RealGatewaySessionTests.
            // That test asks for heartbeats so it passes at 3am on a closed market; this one wants
            // market data and nothing else. A heartbeat arrives on an idle socket, which is the
            // best case the transport ever has — LatencyMeasurement excludes them from the sample
            // for that reason, so asking for more of them would only spend the read budget slower.
            ReadTimeout = Duration.FromSeconds(30),
        };

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        deadline.CancelAfter(checked((int)SessionBudget.TotalMilliseconds));

        await client.ConnectAsync(deadline.Token);
        await client.AuthenticateAsync(deadline.Token);
        await client.SubscribeAsync(
            new Subscription { Schema = schema, Symbols = Symbols.From(symbols) },
            deadline.Token);

        // ------------------------------------------------------------------ The billable line

        var startedAt = SystemClock.Instance.GetCurrentInstant();
        var metadata = await client.StartAsync(deadline.Token);

        Assert.True(
            metadata.TsOut,
            "The session did not negotiate ts_out, so the gateway is stamping no send time and the "
            + "headline series has no gateway-side reading to subtract from. SendTsOut was requested; "
            + "the gateway's own metadata block says it was not granted.");

        LatencyMeasurement measurement;
        try
        {
            measurement = await LatencyMeasurement.CollectAsync(
                client, RecordTarget, CollectionBudget, deadline.Token);
        }
        catch (OperationCanceledException)
        {
            // SessionBudget tripped inside a read, which ends the session. There is no partial
            // measurement to recover — CollectAsync deliberately does not swallow this — so report
            // the cause rather than an empty sample.
            Assert.Fail(
                $"The session ran past its {SessionBudget.TotalSeconds:F0} s budget and was cancelled "
                + "mid-read, so no sample survived. That is a slow or stalled gateway rather than a "
                + "closed market: a closed market returns records slowly, it does not hang.");
            return;
        }

        var finishedAt = SystemClock.Instance.GetCurrentInstant();
        await client.CloseAsync();
        Assert.False(client.IsConnected);
        Assert.False(client.IsSessionStarted);

        // ------------------------------------------------------------------------- The report

        _output.WriteLine(measurement.Render(new LatencyRunContext
        {
            Dataset = metadata.Dataset,
            Schema = LiveCredentials.Schema,
            Symbols = symbols,
            DbnVersion = metadata.Version,
            TsOutNegotiated = metadata.TsOut,
            StartedAt = startedAt,
            FinishedAt = finishedAt,
        }));

        // ---------------------------------------------------------- What makes it a measurement

        // The instrument, not the result. Both stamps in this series come from the same monotonic
        // source on this machine, so a negative one is not slow data or a skewed clock — it is the
        // stopwatch running backwards, and every figure in the report above would be worthless.
        Assert.True(
            measurement.Drain.TrueForAll(nanoseconds => nanoseconds >= 0),
            "A record was delivered before the buffer read that produced it. Both timestamps come "
            + "from the same monotonic clock, so this is a broken instrument rather than a "
            + "measurement, and nothing else in this report can be trusted.");

        Assert.True(
            measurement.Count >= MinimumUsableSample,
            $"Collected {measurement.Count} market-data record(s) in "
            + $"{(finishedAt - startedAt).TotalSeconds:F1} s, which is below the {MinimumUsableSample} "
            + "a p99 needs to be a p99 rather than a synonym for the maximum. The usual cause is a "
            + $"closed market: this subscribes to {string.Join(", ", symbols)} on "
            + $"{LiveCredentials.Dataset} '{LiveCredentials.Schema}' and measures what arrives. Run it "
            + $"during that venue's trading hours, or set {LiveCredentials.SymbolsVariable} to symbols "
            + "that are trading. The report above shows what was collected.");
    }
}
