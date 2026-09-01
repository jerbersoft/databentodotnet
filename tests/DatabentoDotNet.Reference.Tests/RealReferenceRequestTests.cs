using System.Net;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Historical;
using NodaTime;

namespace DatabentoDotNet.Reference.Tests;

/// <summary>
/// The opt-in tests that <b>request reference data</b> from Databento, and therefore cost money.
/// </summary>
/// <remarks>
/// <para>
/// <b>This class exists because <see cref="RealReferenceApiTests"/> may not contain it.</b> The two
/// <c>list_*</c> endpoints are unauthenticated documentation — measured, not assumed — so that
/// class runs behind a key alone. The three <c>get_range</c> endpoints and <c>get_last</c>
/// authenticate and return billed rows, and keeping them in a separate type is what keeps the free
/// class's promise checkable by reading the file list. <c>RealBatchSubmitTests</c> stands in the
/// same relation to <c>RealBatchApiTests</c>, and <c>RealGatewaySessionTests</c> to
/// <c>RealGatewaySmokeTests</c>.
/// </para>
/// <para>
/// <b>Two gates, not one.</b> <c>Category=Reference</c> is filtered out of CI by name, and every
/// test here additionally requires <see cref="ReferenceCredentials.RequestVariable"/>. CLAUDE.md
/// states the rule: <em>no test spends without its own opt-in</em>.
/// </para>
/// <para>
/// <b>And a third guard that is not a gate: <c>allocate_isins=false</c>, everywhere.</b> The
/// library's default is <see langword="true"/>, and a request with it set can consume one of an
/// ISIN-limited account's allocations for a symbol it has not seen — a side effect that outlives
/// the run and that re-running does not undo, which is a different and worse thing than the row
/// charge the gate is nominally about. Nothing here reads an ISIN.
/// <see cref="ReferenceCredentials.AllocateIsins"/> is where that choice is stated and the only
/// place these tests take it from.
/// </para>
/// <para>
/// <b>What this class is for.</b> #57 owes three specific answers to three other issues, and none
/// of them is reachable without a real row:
/// </para>
/// <para>
/// 1. <b>Is the range's <c>end</c> exclusive?</b> Upstream's doc comments say so and nothing had
/// asked — the exact shape of the assumption #45 found <em>false</em> for
/// <c>get_dataset_condition</c>. <see cref="ReferenceDateTimeRange"/> ships documenting the claim
/// as unprobed. <see cref="CorporateActionsGetRange_ReadsTheRangeEndAsExclusive"/> closes it.
/// </para>
/// <para>
/// 2. <b>Is the server's row order already the order upstream sorts into?</b> #52 dropped
/// upstream's client-side sort because a stream has no buffer to rearrange, and whether that is
/// observable depends entirely on this. The mock cannot answer it: it returns the lines it was
/// given. <see cref="CorporateActionsGetRange_ArrivesInTheOrderTheIndexNames"/> asks the server.
/// </para>
/// <para>
/// 3. <b>What magnitudes do the rate fields actually carry?</b> #53 chose <see cref="decimal"/>
/// over upstream's <c>f64</c> and named the risk: a value <see cref="decimal"/> cannot represent
/// would throw where <c>f64</c> approximates.
/// <see cref="TheRateFields_CarryMagnitudesDecimalHoldsComfortably"/> reads real ones.
/// </para>
/// <para>
/// <b>None of the three deciding procedures lives here.</b> <see cref="ReferenceProbe"/> holds the
/// monotonicity check, the boundary arithmetic and the magnitude band, and
/// <c>ReferenceProbeTests</c> drives all three on every <c>dotnet test</c> — no key, no socket, no
/// subscription. What is left in this file is the part only an entitled account can supply: the
/// rows. CLAUDE.md states the rule that split follows — <em>the expensive run is for the fact only
/// it can settle, never for finding out whether the code works</em> — and it was learned here the
/// expensive way. Written inline, the ordering experiment built its failure message by indexing the
/// key list at the first descent, which is <c>-1</c> when there is none; C# evaluates an assertion's
/// message before the assertion, so a correctly sorted response — the outcome #52 needs — threw
/// <see cref="ArgumentOutOfRangeException"/> rather than passing. Nothing caught it, because the
/// only run that would have was the entitled one it exists to inform.
/// </para>
/// <para>
/// <b>A 403 here is an answer, not a mystery — and on the account this was written against, 403 is
/// what every one of these returns.</b> Measured 2026-08-29: all four billed endpoints answered
/// <c>403 license_reference_dataset_no_subscription</c>, which is how #57 discovered that reference
/// data is <em>three</em> subscriptions rather than one, named individually in
/// <c>payload.reference_dataset</c>. <see cref="ReferenceEntitlement"/> lifts that name into the
/// failure message.
/// </para>
/// <para>
/// <b>They fail rather than skip, deliberately.</b> Setting
/// <see cref="ReferenceCredentials.RequestVariable"/> is a statement that these should run; an
/// account that cannot run them cannot answer the three questions above, and a green run would say
/// it had. Eight failures carrying one fact is noisy, and it is the right noise: the alternative is
/// a suite that reports success for having asked nothing. The refusal itself is free — no rows are
/// returned, so nothing is billed — which is the only reason that outcome could be established
/// without an entitled key.
/// </para>
/// </remarks>
[Trait("Category", "Reference")]
public class RealReferenceRequestTests
{
    /// <summary>Gate for every test in this class: a key <b>and</b> consent to spend.</summary>
    public static bool IsRequestAllowed => ReferenceCredentials.IsRequestAllowed;

