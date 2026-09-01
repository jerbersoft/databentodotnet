using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Live;
using NodaTime;
using NodaTime.Text;

namespace DatabentoDotNet.Extensions.Hosting;

/// <summary>
/// Turns a bound <see cref="LiveSessionOptions"/> into a <see cref="ResolvedLiveSession"/>,
/// collecting every failure rather than stopping at the first.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only crossing, and both callers use it.</b>
/// <c>LiveSessionValidator</c> calls it at startup and the registration calls it when it
/// builds a runner, so a configuration that validates is a configuration that resolves — because
/// no second path exists to disagree. That is the rule <c>DbnTime</c> already enforces for the
/// <c>UndefTimestamp</c> sentinel, applied to a different boundary for the same reason.
/// </para>
/// <para>
/// <b>It never re-implements a check the library already makes.</b> A key goes through
/// <c>new ApiKey(text)</c> and a symbol list through <c>Symbols.From</c>; when either throws, the
/// message is kept and the configuration path is prefixed to it. A resolver that decided for
/// itself what a valid key looks like would be a second copy of that rule, and the copy that
/// silently disagrees is the one nobody is looking at.
/// </para>
/// <para>
/// <b>Every failure names its configuration path</b>, because the person reading the message is
/// looking at an <c>appsettings.json</c>, not at this assembly:
/// <c>Databento:Live:equities:Subscriptions:0:Schema — 'mbp1' is not a Databento schema.</c>
/// </para>
/// <para>
/// <b>What resolution checks, and what it does not.</b> Everything this method can decide on its
/// own is decided here, so that its failure names a path: the API key, the dataset, each
/// subscription's schema, symbology and symbol set, every duration and instant, the reconnect
/// pair — including the cross-field rule that <c>InitialDelay</c> cannot exceed <c>MaxDelay</c> —
/// and the gateway endpoint, port range included. <b>Three constraints the guide documents are
/// <em>not</em> checked here</b>, because the library already checks them and the paragraph above
/// is why this does not check them a second time:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>HeartbeatInterval</c>'s 5–1800 second range and <c>ReadTimeout</c>'s positivity, both
/// enforced by <c>LiveClient</c>'s own <c>init</c> accessors.
/// </description></item>
/// <item><description>
/// <c>UseSnapshot</c>'s two rules — the <c>mbo</c> schema only, and never together with
/// <c>Start</c> — enforced by <c>Subscription.Validate</c>, which is <see langword="internal"/> to
/// <c>DatabentoDotNet.Live</c> and so cannot be delegated to from here even if that were wanted.
/// </description></item>
/// </list>
/// <para>
/// <b>So "validated at startup" means every value parsed and converted, not every documented
/// constraint satisfied.</b> Those three surface from
/// <see cref="LiveSessionRunner.StartSessionAsync"/> instead — as an
/// <see cref="ArgumentOutOfRangeException"/> or an <see cref="ArgumentException"/> naming the
/// property rather than the configuration path. That is still a loud, immediate failure and not a
/// background one: <c>LiveSessionService.StartAsync</c> awaits <c>StartSessionAsync</c> before
/// <c>base.StartAsync</c>, so the host's boot fails either way. What is lost is only the path, and
/// the alternative — a second copy of three rules that already exist, free to drift from them — is
/// the trade this resolver refuses everywhere else too.
/// </para>
/// </remarks>
public static class LiveSessionResolver
{
    /// <summary>The environment variable consulted when no configuration supplies a key.</summary>
    public const string ApiKeyEnvironmentVariable = "DATABENTO_API_KEY";

    /// <summary>The configuration path a named session binds from: <c>Databento:Live:{name}</c>.</summary>
    public static string PathFor(string name) =>
        $"{DatabentoOptions.DefaultSectionName}:Live:{name}";

