using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using NodaTime;
using NodaTime.Text;

namespace DatabentoDotNet.Extensions.Hosting;

/// <summary>
/// Turns a bound <see cref="HistoricalOptions"/> into a <see cref="ResolvedHistorical"/>,
/// collecting every failure rather than stopping at the first.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mirrors <see cref="LiveSessionResolver"/> exactly:</b> the same result shape — a resolved
/// value or a list of failures, never an exception for an expected misconfiguration — and the
/// same rule against re-implementing a check the library already makes. <c>ApiKey</c> goes
/// through <c>new ApiKey(text)</c> for exactly that reason; nothing here decides for itself what a
/// valid key looks like.
/// </para>
/// <para>
/// <b>This is the only crossing, and both callers use it.</b> <see cref="HistoricalValidator"/>
/// calls it at startup and <c>DatabentoServiceCollectionExtensions.AddDatabentoHistorical</c>
/// calls it again when it actually builds the <c>HistoricalClient</c> — so a configuration that
/// validates is a configuration this package can build a client from, by construction rather than
/// by two lists kept in step.
/// </para>
/// <para>
/// <b>Every failure names its configuration path</b>, for the same reason
/// <see cref="LiveSessionResolver"/>'s do: the reader is looking at an <c>appsettings.json</c>,
/// not at this assembly.
/// </para>
/// <para>
/// <b><see langword="internal"/>, unlike <see cref="LiveSessionResolver"/>, and the asymmetry is
/// the point.</b> The live resolver is public because <c>LiveSessionResolverTests</c> drives it
/// directly and this repository declares no <c>InternalsVisibleTo</c>. Nothing outside this
/// assembly names this one, in tests, samples or the AOT probe: the container reaches it only
/// through <c>AddDatabentoHistorical</c>'s own factory. A type on the public surface is a type
/// promised under SemVer at 1.0, and <c>PublicAPI.Shipped.txt</c> being empty is exactly the window
/// in which that promise is still cheap to decline.
/// </para>
/// </remarks>
internal static class HistoricalResolver
{
    /// <summary>
    /// The configuration path historical (and reference) options bind from:
    /// <c>Databento:Historical</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A literal path, not derived from whatever section <c>AddDatabento(sectionPath)</c> was
    /// given — the same corner <see cref="LiveSessionResolver.PathFor"/> cuts, for the same
    /// reason: nothing here carries the configured root this far, and a failure message pointing
    /// at the conventional path is a large improvement over one pointing at nothing.
    /// </para>
    /// <para>
    /// <b>That is also why it did not survive the move to <see langword="internal"/> as a public
    /// constant on <see cref="HistoricalOptions"/>.</b> Its value is right only for a host that
    /// took the conventional section, and a consumer who called
    /// <c>AddDatabento("MyApp:Databento")</c> would read a path this package no longer binds from.
    /// A constant that is conditionally true is worse than none:
    /// <see cref="DatabentoOptions.DefaultSectionName"/> is public and says what it means.
    /// </para>
    /// </remarks>
    internal const string Path = DatabentoOptions.DefaultSectionName + ":Historical";

    /// <summary>The pooled-connection lifetime used when none is configured: five minutes.</summary>
    private const string DefaultPooledConnectionLifetime = "PT5M";

    /// <summary>Resolves the historical client's configuration, or reports why it cannot be resolved.</summary>
    /// <param name="options">The bound options.</param>
    /// <param name="root">The root options, consulted for a key <paramref name="options"/> does not carry.</param>
    /// <param name="environmentApiKey">
    /// The value of <see cref="LiveSessionResolver.ApiKeyEnvironmentVariable"/>, or
    /// <see langword="null"/>. A parameter rather than an ambient read, for the same reason
    /// <see cref="LiveSessionResolver.Resolve"/> takes one: the precedence chain is something a
    /// test can state and this method mutates nothing.
    /// </param>
    public static HistoricalResolutionResult Resolve(
        HistoricalOptions options,
        DatabentoOptions root,
        string? environmentApiKey)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(root);

        var failures = ImmutableArray.CreateBuilder<string>();

        var apiKey = ResolveApiKey(options, root, environmentApiKey, failures);
        var baseUrl = ResolveBaseUrl(options.BaseUrl, failures);
        var pooledConnectionLifetime = ResolvePooledConnectionLifetime(options.PooledConnectionLifetime, failures);

        if (failures.Count > 0)
        {
            return HistoricalResolutionResult.Failed(failures.ToImmutable());
        }

