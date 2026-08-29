using DatabentoDotNet.Dbn;
using DatabentoDotNet.Historical.Tests;
using NodaTime;
using NodaTime.Text;

namespace DatabentoDotNet.Reference.Tests;

/// <summary>
/// The API key, the two gates and the query defaults the opt-in reference tests run against.
/// </summary>
/// <remarks>
/// <para>
/// <b>Absent credentials are a skip, never a failure</b>, and CI filters <c>Category=Reference</c>
/// out by name as well — the same two independent guards <c>LiveCredentials</c> and
/// <see cref="HistoricalCredentials"/> carry, neither of which is load-bearing alone.
/// </para>
/// <para>
/// <b>This one does not copy the <c>.env</c> parser, and the difference from
/// <see cref="HistoricalCredentials"/> is worth stating.</b> That class wrote down why it duplicated
/// <c>LiveCredentials</c>' sixty lines rather than extracting them: extracting would have added a
/// fourth test assembly to the solution and a project reference between two harnesses that are
/// otherwise deliberately independent. <b>Neither cost exists here.</b> This project already
/// references <c>DatabentoDotNet.Historical.Tests</c> — for <c>MockHistoricalGateway</c>, and that
/// reference is documented at length in the <c>.csproj</c> — so
/// <see cref="HistoricalCredentials.Resolve"/> and <see cref="HistoricalCredentials.IsEnabled"/>
/// are already in scope. A third copy would put a third <c>.env</c> quoting rule in the repository
/// for one file, and buy nothing at all.
/// </para>
/// <para>
/// What this class does hold is everything reference-specific: its own two variables, its own
/// defaults, and its own skip reasons. The gate names are what a reader greps for, so they are
/// spelled out here rather than assembled.
/// </para>
/// <para>
/// <b>The redaction rule is not implemented here either.</b> It lives in <see cref="ApiKey"/>, in
/// the shipping <c>DatabentoDotNet.Dbn</c> assembly, whose <see cref="ApiKey.ToString"/> renders
/// only the bucket id — and <see cref="ApiKey"/> is what <see cref="ReferenceCredentials.ApiKey"/>
/// hands back. There is one implementation of it and none of the three credential classes contains
/// a copy.
/// </para>
/// </remarks>
public static class ReferenceCredentials
{
    /// <summary>
    /// The environment variable holding the API key. The same one the live and historical tests
    /// read — Databento issues one key per account, and the reference API authenticates with it
    /// exactly as the historical API does.
    /// </summary>
    public const string KeyVariable = HistoricalCredentials.KeyVariable;

    /// <summary>
    /// The environment variable that opts in to reference tests which cost money.
    /// </summary>
    /// <remarks>
    /// The M4 counterpart of <c>DATABENTO_LIVE_SESSION</c> and
    /// <see cref="HistoricalCredentials.RequestVariable"/>. See <see cref="IsRequestAllowed"/>.
    /// </remarks>
    public const string RequestVariable = "DATABENTO_REFERENCE_REQUEST";

    /// <summary>The environment variable naming the symbol the billable tests query.</summary>
    public const string SymbolVariable = "DATABENTO_REFERENCE_SYMBOL";

    /// <summary>The environment variable naming the first day of the queried range.</summary>
    public const string StartVariable = "DATABENTO_REFERENCE_START";

    /// <summary>The environment variable naming the day the queried range ends before.</summary>
    public const string EndVariable = "DATABENTO_REFERENCE_END";

    /// <summary>
    /// The symbol used when <see cref="SymbolVariable"/> is unset.
    /// </summary>
    /// <remarks>
    /// A US listing with a dense and entirely settled corporate-actions history: a quarterly
    /// dividend inside any month-long window in <see cref="DefaultStart"/>'s year, and a stock
    /// split far enough back that <c>adjustment_factors</c> has something to say. Reference data is
    /// keyed by security rather than by dataset, so there is no dataset variable to move with it —
    /// which is the one structural difference from <see cref="HistoricalCredentials"/>' block of
    /// four coupled defaults.
    /// </remarks>
    public const string DefaultSymbol = "AAPL";

    /// <summary>
    /// The inclusive first day of the queried range when <see cref="StartVariable"/> is unset:
    /// 1 February 2024.
    /// </summary>
    /// <remarks>
    /// Settled, and chosen so the window below contains a declared dividend — an event with
    /// populated <c>rate_info</c>, which is what the <c>#53</c> magnitude question needs to read.
    /// </remarks>
    public static LocalDate DefaultStart => new(2024, 2, 1);

    /// <summary>
    /// The day the queried range ends before when <see cref="EndVariable"/> is unset:
    /// 1 March 2024.
    /// </summary>
    /// <remarks>
    /// A month, deliberately small. These endpoints bill by what they return, and every question
    /// this suite asks of them — ordering, magnitudes, whether the end is exclusive — is answerable
    /// from a handful of rows. Widening it costs money and answers nothing new.
    /// </remarks>
    public static LocalDate DefaultEnd => new(2024, 3, 1);

