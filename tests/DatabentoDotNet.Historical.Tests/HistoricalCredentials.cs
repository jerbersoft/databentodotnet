using DatabentoDotNet.Dbn;
using NodaTime;
using NodaTime.Text;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// The API key and query defaults the opt-in historical tests run against, read from the
/// environment or from a <c>.env</c> file at the repository root.
/// </summary>
/// <remarks>
/// <para>
/// <b>Absent credentials are a skip, never a failure.</b> Every test that touches the real API is
/// gated on <see cref="IsConfigured"/>, so a clone with no <c>.env</c> — a fresh machine, a
/// contributor, all three CI runners — runs the whole suite green and reports those tests as
/// skipped. CI additionally filters <c>Category=Historical</c> out by name, so the gate is belt and
/// braces rather than a single point of failure.
/// </para>
/// <para>
/// <b>A real environment variable wins over the file</b>, which is what lets a secret manager or a
/// CI runner supply the key without a <c>.env</c> existing at all.
/// </para>
/// <para>
/// <b>Why this duplicates <c>LiveCredentials</c> rather than sharing a project with it.</b> #44
/// required this decision be written down, because two copies that drift on the <em>redaction</em>
/// rule would be a real hazard. They cannot drift on it: redaction is not implemented here at all.
/// It lives in <see cref="ApiKey"/>, in the shipping <c>DatabentoDotNet.Dbn</c> assembly both test
/// projects reference, whose <see cref="ApiKey.ToString"/> renders only the bucket id — and
/// <see cref="ApiKey"/> is what this class hands back, exactly as <c>LiveCredentials.ApiKey</c>
/// does. There is one implementation of the rule and neither copy contains it.
/// </para>
/// <para>
/// What does duplicate is <c>.env</c> parsing and the opt-in allow-list: mechanical, and divergence
/// in either is loud rather than silent — a test that fails to skip, or a gate that fails to gate.
/// Extracting sixty lines into a shared test project would add a fourth test assembly to the
/// solution and a cross-project reference between two harnesses that are otherwise deliberately
/// independent, buying less than the shared <see cref="ApiKey"/> already provides.
/// </para>
/// </remarks>
public static class HistoricalCredentials
{
    /// <summary>The environment variable holding the API key. Shared with the live tests.</summary>
    public const string KeyVariable = "DATABENTO_API_KEY";

    /// <summary>The environment variable naming the dataset the historical tests query.</summary>
    public const string DatasetVariable = "DATABENTO_HISTORICAL_DATASET";

    /// <summary>The environment variable naming the schema the billing enquiries price.</summary>
    public const string SchemaVariable = "DATABENTO_HISTORICAL_SCHEMA";

    /// <summary>The environment variable naming the symbol the billing enquiries query.</summary>
    public const string SymbolVariable = "DATABENTO_HISTORICAL_SYMBOL";

    /// <summary>The environment variable naming the first day of the queried range.</summary>
    public const string DateVariable = "DATABENTO_HISTORICAL_DATE";

    /// <summary>
    /// The environment variable that opts in to tests which cost money.
    /// </summary>
    /// <remarks>
    /// See <see cref="IsRequestAllowed"/>. Nothing in this project consumes it yet, and that is
    /// deliberate — see that property's remarks.
    /// </remarks>
    public const string RequestVariable = "DATABENTO_HISTORICAL_REQUEST";

    /// <summary>
    /// The dataset used when <see cref="DatasetVariable"/> is unset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CME Globex MDP 3.0, upstream's own example dataset throughout <c>metadata.rs</c>, and the
    /// one every endpoint in <see cref="RealHistoricalApiTests"/> was verified against by hand
    /// before those tests were written.
    /// </para>
    /// <para>
    /// <b>Not <c>LiveCredentials.DefaultDataset</c>, and the difference is the point.</b> A
    /// <em>live</em> data license and historical access are separate entitlements: the live default
    /// names a feed a plain subscription tends to carry for streaming, which says nothing about
    /// historical availability. Every endpoint reached from here is discovery or a billing
    /// enquiry, and those answer for any dataset in the catalog rather than only for entitled ones.
    /// </para>
    /// </remarks>
    public const string DefaultDataset = "GLBX.MDP3";