    /// <summary>
    /// Where the three answers are written down.
    /// </summary>
    /// <remarks>
    /// <b>A verdict this class does not print is an answer nobody can carry back.</b> #57's
    /// definition of done is that the three findings land as comments on #49, #52 and #53 — and a
    /// green checkmark is not a sentence that can be pasted into an issue. Each experiment renders
    /// what it measured before it asserts on it, so the run produces the comment whichever way the
    /// assertion goes. <c>RealGatewayLatencyTests</c> reports through the same seam and for the
    /// same reason; at default verbosity a passing test prints none of it, so
    /// <c>-l "console;verbosity=detailed"</c> is what the operator wants here.
    /// </remarks>
    private readonly ITestOutputHelper _output;

    /// <summary>Creates the fixture. xunit supplies the output helper.</summary>
    /// <param name="output">Where the three verdicts are rendered.</param>
    public RealReferenceRequestTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// A syntactically valid key that is not a real one. Unlike against the documentation
    /// endpoints, <c>get_range</c> refuses this.
    /// </summary>
    private static readonly string NotARealKey = "db-" + new string('0', ApiKey.Length - 3);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static ReferenceClient Client() => new() { ApiKey = ReferenceCredentials.ApiKey };

    private static Symbols Queried => Symbols.From(ReferenceCredentials.Symbol);

    private static CorporateActionsGetRangeParams CorporateActionsQuery(
        ReferenceDateTimeRange? range = null,
        CorporateActionIndex index = CorporateActionIndex.EventDate) =>
        new()
        {
            Symbols = Queried,
            DateTimeRange = range ?? ReferenceCredentials.Range,
            Index = index,
            AllocateIsins = ReferenceCredentials.AllocateIsins,
        };

    // ----------------------------------------------------------------------------------------
    // The three endpoints answer, and this library can read what they send.
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// <c>corporate_actions.get_range</c> answers with rows, and every one of them binds.
    /// </summary>
    /// <remarks>
    /// <b>The parse is most of the test.</b> <see cref="CorporateAction"/> has 104 fields, 23 of
    /// them <c>required</c>, and reading a live row runs every closed enum converter, both
    /// timestamp formats, all twenty-four dates and the three open maps against bytes this
    /// repository did not write. #55 built that model against upstream's own single fixture row and
    /// four rows of this repository's own construction; this is the first time it meets the server.
    /// </remarks>
    [Fact(SkipUnless = nameof(IsRequestAllowed), Skip = ReferenceCredentials.RequestSkipReason)]
    public async Task CorporateActionsGetRange_AnswersWithRowsThisLibraryCanRead()
    {
        var rows = await CorporateActions(CorporateActionsQuery());

        Assert.NotEmpty(rows);

        foreach (var row in rows)
        {
            // The required fields, which the deserializer would already have refused a row without
            // — asserted anyway because "required" is a compile-time contract and this is the one
            // place it is checked against a server rather than against a fixture.
            Assert.NotEmpty(row.SecurityId);
            Assert.NotEmpty(row.EventDateLabel);
            Assert.True(row.Event.HasValue, "A row arrived with no event code.");
            Assert.NotEqual(default, row.TsRecord);

            // The three open maps are required fields, so a row missing one is an error rather
            // than an empty map — #55's finding, and this is where the server gets to disagree.
            Assert.NotNull(row.DateInfo);
            Assert.NotNull(row.RateInfo);
            Assert.NotNull(row.EventInfo);
        }
    }

