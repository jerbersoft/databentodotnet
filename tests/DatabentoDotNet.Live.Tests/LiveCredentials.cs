namespace DatabentoDotNet.Live.Tests;

/// <summary>
/// The API key and dataset the opt-in live tests run against, read from the environment or from
/// a <c>.env</c> file at the repository root.
/// </summary>
/// <remarks>
/// <para>
/// <b>Absent credentials are a skip, never a failure.</b> Every test that touches the real
/// gateway is gated on <see cref="IsConfigured"/>, so a clone with no <c>.env</c> — a fresh
/// machine, a contributor, all three CI runners — runs the whole suite green and reports those
/// tests as skipped. CI additionally filters the category out by name, so the gate is belt and
/// braces rather than a single point of failure.
/// </para>
/// <para>
/// <b>A real environment variable wins over the file.</b> That is what lets a secret manager or a
/// CI runner supply the key without a <c>.env</c> existing at all, and it means the file is a
/// developer convenience rather than the mechanism.
/// </para>
/// <para>
/// <b>The key is never rendered.</b> Nothing here returns the raw string: <see cref="ApiKey"/>
/// hands back a validated <see cref="Live.ApiKey"/>, whose <c>ToString</c> is redacted to its
/// bucket id. A test that interpolated a raw key into an assertion message would print it into a
/// CI log, which is why <c>RealGatewaySmokeTests</c> asserts against that directly.
/// </para>
/// </remarks>
public static class LiveCredentials
{
    /// <summary>The environment variable holding the API key.</summary>
    public const string KeyVariable = "DATABENTO_API_KEY";

    /// <summary>The environment variable naming the dataset to authenticate against.</summary>
    public const string DatasetVariable = "DATABENTO_LIVE_DATASET";

    /// <summary>
    /// The dataset used when <see cref="DatasetVariable"/> is unset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Databento's own consolidated equities feed. Chosen because a <em>live</em> data license is
    /// a separate entitlement from historical access — an account with full historical access is
    /// still answered <c>success=0|error=A live data license is required to access …</c> on a
    /// venue feed it has not licensed for live — and this is the one a plain subscription tends to
    /// carry. Override it when the account holds licenses for something else.
    /// </para>
    /// <para>
    /// It carries <c>mbp-1</c>, <c>tbbo</c>, <c>trades</c>, <c>bbo-1s</c>, <c>bbo-1m</c>, the four
    /// <c>ohlcv</c> intervals and <c>definition</c> — <b>not <c>mbo</c></b>, which is a venue-feed
    /// schema (<c>XNAS.ITCH</c>, <c>GLBX.MDP3</c>, <c>DBEQ.BASIC</c>) this account holds no live
    /// license for. That is why M2 measures its zero-per-record-allocation target against
    /// <c>MockLiveGateway</c> replaying synthetic MBO rather than against a real subscription:
    /// allocation is a property of the code path, not of where the bytes came from. ROADMAP.md §4
    /// records the reasoning and the full license snapshot it turns on.
    /// </para>
    /// </remarks>
    public const string DefaultDataset = "EQUS.MINI";

    /// <summary>
    /// Whether an API key is available, and therefore whether the live tests run at all. Referenced
    /// by every live test's <c>SkipUnless</c>.
    /// </summary>
    /// <remarks>
    /// Resolved on each call rather than cached in a <see langword="static"/> field. A field
    /// initialiser here would run <em>before</em> the <see cref="DotEnv"/> it depends on — static
    /// field initialisers run in declaration order — and the first thing xUnit touches is this
    /// property, so the whole class failed to initialise. The parse itself still happens once,
    /// inside the <see cref="Lazy{T}"/>; only the dictionary lookup repeats.
    /// </remarks>
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Resolve(KeyVariable));

    /// <summary>The reason reported for a skipped live test.</summary>
    public const string SkipReason =
        "No " + KeyVariable + " in the environment or in .env — the live gateway tests are opt-in.";

    /// <summary>The dataset to authenticate against.</summary>
    public static string Dataset => Resolve(DatasetVariable) is { Length: > 0 } dataset
        ? dataset
        : DefaultDataset;

    /// <summary>The validated API key.</summary>
    /// <exception cref="InvalidOperationException">No key is configured.</exception>
    /// <exception cref="ArgumentException">The key is present but not a valid Databento key.</exception>
    public static ApiKey ApiKey => Resolve(KeyVariable) is { Length: > 0 } key
        ? new ApiKey(key)
        : throw new InvalidOperationException(SkipReason);

    /// <summary>
    /// The environment first, then <c>.env</c>. Returns <see langword="null"/> when neither has it.
    /// </summary>
    private static string? Resolve(string name)
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
    /// Rooted at <see cref="AppContext.BaseDirectory"/> rather than at the current directory, for
    /// the reason <c>TestFixtures</c> gives: the working directory is the project folder on a dev
    /// machine and something else entirely under a CI runner. Walking up is what finds a
    /// repository-root <c>.env</c> from <c>bin/Debug/net10.0</c>; finding nothing is a normal
    /// outcome, not an error.
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