        return HistoricalResolutionResult.Success(new ResolvedHistorical
        {
            ApiKey = apiKey!,
            BaseUrl = baseUrl,
            UserAgentExtension = options.UserAgentExtension,
            PooledConnectionLifetime = pooledConnectionLifetime!.Value,
        });
    }

    /// <summary>
    /// Resolves the API key: the historical section's own, then the root's, then the environment
    /// variable — then <see cref="ApiKey(string)"/> itself, which is never re-implemented here.
    /// </summary>
    private static ApiKey? ResolveApiKey(
        HistoricalOptions options,
        DatabentoOptions root,
        string? environmentApiKey,
        ImmutableArray<string>.Builder failures)
    {
        var text = !string.IsNullOrWhiteSpace(options.ApiKey) ? options.ApiKey
            : !string.IsNullOrWhiteSpace(root.ApiKey) ? root.ApiKey
            : !string.IsNullOrWhiteSpace(environmentApiKey) ? environmentApiKey
            : null;

        if (text is null)
        {
            // Names all three places it looked, in the order it looked at them — the same message
            // shape LiveSessionResolver uses, so a reader who has already met one of these does
            // not have to learn a second phrasing for the other.
            failures.Add(
                $"{Path}:ApiKey — no API key found. Checked {Path}:ApiKey, "
                + $"{DatabentoOptions.DefaultSectionName}:ApiKey, and the "
                + $"{LiveSessionResolver.ApiKeyEnvironmentVariable} environment variable.");
            return null;
        }

        try
        {
            return new ApiKey(text);
        }
        catch (ArgumentException ex)
        {
            // ApiKey's own message, never the key itself: the message names a length or a
            // placeholder, and this resolver adds nothing that could leak the value.
            failures.Add($"{Path}:ApiKey — {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Parses an absolute URL, or reports the failure — the same predicate
    /// <see cref="DatabentoDotNet.Historical.HistoricalClient.BaseUrl"/>'s own setter checks
    /// (<see cref="Uri.IsAbsoluteUri"/>), stated here before a client exists to enforce it on.
    /// </summary>
    private static Uri? ResolveBaseUrl(string? text, ImmutableArray<string>.Builder failures)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return uri;
        }

        failures.Add(
            $"{Path}:BaseUrl — '{text}' is not an absolute URL. Scheme and host are both "
            + "required, for example 'https://hist.databento.com/'.");
        return null;
    }

    /// <summary>
    /// Parses an ISO-8601 duration with <see cref="PeriodPattern.NormalizingIso"/>, defaulting to
    /// <see cref="DefaultPooledConnectionLifetime"/> when nothing is configured. The same two
    /// guards <see cref="LiveSessionResolver"/>'s duration parsing uses, plus one more: zero is a
    /// parseable, non-negative duration, but a connection recycled every zero seconds pools
    /// nothing — so this one must be strictly positive, not merely non-negative.
    /// </summary>
    private static Duration? ResolvePooledConnectionLifetime(string? text, ImmutableArray<string>.Builder failures)
    {
        var value = string.IsNullOrWhiteSpace(text) ? DefaultPooledConnectionLifetime : text;

        var parsed = PeriodPattern.NormalizingIso.Parse(value);
        if (!parsed.Success)
        {
            failures.Add(
                $"{Path}:PooledConnectionLifetime — '{value}' is not an ISO-8601 duration, for "
                + "example 'PT5M'.");
            return null;
        }

        var period = parsed.Value;
        if (period.Months != 0 || period.Years != 0)
        {
            // Caught before ToDuration() ever runs: NodaTime throws InvalidOperationException for
            // exactly this, and that message names neither this value nor its configuration path.
            failures.Add(
                $"{Path}:PooledConnectionLifetime — '{value}' has a non-zero month or year "
                + "component. A month is not a fixed length, so it cannot become a Duration; "
                + "express this as weeks, days, hours, minutes, or seconds instead.");
            return null;
        }

        var duration = period.ToDuration();
        if (duration <= Duration.Zero)
        {
            failures.Add(
                $"{Path}:PooledConnectionLifetime — '{value}' must be positive; a connection "
                + "pool recycled every zero seconds pools nothing.");
            return null;
        }

        return duration;
    }
}

/// <summary>
/// The outcome of resolving the historical client's configuration: the result, or every reason it
/// could not be.
/// </summary>
/// <remarks><see langword="internal"/> with <see cref="HistoricalResolver"/>, which is its only producer.</remarks>
internal sealed class HistoricalResolutionResult
{
    private HistoricalResolutionResult(ResolvedHistorical? historical, ImmutableArray<string> failures)
    {
        Historical = historical;
        Failures = failures;
    }

    /// <summary>The resolved configuration, or <see langword="null"/> when resolution failed.</summary>
    public ResolvedHistorical? Historical { get; }

    /// <summary>Every failure, each naming its configuration path. Empty on success.</summary>
    public ImmutableArray<string> Failures { get; }

    /// <summary>Whether resolution succeeded.</summary>
    [MemberNotNullWhen(true, nameof(Historical))]
    public bool Succeeded => Historical is not null;

    // Named Success rather than Succeeded, for the same reason LiveSessionResolutionResult's
    // factory is: C# does not allow a property and a method to share a name, and this factory is
    // internal, so renaming it never touches the public surface.
    internal static HistoricalResolutionResult Success(ResolvedHistorical historical) => new(historical, []);

    internal static HistoricalResolutionResult Failed(ImmutableArray<string> failures) => new(null, failures);
}

/// <summary>
/// The historical (and reference) client's configuration with every value converted to the type
/// the library actually takes. Produced only by <see cref="HistoricalResolver"/>.
/// </summary>
/// <remarks>
/// <see langword="internal"/> with <see cref="HistoricalResolver"/>. Unlike
/// <see cref="ResolvedLiveSession"/>, which a test constructs to drive <c>LiveSessionRunner</c>
/// without a container, nothing outside this assembly builds or reads one of these.
/// </remarks>
internal sealed record ResolvedHistorical
{
    /// <summary>The validated API key.</summary>
    public required ApiKey ApiKey { get; init; }

    /// <summary>The base URL to send requests to, or <see langword="null"/> for the gateway's own.</summary>
    public Uri? BaseUrl { get; init; }

    /// <summary>Text identifying the application, appended to this library's <c>User-Agent</c>.</summary>
    public string? UserAgentExtension { get; init; }

    /// <summary>How long a pooled connection may be reused before it is replaced.</summary>
    public required Duration PooledConnectionLifetime { get; init; }
}