    [Fact(SkipUnless = nameof(IsRequestAllowed), Skip = ReferenceCredentials.RequestSkipReason)]
    public async Task SecurityMasterGetRange_AnswersWithRowsThisLibraryCanRead()
    {
        await using var client = Client();

        var rows = await ReferenceEntitlement.CollectAsync(
            client.SecurityMaster.GetRangeAsync(
                new SecurityMasterGetRangeParams
                {
                    Symbols = Queried,
                    DateTimeRange = ReferenceCredentials.Range,
                    AllocateIsins = ReferenceCredentials.AllocateIsins,
                },
                Cancel),
            Cancel);

        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
        {
            Assert.NotEmpty(row.SecurityId);
            Assert.NotEmpty(row.ListingId);
            Assert.NotEqual(default, row.TsEffective);
        });
    }

    [Fact(SkipUnless = nameof(IsRequestAllowed), Skip = ReferenceCredentials.RequestSkipReason)]
    public async Task SecurityMasterGetLast_AnswersWithTheCurrentRowForTheSymbol()
    {
        await using var client = Client();

        var rows = await ReferenceEntitlement.CollectAsync(
            client.SecurityMaster.GetLastAsync(
                new SecurityMasterGetLastParams
                {
                    Symbols = Queried,
                    AllocateIsins = ReferenceCredentials.AllocateIsins,
                },
                Cancel),
            Cancel);

        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.NotEmpty(row.SecurityId));
    }

    [Fact(SkipUnless = nameof(IsRequestAllowed), Skip = ReferenceCredentials.RequestSkipReason)]
    public async Task AdjustmentFactorsGetRange_AnswersWithRowsThisLibraryCanRead()
    {
        var rows = await AdjustmentFactors();

        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
        {
            Assert.NotEmpty(row.SecurityId);
            Assert.NotEmpty(row.EventId);
            Assert.True(row.Event.HasValue, "An adjustment factor arrived with no event code.");
        });
    }

    // ----------------------------------------------------------------------------------------
    // The answer #49 is waiting for.
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// The range's <c>end</c> excludes rows sitting exactly on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The experiment.</b> Read the configured window, take the <em>latest</em>
    /// <c>event_date</c> any row carries, then re-request the same window ending at midnight UTC on
    /// that date. If the end is exclusive, every row on that date disappears and the rest stay; if
    /// it is inclusive, they are all still there. The two outcomes differ by whole rows rather than
    /// by a nanosecond, so there is nothing to interpret.
    /// </para>
    /// <para>
    /// <b>Why <c>event_date</c> rather than <c>ts_record</c>.</b> The index names the column the
    /// server filters on, and <c>event_date</c> is a date — so the boundary lands on a midnight
    /// that a whole day's rows share, which makes the second request's answer unambiguous. A
    /// nanosecond-resolution index would put at most one row on the boundary and a window with none
    /// would prove nothing.
    /// </para>
    /// <para>
    /// <b>This is #45 run again on purpose.</b> That defect was an <c>end_date</c> upstream
    /// documented one way and the server implemented the other, agreed with by a mock for as long
    /// as both existed. Upstream documents this end as exclusive in three places —
    /// <c>corporate.rs:130</c>, <c>security.rs:103</c>, <c>adjustment.rs:63</c> — and #46's lesson
    /// is to probe the endpoint being changed rather than the one beside it, so this asks
    /// <c>corporate_actions.get_range</c> and claims nothing about the other two.
    /// </para>
    /// </remarks>
    [Fact(SkipUnless = nameof(IsRequestAllowed), Skip = ReferenceCredentials.RequestSkipReason)]
    public async Task CorporateActionsGetRange_ReadsTheRangeEndAsExclusive()
    {
        var window = await CorporateActions(CorporateActionsQuery());

        var dated = window.Where(r => r.EventDate is not null).ToList();
        Assert.True(
            dated.Count > 0,
            "No row in the configured window carries an event_date, so there is no boundary to "
            + $"put an end on. Widen {ReferenceCredentials.StartVariable}/"
            + $"{ReferenceCredentials.EndVariable} or point "
            + $"{ReferenceCredentials.SymbolVariable} at a busier symbol.");

        var boundary = dated.Max(r => r.EventDate!.Value);
        var onTheBoundary = dated.Count(r => r.EventDate == boundary);

        var narrowed = await CorporateActions(CorporateActionsQuery(
            ReferenceDateTimeRange.Between(
                ReferenceCredentials.Midnight(ReferenceCredentials.Start),
                ReferenceCredentials.Midnight(boundary))));

        var verdict = ReferenceProbe.CheckBoundary(
            windowCount: window.Count,
            onTheBoundary: onTheBoundary,
            narrowedCount: narrowed.Count,
            survivors: narrowed.Count(r => r.EventDate == boundary));

        _output.WriteLine(
            $"boundary {boundary:uuuu-MM-dd}: {verdict.WindowCount} row(s) in the window, "
            + $"{verdict.OnTheBoundary} on the boundary; narrowing the end onto it returned "
            + $"{verdict.NarrowedCount} (predicted {verdict.ExpectedNarrowedCount} if exclusive), "
            + $"of which {verdict.Survivors} were still dated that day. "
            + $"end reads {verdict.Reading}.");

        // One branch, not two assertions. An inclusive end also fails the row-count check, so
        // asserting that check separately would report #49's answer as a confounded run.
        switch (verdict.Reading)
        {
            case BoundaryReading.Exclusive:
                break;

            case BoundaryReading.Inclusive:
                Assert.Fail(
                    "end is INCLUSIVE, not exclusive. A range ending at midnight UTC on "
                    + $"{boundary:uuuu-MM-dd} still returned {verdict.Survivors} of the "
                    + $"{verdict.OnTheBoundary} row(s) dated that day. Upstream documents this end "
                    + "as exclusive in corporate.rs:130, security.rs:103 and adjustment.rs:63, and "
                    + "ReferenceDateTimeRange repeats the claim — all of which is now wrong, exactly "
                    + "as #45 was for get_dataset_condition. Fix the type's documentation before its "
                    + "behaviour, and probe the other two endpoints separately rather than assuming "
                    + "they match.");
                break;

            default:
                Assert.Fail(
                    $"This run answers #49 neither way. The narrowed query returned "
                    + $"{verdict.NarrowedCount} row(s) where removing the {verdict.OnTheBoundary} "
                    + $"boundary row(s) from {verdict.WindowCount} predicts "
                    + $"{verdict.ExpectedNarrowedCount}, so the two queries differ by more than "
                    + "their end. Re-run against a quieter window — a symbol and range whose row "
                    + "set is stable between two calls — before reading anything into it.");
                break;
        }
    }

    // ----------------------------------------------------------------------------------------
    // The answer #52 is waiting for.
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Rows arrive already ordered by the column <c>index</c> names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What is riding on this.</b> Upstream buffers the whole response into a <c>Vec</c> and
    /// sorts it by the index (<c>corporate.rs:59-63</c>). #52 ports the endpoint as a stream, which
    /// has no buffer to rearrange, so this library hands rows over in whatever order the server
    /// sent them. If the server already sorts, that difference is invisible and #52's decision
    /// costs nothing. If it does not, callers who need index order must sort for themselves, and
    /// that has to be documented on <c>GetRangeAsync</c> rather than discovered.
    /// </para>
    /// <para>
    /// <b>Both indexes with a usable column are asked</b>, because "the server sorts" and "the
    /// server happens to store in event_date order" are different claims and only the second one
    /// survives changing the index. <c>ts_record</c> is the control: it is a different column with
    /// a different natural order, so a response sorted under both is genuinely being sorted to
    /// order rather than returned in storage order.
    /// </para>
    /// <para>
    /// Rows with no value in the indexed column are dropped from the comparison rather than sorted
    /// against: <c>event_date</c> and <c>ex_date</c> are both nullable, and where a null sorts is a
    /// question about the server's collation and not about whether it sorted at all.
    /// </para>
    /// </remarks>
    [Theory(SkipUnless = nameof(IsRequestAllowed), Skip = ReferenceCredentials.RequestSkipReason)]
    [InlineData(CorporateActionIndex.EventDate)]
    [InlineData(CorporateActionIndex.TsRecord)]
    public async Task CorporateActionsGetRange_ArrivesInTheOrderTheIndexNames(CorporateActionIndex index)
    {
        var rows = await CorporateActions(CorporateActionsQuery(index: index));
        Assert.NotEmpty(rows);

        var verdict = index switch
        {
            CorporateActionIndex.EventDate => ReferenceProbe.CheckOrdering(
                rows.Where(r => r.EventDate is not null).Select(r => r.EventDate!.Value)),
            CorporateActionIndex.TsRecord => ReferenceProbe.CheckOrdering(
                rows.Select(r => r.TsRecord)),
            _ => throw new ArgumentOutOfRangeException(nameof(index), index, null),
        };

        _output.WriteLine(
            $"{index}: {verdict.ComparedCount} comparable row(s) of {rows.Count}, "
            + (verdict.IsOrdered ? "ascending throughout." : $"first descent at {verdict.Descent}."));

        Assert.True(
            verdict.IsOrdered,
            $"The server does NOT return corporate_actions.get_range sorted by {index}. "
            + $"{verdict.Descent}, over {verdict.ComparedCount} comparable row(s). #52 dropped "
            + "upstream's client-side sort on the argument that a stream cannot be sorted; that is "
            + "still true, but it is now an observable difference from upstream and "
            + "CorporateActionsClient.GetRangeAsync must say so.");
    }

    // ----------------------------------------------------------------------------------------
    // The answer #53 is waiting for.
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Every rate the server sends is a magnitude <see cref="decimal"/> holds without strain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What #53 decided and what it risked.</b> Upstream types every rate as <c>f64</c>; this
    /// library types them <see cref="decimal"/>, because a dividend of <c>0.24</c> is a decimal
    /// quantity and binary floating point cannot hold it exactly. The named risk was the other
    /// side of that trade: <c>f64</c> spans roughly 1e±308 and <see cref="decimal"/> stops at
    /// ±7.9e28, so a value outside that range throws here where upstream would have approximated
    /// it.
    /// </para>
    /// <para>
    /// <b>Reaching this assertion at all is most of the answer.</b> A rate outside
    /// <see cref="decimal"/>'s range would have thrown in the converter, before any assertion here
    /// ran — so a green run means every rate in the sample was representable. The band below is the
    /// part that adds something: it reports the observed magnitudes rather than merely surviving
    /// them, and it fails on a value that is technically representable but nowhere near what a rate
    /// should look like, which is the shape a units bug takes.
    /// </para>
    /// <para>
    /// <b>Both models are read, because the fields live in both.</b>
    /// <see cref="CorporateAction.RateInfo"/> is an open map whose values are rates —
    /// #55's "a map of rates is still rates" — and <see cref="AdjustmentFactor"/> carries
    /// <c>factor</c>, <c>close</c> and <c>gross_dividend</c> as fixed columns.
    /// </para>
    /// </remarks>
    [Fact(SkipUnless = nameof(IsRequestAllowed), Skip = ReferenceCredentials.RequestSkipReason)]
    public async Task TheRateFields_CarryMagnitudesDecimalHoldsComfortably()
    {
        var observed = new List<RateObservation>();

        foreach (var row in await CorporateActions(CorporateActionsQuery()))
        {
            foreach (var (name, rate) in row.RateInfo)
            {
                if (rate is { } value)
                {
                    observed.Add(new RateObservation
                    {
                        Field = $"corporate_actions rate_info['{name}']",
                        Value = value,
                    });
                }
            }
        }

        foreach (var factor in await AdjustmentFactors())
        {
            observed.Add(new RateObservation { Field = "adjustment_factors factor", Value = factor.Factor });
            observed.Add(new RateObservation { Field = "adjustment_factors sentiment", Value = factor.Sentiment });

            if (factor.Close is { } close)
            {
                observed.Add(new RateObservation { Field = "adjustment_factors close", Value = close });
            }

            if (factor.GrossDividend is { } dividend)
            {
                observed.Add(new RateObservation
                {
                    Field = "adjustment_factors gross_dividend",
                    Value = dividend,
                });
            }
        }

        Assert.True(
            observed.Count > 0,
            "Not one rate came back across both endpoints, so this test measured nothing. Point "
            + $"{ReferenceCredentials.SymbolVariable} at a symbol with a dividend or a split inside "
            + $"{ReferenceCredentials.StartVariable}..{ReferenceCredentials.EndVariable}.");

        // Wide enough that no real rate, price or ratio is near it, and narrow enough that a value
        // decimal merely *tolerates* still fails. decimal's own ceiling is ~7.9e28.
        var verdict = ReferenceProbe.CheckMagnitudes(
            observed,
            floor: 0.000_000_000_001m,
            ceiling: 1_000_000_000_000m);

        // The report is what #53 is owed. A green assertion says "nothing outrageous", which is not
        // a sentence anybody can write into the issue; the per-field spans are.
        _output.WriteLine(verdict.Render());

        Assert.True(
            verdict.IsWithinBand,
            $"{verdict.Extreme.Count} of {verdict.ObservedCount} rate(s) carry a magnitude outside "
            + $"[{verdict.Floor}, {verdict.Ceiling}]. decimal still held them, so nothing threw — "
            + "but a rate that size is a units question rather than a precision one:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, verdict.Extreme));
    }

    // ----------------------------------------------------------------------------------------
    // Credentials. Free even here: a refused request returns no rows.
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// <c>get_range</c> refuses a key the API does not know, which the documentation endpoints do
    /// not.
    /// </summary>
    /// <remarks>
    /// <b>This costs nothing and is here anyway.</b> A request rejected at authentication returns
    /// no rows and so cannot be billed, which would put it in <see cref="RealReferenceApiTests"/> on
    /// price alone. It lives here because that class's promise is "this file calls only the free
    /// endpoints", and a promise checkable by reading the file list is worth more than one free
    /// test's placement. The endpoint is what decides where a test goes, not the outcome of one
    /// call to it.
    /// </remarks>
    [Fact(SkipUnless = nameof(IsRequestAllowed), Skip = ReferenceCredentials.RequestSkipReason)]
    public async Task GetRange_WithAKeyTheApiDoesNotKnow_IsRefusedBeforeAnyRowMoves()
    {
        await using var client = new ReferenceClient { ApiKey = new ApiKey(NotARealKey) };

        var rejected = await Assert.ThrowsAsync<DatabentoApiException>(async () =>
        {
            await foreach (var _ in client.CorporateActions
                .GetRangeAsync(CorporateActionsQuery(), Cancel)
                .WithCancellation(Cancel))
            {
                Assert.Fail("A row arrived from a request the API should have refused.");
            }
        });

        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.DoesNotContain(NotARealKey, rejected.ToString(), StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------------

    private static async Task<List<CorporateAction>> CorporateActions(CorporateActionsGetRangeParams query)
    {
        await using var client = Client();
        return await ReferenceEntitlement.CollectAsync(
            client.CorporateActions.GetRangeAsync(query, Cancel), Cancel);
    }

    private static async Task<List<AdjustmentFactor>> AdjustmentFactors()
    {
        await using var client = Client();
        return await ReferenceEntitlement.CollectAsync(
            client.AdjustmentFactors.GetRangeAsync(
                new AdjustmentFactorsGetRangeParams
                {
                    Symbols = Queried,
                    DateTimeRange = ReferenceCredentials.Range,
                    AllocateIsins = ReferenceCredentials.AllocateIsins,
                },
                Cancel),
            Cancel);
    }
}
