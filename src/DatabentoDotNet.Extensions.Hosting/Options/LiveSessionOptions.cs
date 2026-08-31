namespace DatabentoDotNet.Extensions.Hosting;

/// <summary>One named live session: what to stream, from where, and how to recover.</summary>
/// <remarks>
/// <para>
/// <b>Every property is a <see langword="string"/>, an <see langword="int"/>, a
/// <see langword="bool"/>, or a list of those</b>, and that is forced rather than chosen.
/// <c>T:System.TimeSpan</c> is banned as a type, so RS0030 fires on the property declaration and
/// not merely on <c>TimeSpan.FromSeconds</c>; NodaTime's <c>Duration</c> has no
/// <c>TypeConverter</c> and no settable properties, so a binder fills it with nothing;
/// <c>ApiKey</c> validates in its constructor; <c>Symbols</c> has no binder-shaped form at all;
/// and <c>Schema</c> and <c>SType</c> would bind by their C# names — <c>Mbp1</c> rather than
/// <c>mbp-1</c> — making the configuration file the only place in the Databento ecosystem where
/// the name is spelled differently.
/// </para>
/// <para>
/// All of them are therefore <em>resolved</em> rather than bound, by
/// <see cref="LiveSessionResolver"/>, which is also what <c>LiveSessionValidator</c> calls — the
/// startup validator this options model exists to feed, added by Task 5. One crossing, two
/// callers.
/// </para>
/// </remarks>
public sealed class LiveSessionOptions
{
    /// <summary>The session's API key, or <see langword="null"/> to use the root's.</summary>
    public string? ApiKey { get; set; }

    /// <summary>The dataset, as its wire name — for example <c>EQUS.MINI</c>.</summary>
    public string? Dataset { get; set; }

    /// <summary>What to subscribe to. At least one.</summary>
    public IList<SubscriptionOptions> Subscriptions { get; set; } = [];

    /// <summary>How to recover from a dropped connection.</summary>
    public ReconnectOptions Reconnect { get; set; } = new();

    /// <summary>Whether to ask the gateway to stamp each record with its send time.</summary>
    public bool SendTsOut { get; set; }

    /// <summary>Session compression, as a wire string: <c>none</c> or <c>zstd</c>. Defaults to <c>none</c>.</summary>
    public string? Compression { get; set; }

    /// <summary>What the gateway does when this client reads too slowly: <c>warn</c> or <c>skip</c>.</summary>
    public string? SlowReaderBehavior { get; set; }

    /// <summary>The heartbeat interval as an ISO-8601 duration, or <see langword="null"/> for the gateway's default.</summary>
    public string? HeartbeatInterval { get; set; }

    /// <summary>
    /// How long a read may find nothing before the connection is treated as dead, as an ISO-8601
    /// duration, or <see langword="null"/> for <c>LiveClient</c>'s own derivation from the
    /// heartbeat interval.
    /// </summary>
    /// <remarks>
    /// This is what turns a silent gateway into a <c>HeartbeatTimeoutException</c>, which is the
    /// transient failure the reconnect policy exists for. Lowering it in a test is also how
    /// <c>LiveSessionReconnectTests</c> provokes one without waiting thirty-five seconds.
    /// </remarks>
    public string? ReadTimeout { get; set; }

    /// <summary>
    /// The gateway to connect to as <c>host:port</c>, or <see langword="null"/> to derive it from
    /// <see cref="Dataset"/>.
    /// </summary>
    /// <remarks>
    /// Left <see langword="null"/> this stays null on the resolved session, so
    /// <c>LiveClient</c> derives it through <c>LiveGateway.For</c>. The resolver deliberately does
    /// not derive it too: two derivations of one value are two things that can drift.
    /// </remarks>
    public string? Gateway { get; set; }
}

/// <summary>One subscription within a session.</summary>
public sealed class SubscriptionOptions
{
    /// <summary>The schema, as its wire string — <c>mbp-1</c>, <c>trades</c>, <c>ohlcv-1s</c>.</summary>
    public string? Schema { get; set; }

    /// <summary>The input symbology, as its wire string. Defaults to <c>raw_symbol</c>.</summary>
    public string? StypeIn { get; set; }

    /// <summary>
    /// The symbols. A single entry of <c>ALL_SYMBOLS</c> means the whole dataset.
    /// </summary>
    public IList<string> Symbols { get; set; } = [];

    /// <summary>
    /// An ISO-8601 instant to replay from before going live, or <see langword="null"/> for
    /// real-time only.
    /// </summary>
    public string? Start { get; set; }

    /// <summary>Whether to ask for a book snapshot first. Only the <c>mbo</c> schema supports it.</summary>
    public bool UseSnapshot { get; set; }
}

/// <summary>How a session recovers from a dropped connection.</summary>
public sealed class ReconnectOptions
{
    /// <summary>Whether to reconnect at all. Defaults to <see langword="true"/>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The first backoff delay, as an ISO-8601 duration. Defaults to <c>PT1S</c>.</summary>
    public string InitialDelay { get; set; } = "PT1S";

    /// <summary>The backoff ceiling, as an ISO-8601 duration. Defaults to <c>PT30S</c>.</summary>
    public string MaxDelay { get; set; } = "PT30S";

    /// <summary>
    /// How many <em>consecutive</em> failures to tolerate before giving up. Defaults to 10.
    /// </summary>
    /// <remarks>
    /// Consecutive, and the counter resets on a successful start — so a gateway that flaps every
    /// ten minutes reconnects indefinitely. That is deliberate: the alternative silently stops a
    /// worker overnight. <b>Every reconnect starts a newly billed session</b>, which is what this
    /// bound is really bounding.
    /// </remarks>
    public int MaxAttempts { get; set; } = 10;
}