    /// <summary>Resolves one session, or reports why it cannot be resolved.</summary>
    /// <param name="name">The session's registration name.</param>
    /// <param name="options">The bound options.</param>
    /// <param name="root">The root options, consulted for a key the session does not carry.</param>
    /// <param name="environmentApiKey">
    /// The value of <see cref="ApiKeyEnvironmentVariable"/>, or <see langword="null"/>. A
    /// parameter rather than an ambient read, so that the precedence chain is something a test
    /// can state and this method mutates nothing.
    /// </param>
    public static LiveSessionResolutionResult Resolve(
        string name,
        LiveSessionOptions options,
        DatabentoOptions root,
        string? environmentApiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(root);

        var path = PathFor(name);
        var failures = ImmutableArray.CreateBuilder<string>();

        var apiKey = ResolveApiKey(path, options, root, environmentApiKey, failures);
        var dataset = Required(options.Dataset, $"{path}:Dataset", "the dataset to stream, for example 'EQUS.MINI'", failures);
        var subscriptions = ResolveSubscriptions(path, options, failures);
        var reconnect = ResolveReconnect(path, options.Reconnect, failures);
        var compression = ResolveCompression(path, options.Compression, failures);
        var slowReader = ResolveSlowReader(path, options.SlowReaderBehavior, failures);
        var heartbeat = ResolveOptionalDuration($"{path}:HeartbeatInterval", options.HeartbeatInterval, failures);
        var readTimeout = ResolveOptionalDuration($"{path}:ReadTimeout", options.ReadTimeout, failures);
        var gateway = ResolveGateway(path, options.Gateway, failures);

        if (failures.Count > 0)
        {
            return LiveSessionResolutionResult.Failed(failures.ToImmutable());
        }

        return LiveSessionResolutionResult.Success(new ResolvedLiveSession
        {
            Name = name,
            ApiKey = apiKey!,
            Dataset = dataset!,
            Subscriptions = subscriptions,
            Reconnect = reconnect,
            SendTsOut = options.SendTsOut,
            Compression = compression,
            SlowReaderBehavior = slowReader,
            HeartbeatInterval = heartbeat,
            ReadTimeout = readTimeout,
            Gateway = gateway,
        });
    }