    /// <summary>
    /// The schema used when <see cref="SchemaVariable"/> is unset, in its DBN wire spelling.
    /// </summary>
    /// <remarks>
    /// Overridable because it has to move with <see cref="DefaultDataset"/>: a schema is offered
    /// per dataset, so a hard-coded one would make the dataset override useless. The same coupling
    /// <c>LiveCredentials.DefaultSchema</c> documents.
    /// </remarks>
    public const string DefaultSchema = "trades";

    /// <summary>
    /// The symbol used when <see cref="SymbolVariable"/> is unset.
    /// </summary>
    /// <remarks>
    /// The March 2024 E-mini S&amp;P 500 future. Expired, and chosen for that: a settled contract
    /// over a settled date has a record count that does not move, so a test written against it
    /// does not quietly change meaning with the calendar. It moves with
    /// <see cref="DefaultDataset"/> and <see cref="DefaultDate"/> for the same reason the schema
    /// does.
    /// </remarks>
    public const string DefaultSymbol = "ESH4";

    /// <summary>
    /// The first day of the queried range when <see cref="DateVariable"/> is unset: 2 January 2024,
    /// a settled trading day well inside <see cref="DefaultDataset"/>'s available history.
    /// </summary>
    /// <remarks>
    /// Point <see cref="DatasetVariable"/> at a dataset whose history starts later and this needs
    /// to move with it, which is why it is a variable rather than a literal in the test body.
    /// </remarks>
    public static LocalDate DefaultDate => new(2024, 1, 2);

    /// <summary>The reason reported for a skipped historical test.</summary>
    public const string SkipReason =
        "No " + KeyVariable + " in the environment or in .env — the real historical API tests are "
        + "opt-in.";