    /// <summary>
    /// What every billable test sends for <c>allocate_isins</c>, and it is
    /// <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The library's default is <see langword="true"/>, and these tests deliberately do not take
    /// it.</b> Databento allocates an ISIN to a security on request, and an account on an
    /// ISIN-limited plan has a finite number of those allocations; a request with
    /// <c>allocate_isins=true</c> for a symbol the account has not seen before can consume one.
    /// That is a side effect on the account which outlives the test run and which no amount of
    /// re-running undoes — a different and worse thing than the row charge, which is what the gate
    /// is nominally about.
    /// </para>
    /// <para>
    /// Nothing in this suite reads an ISIN, so nothing here needs one. #54 raised the hazard and
    /// #57's porting notes require any real call to state its choice; this is that statement, made
    /// once, in the place every billable test reads it from.
    /// </para>
    /// </remarks>
    public const bool AllocateIsins = false;

    /// <summary>The reason reported for a skipped reference test.</summary>
    public const string SkipReason =
        "No " + KeyVariable + " in the environment or in .env — the real reference API tests are "
        + "opt-in.";

    /// <summary>The reason reported when a billable reference test is skipped.</summary>
    public const string RequestSkipReason =
        "Set " + RequestVariable + "=1 (alongside " + KeyVariable + ") to run the reference tests "
        + "that request data and therefore cost money.";

    /// <summary>
    /// Whether an API key is available, and therefore whether the free reference tests run at all.
    /// </summary>
    /// <remarks>
    /// Resolved on each call rather than cached, for the reason
    /// <see cref="HistoricalCredentials.IsConfigured"/> gives.
    /// </remarks>
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(HistoricalCredentials.Resolve(KeyVariable));

    /// <summary>
    /// Whether tests that <em>request reference data</em> may run: a key is configured <b>and</b>
    /// <see cref="RequestVariable"/> is explicitly enabled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A second gate, because <see cref="IsConfigured"/> answers a different question.</b> A key
    /// in <c>.env</c> means "this developer can reach the API", which is all
    /// <see cref="RealReferenceApiTests"/> needs: both endpoints it calls are documentation, and
    /// documentation is free. The three <c>get_range</c> endpoints and <c>get_last</c> return
    /// billed rows, and "I have a key configured" is not consent to spend on every
    /// <c>dotnet test</c>. CLAUDE.md states the rule: <em>no test spends without its own
    /// opt-in</em>.
    /// </para>
    /// <para>
    /// <b>And a third guard that is not a variable: which class a test is in.</b>
    /// <see cref="RealReferenceRequestTests"/> holds every billable call and nothing else, so the
    /// free class's promise is checkable by reading the file list rather than by auditing
    /// attributes — the split <c>RealBatchSubmitTests</c> keeps from <c>RealBatchApiTests</c>.
    /// </para>
    /// </remarks>
    public static bool IsRequestAllowed =>
        IsConfigured && HistoricalCredentials.IsEnabled(HistoricalCredentials.Resolve(RequestVariable));

    /// <summary>The symbol the billable tests query.</summary>
    public static string Symbol =>
        HistoricalCredentials.Resolve(SymbolVariable) is { Length: > 0 } symbol ? symbol : DefaultSymbol;

    /// <summary>The inclusive first day of the queried range.</summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="StartVariable"/> is set but is not an ISO <c>yyyy-MM-dd</c> date.
    /// </exception>
    public static LocalDate Start => Date(StartVariable, DefaultStart);

    /// <summary>The day the queried range ends before.</summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="EndVariable"/> is set but is not an ISO <c>yyyy-MM-dd</c> date.
    /// </exception>
    public static LocalDate End => Date(EndVariable, DefaultEnd);

    /// <summary>
    /// The queried range as the reference endpoints take it: midnight UTC on <see cref="Start"/>,
    /// up to but not including midnight UTC on <see cref="End"/>.
    /// </summary>
    /// <remarks>
    /// "Up to but not including" is upstream's documented claim about the end and is exactly what
    /// <see cref="RealReferenceRequestTests"/> probes. This property spells the range the way the
    /// library models it; whether the server agrees is the finding, not the premise.
    /// </remarks>
    public static ReferenceDateTimeRange Range =>
        ReferenceDateTimeRange.Between(Midnight(Start), Midnight(End));

    /// <summary>Midnight UTC on a date, as an instant.</summary>
    /// <param name="date">The date.</param>
    /// <returns>The instant.</returns>
    public static Instant Midnight(LocalDate date) => date.AtMidnight().InUtc().ToInstant();

    /// <summary>The validated API key.</summary>
    /// <exception cref="InvalidOperationException">No key is configured.</exception>
    /// <exception cref="ArgumentException">The key is present but not a valid Databento key.</exception>
    public static ApiKey ApiKey => HistoricalCredentials.Resolve(KeyVariable) is { Length: > 0 } key
        ? new ApiKey(key)
        : throw new InvalidOperationException(SkipReason);

    private static LocalDate Date(string variable, LocalDate fallback)
    {
        if (HistoricalCredentials.Resolve(variable) is not { Length: > 0 } text)
        {
            return fallback;
        }

        var parsed = LocalDatePattern.Iso.Parse(text);
        return parsed.Success
            ? parsed.Value
            : throw new InvalidOperationException(
                $"{variable} must be an ISO yyyy-MM-dd date; '{text}' is not.");
    }
}