    /// <summary>
    /// Resolves the API key: the session's own, then the root's, then the environment variable —
    /// then <see cref="ApiKey(string)"/> itself, which is never re-implemented here.
    /// </summary>
    private static ApiKey? ResolveApiKey(
        string path,
        LiveSessionOptions options,
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
            // Names all three places it looked, in the order it looked at them, so the reader
            // does not have to guess which of three files or environments to check.
            failures.Add(
                $"{path}:ApiKey — no API key found. Checked {path}:ApiKey, "
                + $"{DatabentoOptions.DefaultSectionName}:ApiKey, and the "
                + $"{ApiKeyEnvironmentVariable} environment variable.");
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
            failures.Add($"{path}:ApiKey — {ex.Message}");
            return null;
        }
    }

    /// <summary>A required string field: missing or whitespace becomes one failure at <paramref name="path"/>.</summary>
    private static string? Required(string? value, string path, string what, ImmutableArray<string>.Builder failures)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        failures.Add($"{path} — missing. Set it to {what}.");
        return null;
    }

    /// <summary>Resolves every subscription, reporting a failure per bad field rather than stopping at the first.</summary>
    private static ImmutableArray<Subscription> ResolveSubscriptions(
        string path,
        LiveSessionOptions options,
        ImmutableArray<string>.Builder failures)
    {
        var subscriptionsPath = $"{path}:Subscriptions";

        if (options.Subscriptions.Count == 0)
        {
            failures.Add(
                $"{subscriptionsPath} — at least one subscription is required; a session with "
                + "nothing to stream has nothing to do.");
            return [];
        }

        var subscriptions = ImmutableArray.CreateBuilder<Subscription>(options.Subscriptions.Count);
        for (var i = 0; i < options.Subscriptions.Count; i++)
        {
            var subscriptionOptions = options.Subscriptions[i];
            var itemPath = $"{subscriptionsPath}:{i}";

            var schema = ResolveSchema(itemPath, subscriptionOptions.Schema, failures);
            var stypeIn = ResolveStypeIn(itemPath, subscriptionOptions.StypeIn, failures);
            var symbols = ResolveSymbols(itemPath, subscriptionOptions.Symbols, failures);
            var start = ResolveOptionalInstant($"{itemPath}:Start", subscriptionOptions.Start, failures);

            if (schema is not null && stypeIn is not null && symbols is not null)
            {
                subscriptions.Add(new Subscription
                {
                    Symbols = symbols.Value,
                    Schema = schema.Value,
                    StypeIn = stypeIn.Value,
                    Start = start,
                    UseSnapshot = subscriptionOptions.UseSnapshot,
                    // Left unset so LiveClient.SubscribeAsync assigns the next id, exactly as it
                    // does for a subscription built directly rather than through configuration.
                    Id = null,
                });
            }
        }

        return subscriptions.ToImmutable();
    }

    private static Schema? ResolveSchema(string itemPath, string? text, ImmutableArray<string>.Builder failures)
    {
        var path = $"{itemPath}:Schema";

        if (string.IsNullOrWhiteSpace(text))
        {
            failures.Add($"{path} — missing. Set it to a Databento schema, for example 'trades'.");
            return null;
        }

        if (WireStrings.TryParseSchema(text, out var schema))
        {
            return schema;
        }

        failures.Add($"{path} — '{text}' is not a Databento schema.");
        return null;
    }

    private static SType? ResolveStypeIn(string itemPath, string? text, ImmutableArray<string>.Builder failures)
    {
        // LiveClient's own default (Subscription.StypeIn), restated here rather than left to
        // chance: the wire default and the configuration default must agree.
        //
        // IsNullOrWhiteSpace rather than `is null`, and that is not tidying. The environment
        // variable provider yields "" for a key set to nothing, so `is null` turned
        // Databento__Live__equities__Subscriptions__0__StypeIn= — an empty override, which is how
        // an operator spells "leave it alone" — into a startup failure instead of the default.
        // Every other optional field in this file already reads it this way.
        if (string.IsNullOrWhiteSpace(text))
        {
            return SType.RawSymbol;
        }

        var path = $"{itemPath}:StypeIn";
        if (WireStrings.TryParseSType(text, out var stype))
        {
            return stype;
        }

        failures.Add($"{path} — '{text}' is not a Databento symbology.");
        return null;
    }

    private static Symbols? ResolveSymbols(string itemPath, IList<string> symbols, ImmutableArray<string>.Builder failures)
    {
        if (symbols is [var single] && single == Symbols.AllWireValue)
        {
            return Symbols.All;
        }

        try
        {
            return Symbols.From(symbols);
        }
        catch (ArgumentException ex)
        {
            // Symbols.From's own message, with the path prefixed: this resolver does not decide
            // for itself what a valid symbol set is.
            failures.Add($"{itemPath}:Symbols — {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Resolves the reconnection policy: two durations, the attempt bound, and the one rule
    /// neither duration can state alone — <c>InitialDelay</c> must not exceed <c>MaxDelay</c>.
    /// </summary>
    private static ResolvedReconnect ResolveReconnect(string path, ReconnectOptions options, ImmutableArray<string>.Builder failures)
    {
        var reconnectPath = $"{path}:Reconnect";
        var initialDelay = ResolveDuration($"{reconnectPath}:InitialDelay", options.InitialDelay, failures);
        var maxDelay = ResolveDuration($"{reconnectPath}:MaxDelay", options.MaxDelay, failures);

        if (options.MaxAttempts < 1)
        {
            failures.Add(
                $"{reconnectPath}:MaxAttempts — must be at least 1 to ever start a session; "
                + $"{options.MaxAttempts.ToString(CultureInfo.InvariantCulture)} was given.");
        }

        // A cross-field check, not something ResolveDuration alone could catch: each duration
        // parses cleanly on its own, and it is the pair together that is meaningless. Reported at
        // InitialDelay, since that is the value that has to move for the pair to make sense.
        if (initialDelay is not null && maxDelay is not null && initialDelay > maxDelay)
        {
            failures.Add(
                $"{reconnectPath}:InitialDelay — '{options.InitialDelay}' is greater than "
                + $"{reconnectPath}:MaxDelay's '{options.MaxDelay}'. An initial backoff cannot "
                + "start above the ceiling it backs off toward.");
        }

        return new ResolvedReconnect
        {
            Enabled = options.Enabled,
            InitialDelay = initialDelay ?? Duration.Zero,
            MaxDelay = maxDelay ?? Duration.Zero,
            MaxAttempts = options.MaxAttempts,
        };
    }

    /// <summary>
    /// Parses an ISO-8601 duration with <see cref="PeriodPattern.NormalizingIso"/>, not
    /// <c>DurationPattern.Roundtrip</c> — the latter parses NodaTime's own
    /// <c>days:hh:mm:ss</c> form, not <c>"PT30S"</c>. Two guards beyond parse success: a period
    /// with a non-zero month or year component has no fixed length and cannot become a
    /// <see cref="Duration"/> at all, and a negative duration is not a meaningful delay.
    /// </summary>
    private static Duration? ResolveDuration(string path, string text, ImmutableArray<string>.Builder failures)
    {
        var parsed = PeriodPattern.NormalizingIso.Parse(text);
        if (!parsed.Success)
        {
            failures.Add($"{path} — '{text}' is not an ISO-8601 duration, for example 'PT30S'.");
            return null;
        }

        var period = parsed.Value;
        if (period.Months != 0 || period.Years != 0)
        {
            // Caught before ToDuration() ever runs: NodaTime throws InvalidOperationException for
            // exactly this, and that message names neither this value nor its configuration path.
            failures.Add(
                $"{path} — '{text}' has a non-zero month or year component. A month is not a "
                + "fixed length, so it cannot become a Duration; express this as weeks, days, "
                + "hours, minutes, or seconds instead.");
            return null;
        }

        var duration = period.ToDuration();
        if (duration < Duration.Zero)
        {
            failures.Add($"{path} — '{text}' is a negative duration, which is not a meaningful delay.");
            return null;
        }

        return duration;
    }

    /// <summary>A duration that may be absent entirely, meaning "use the library's own default".</summary>
    private static Duration? ResolveOptionalDuration(string path, string? text, ImmutableArray<string>.Builder failures) =>
        string.IsNullOrWhiteSpace(text) ? null : ResolveDuration(path, text, failures);

    private static Instant? ResolveOptionalInstant(string path, string? text, ImmutableArray<string>.Builder failures)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parsed = InstantPattern.ExtendedIso.Parse(text);
        if (parsed.Success)
        {
            return parsed.Value;
        }

        failures.Add($"{path} — '{text}' is not an ISO-8601 instant, for example '2024-01-01T00:00:00Z'.");
        return null;
    }

    private static Compression ResolveCompression(string path, string? text, ImmutableArray<string>.Builder failures)
    {
        // IsNullOrWhiteSpace, for the reason spelled out in ResolveStypeIn: an empty
        // configuration value means "absent", not "invalid".
        if (string.IsNullOrWhiteSpace(text))
        {
            return Compression.None;
        }

        if (WireStrings.TryParseCompression(text, out var compression))
        {
            return compression;
        }

        failures.Add($"{path}:Compression — '{text}' is not a Databento compression. Use 'none' or 'zstd'.");
        return Compression.None;
    }

    private static SlowReaderBehavior? ResolveSlowReader(string path, string? text, ImmutableArray<string>.Builder failures)
    {
        // IsNullOrWhiteSpace, for the reason spelled out in ResolveStypeIn: an empty
        // configuration value means "absent", not "invalid".
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (SlowReaderBehaviorWireStrings.TryParse(text, out var behavior))
        {
            return behavior;
        }

        failures.Add(
            $"{path}:SlowReaderBehavior — '{text}' is not a recognised slow-reader behaviour. Use "
            + "'warn' or 'skip'.");
        return null;
    }

    /// <summary>
    /// Parses <c>host:port</c> as an <see cref="IPEndPoint"/> when the host is a literal address,
    /// or a <see cref="DnsEndPoint"/> otherwise — split on the last colon so an IPv6 literal's own
    /// colons do not confuse the split. <see langword="null"/> leaves the gateway to
    /// <c>LiveClient</c>'s own derivation, and this resolver deliberately does not derive it too.
    /// </summary>
    /// <remarks>
    /// <b>The port range is checked here rather than left to <see cref="DnsEndPoint"/>.</b>
    /// <see cref="int.TryParse(ReadOnlySpan{char}, IFormatProvider, out int)"/> accepts any
    /// <see cref="int"/>, and <see cref="DnsEndPoint"/>'s constructor throws
    /// <see cref="ArgumentOutOfRangeException"/> for anything outside
    /// <see cref="IPEndPoint.MinPort"/>–<see cref="IPEndPoint.MaxPort"/> — so
    /// <c>"lsg.databento.com:99999"</c> escaped <c>Resolve</c> as an exception naming
    /// <c>port</c>, past the failure list this type exists to collect, past
    /// <c>ValidateOnStart</c>, and into a consumer's face naming neither the session nor the
    /// configuration path. A bad configuration value is expected, not exceptional, so it folds
    /// into the same failure the rest of this method reports.
    /// <see cref="IPEndPoint.TryParse(string, out IPEndPoint)"/> above already rejects an
    /// out-of-range port on a literal address; this is the other branch.
    /// </remarks>
    private static EndPoint? ResolveGateway(string path, string? text, ImmutableArray<string>.Builder failures)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (IPEndPoint.TryParse(text, out var ipEndPoint))
        {
            return ipEndPoint;
        }

        var separator = text.LastIndexOf(':');
        if (separator > 0
            && separator < text.Length - 1
            && int.TryParse(text.AsSpan(separator + 1), CultureInfo.InvariantCulture, out var port)
            && port is >= IPEndPoint.MinPort and <= IPEndPoint.MaxPort)
        {
            return new DnsEndPoint(text[..separator], port);
        }

        failures.Add(
            $"{path}:Gateway — '{text}' is not a gateway address. Use 'host:port', for example "
            + "'lsg.databento.com:13000' or '127.0.0.1:13000'.");
        return null;
    }
}

/// <summary>The outcome of resolving one session: the session, or every reason it could not be.</summary>
public sealed class LiveSessionResolutionResult
{
    private LiveSessionResolutionResult(ResolvedLiveSession? session, ImmutableArray<string> failures)
    {
        Session = session;
        Failures = failures;
    }

    /// <summary>The resolved session, or <see langword="null"/> when resolution failed.</summary>
    public ResolvedLiveSession? Session { get; }

    /// <summary>Every failure, each naming its configuration path. Empty on success.</summary>
    public ImmutableArray<string> Failures { get; }

    /// <summary>Whether the session resolved.</summary>
    [MemberNotNullWhen(true, nameof(Session))]
    public bool Succeeded => Session is not null;

    // Named Success rather than Succeeded: the latter collides with the Succeeded property above
    // — C# does not allow a property and a method to share a name, static or not — and this
    // factory is internal, so renaming it does not touch the public surface Tasks 5-11 depend on.
    internal static LiveSessionResolutionResult Success(ResolvedLiveSession session) => new(session, []);

    internal static LiveSessionResolutionResult Failed(ImmutableArray<string> failures) => new(null, failures);
}