    /// <summary>
    /// Whether an API key is available, and therefore whether the historical tests run at all.
    /// Referenced by every <c>SkipUnless</c> in <see cref="RealHistoricalApiTests"/>.
    /// </summary>
    /// <remarks>
    /// Resolved on each call rather than cached in a <see langword="static"/> field, for the reason
    /// <c>LiveCredentials.IsConfigured</c> gives: a field initialiser here would run before the
    /// <see cref="DotEnv"/> it depends on, because static field initialisers run in declaration
    /// order and the first thing xUnit touches is this property.
    /// </remarks>
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Resolve(KeyVariable));

    /// <summary>
    /// Whether tests that <em>spend money</em> may run: a key is configured <b>and</b>
    /// <see cref="RequestVariable"/> is explicitly enabled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A second gate, because <see cref="IsConfigured"/> answers a different question.</b> A key
    /// in <c>.env</c> means "this developer can reach the API", which is all the metadata tests
    /// need — every one of them is discovery or a billing enquiry, and <c>get_cost</c> exists
    /// precisely to be called <em>before</em> committing to a request. Downloading data costs
    /// money, and "I have a key configured" is not consent to spend it on every
    /// <c>dotnet test</c>. The same rule <c>LiveCredentials.IsSessionAllowed</c> implements for
    /// M2: <em>no test spends without its own opt-in</em>.
    /// </para>
    /// <para>
    /// <b>Nothing consumes this yet, and that is the point of landing it here.</b> Its consumers
    /// are <c>timeseries.get_range</c> (#38) and <c>batch.submit_job</c> (#39). Shipping the gate
    /// with the harness means the first billable test arrives behind a gate that already exists
    /// and is already documented in <c>.env.example</c>, rather than one written in the same commit
    /// that first needs it — which is the commit least likely to get the gate right.
    /// </para>
    /// </remarks>
    public static bool IsRequestAllowed => IsConfigured && IsEnabled(Resolve(RequestVariable));

    /// <summary>The reason reported when a billable historical test is skipped.</summary>
    public const string RequestSkipReason =
        "Set " + RequestVariable + "=1 (alongside " + KeyVariable + ") to run the tests that "
        + "download data and therefore cost money.";

    /// <summary>The dataset to query.</summary>
    public static string Dataset => Resolve(DatasetVariable) is { Length: > 0 } dataset
        ? dataset
        : DefaultDataset;

    /// <summary>The schema to price, in its wire spelling.</summary>
    public static string Schema => Resolve(SchemaVariable) is { Length: > 0 } schema
        ? schema
        : DefaultSchema;

    /// <summary>The symbol to query.</summary>
    public static string Symbol => Resolve(SymbolVariable) is { Length: > 0 } symbol
        ? symbol
        : DefaultSymbol;

    /// <summary>The first day of the queried range.</summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="DateVariable"/> is set but is not an ISO <c>yyyy-MM-dd</c> date.
    /// </exception>
    public static LocalDate Date
    {
        get
        {
            if (Resolve(DateVariable) is not { Length: > 0 } text)
            {
                return DefaultDate;
            }

            var parsed = LocalDatePattern.Iso.Parse(text);
            return parsed.Success
                ? parsed.Value
                : throw new InvalidOperationException(
                    $"{DateVariable} must be an ISO yyyy-MM-dd date; '{text}' is not.");
        }
    }

    /// <summary>The validated API key.</summary>
    /// <exception cref="InvalidOperationException">No key is configured.</exception>
    /// <exception cref="ArgumentException">The key is present but not a valid Databento key.</exception>
    public static ApiKey ApiKey => Resolve(KeyVariable) is { Length: > 0 } key
        ? new ApiKey(key)
        : throw new InvalidOperationException(SkipReason);

    /// <summary>
    /// Whether an opt-in flag is set to something that means yes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately a small allow-list rather than "any non-empty value". The failure this
    /// prevents is a <c>.env</c> carrying <c>DATABENTO_HISTORICAL_REQUEST=0</c> — written by
    /// someone turning the gate <em>off</em> — being read as consent to spend money.
    /// </para>
    /// <para>
    /// <b>Public, and read by <c>ReferenceCredentials</c> in the M4 test project.</b> See
    /// <see cref="Resolve"/> for why that project reuses these two rather than copying them the
    /// way this class copied <c>LiveCredentials</c>.
    /// </para>
    /// </remarks>
    /// <param name="value">The raw variable value, or <see langword="null"/> when unset.</param>
    /// <returns><see langword="true"/> when it spells consent.</returns>
    public static bool IsEnabled(string? value) =>
        value is not null
        && (string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The environment first, then <c>.env</c>. Returns <see langword="null"/> when neither has it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Public because <c>ReferenceCredentials</c> calls it, and that is the one place this
    /// class's own "copy rather than share" argument does not reach.</b> The type's remarks explain
    /// why this file duplicates <c>LiveCredentials</c>' <c>.env</c> parsing instead of extracting
    /// it: extracting would have added a fourth test assembly and a project reference between two
    /// harnesses that are otherwise deliberately independent. Neither cost applies to the M4 test
    /// project — <c>DatabentoDotNet.Reference.Tests</c> already references this one, for
    /// <see cref="MockHistoricalGateway"/> — so a third copy would buy nothing and would put a
    /// third quoting rule in the repository for one <c>.env</c> file.
    /// </para>
    /// <para>
    /// Nothing about this resolution is historical-specific: it is "the environment, then the
    /// repository-root <c>.env</c>", and the variable it is asked for is the caller's business.
    /// </para>
    /// </remarks>
    /// <param name="name">The variable name.</param>
    /// <returns>Its value, or <see langword="null"/>.</returns>
    public static string? Resolve(string name)
    {
        if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } fromEnvironment)
        {
            return fromEnvironment;
        }

        return DotEnv.Value.GetValueOrDefault(name);
    }

    /// <summary>
    /// The parsed <c>.env</c>, read once. Empty when there is no such file, which is the normal
    /// case everywhere except a developer's machine.
    /// </summary>
    private static readonly Lazy<Dictionary<string, string>> DotEnv = new(Load);

    private static Dictionary<string, string> Load()
    {
        var file = FindUpwards(".env");
        if (file is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"').Trim('\'');
            values[name] = value;
        }

        return values;
    }

    /// <summary>
    /// Walks up from the test assembly's own directory looking for <paramref name="fileName"/>.
    /// </summary>
    /// <remarks>
    /// Rooted at <see cref="AppContext.BaseDirectory"/> rather than at the current directory: the
    /// working directory is the project folder on a dev machine and something else entirely under
    /// a CI runner. Walking up is what finds a repository-root <c>.env</c> from
    /// <c>bin/Debug/net10.0</c>; finding nothing is a normal outcome, not an error.
    /// </remarks>
    private static string? FindUpwards(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
