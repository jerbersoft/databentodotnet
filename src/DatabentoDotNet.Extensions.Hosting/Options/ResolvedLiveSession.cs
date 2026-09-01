using System.Collections.Immutable;
using System.Net;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Live;
using NodaTime;

namespace DatabentoDotNet.Extensions.Hosting;

/// <summary>
/// A live session's configuration with every value converted to the type the library actually
/// takes. Produced only by <see cref="LiveSessionResolver"/>.
/// </summary>
/// <remarks>
/// Public, and constructible directly, which is what lets <c>LiveSessionRunner</c> be driven by a
/// test with no host, no container and no configuration provider — the property the whole testing
/// strategy rests on.
/// </remarks>
public sealed record ResolvedLiveSession
{
    /// <summary>The session's registration name, which is also its configuration key.</summary>
    public required string Name { get; init; }

    /// <summary>The validated API key.</summary>
    public required ApiKey ApiKey { get; init; }

    /// <summary>The dataset.</summary>
    public required string Dataset { get; init; }

    /// <summary>The subscriptions to send, in order, after authenticating.</summary>
    public required ImmutableArray<Subscription> Subscriptions { get; init; }

    /// <summary>The reconnection policy.</summary>
    public required ResolvedReconnect Reconnect { get; init; }

    /// <summary>Whether the gateway stamps each record with its send time.</summary>
    public bool SendTsOut { get; init; }

    /// <summary>Session compression.</summary>
    public Compression Compression { get; init; } = Compression.None;

    /// <summary>What the gateway does when this client reads too slowly, or <see langword="null"/> for its default.</summary>
    public SlowReaderBehavior? SlowReaderBehavior { get; init; }

    /// <summary>The heartbeat interval, or <see langword="null"/> for the gateway's default.</summary>
    public Duration? HeartbeatInterval { get; init; }

    /// <summary>The read timeout, or <see langword="null"/> for <c>LiveClient</c>'s own derivation.</summary>
    public Duration? ReadTimeout { get; init; }

    /// <summary>The gateway, or <see langword="null"/> to let <c>LiveClient</c> derive it from <see cref="Dataset"/>.</summary>
    public EndPoint? Gateway { get; init; }
}

/// <summary>A reconnection policy with its durations parsed.</summary>
public sealed record ResolvedReconnect
{
    /// <summary>The default policy: enabled, one second to thirty, ten consecutive attempts.</summary>
    public static ResolvedReconnect Default { get; } = new()
    {
        Enabled = true,
        InitialDelay = Duration.FromSeconds(1),
        MaxDelay = Duration.FromSeconds(30),
        MaxAttempts = 10,
    };

    /// <summary>Whether to reconnect at all.</summary>
    public required bool Enabled { get; init; }

    /// <summary>The first backoff delay.</summary>
    public required Duration InitialDelay { get; init; }

    /// <summary>The backoff ceiling.</summary>
    public required Duration MaxDelay { get; init; }

    /// <summary>How many consecutive failures to tolerate. The counter resets on a successful start.</summary>
    public required int MaxAttempts { get; init; }
}
