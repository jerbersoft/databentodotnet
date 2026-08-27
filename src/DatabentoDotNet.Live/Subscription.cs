using DatabentoDotNet.Dbn;
using NodaTime;

namespace DatabentoDotNet.Live;

/// <summary>
/// One subscription request: what to stream, in which symbology, and optionally from when.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>live::Subscription</c>. Its <c>bon</c>-derived builder becomes
/// <see langword="required"/> init properties — C# 11 checks at every construction site exactly
/// what the builder's type-state checked at <c>build()</c>. CLAUDE.md, "Porting rules".
/// </para>
/// <para>
/// <b>Immutable, where upstream's is mutated in place.</b> Upstream's <c>Client::subscribe</c>
/// takes ownership, assigns <c>id</c> if absent, and pushes the mutated value onto its list; its
/// <c>resubscribe</c> then clears each stored <c>start</c> in place. Neither is expressible on a
/// record, so <see cref="LiveClient.SubscribeAsync"/> returns the subscription it actually sent —
/// <see cref="Id"/> filled in — and keeps that in <see cref="LiveClient.Subscriptions"/>. A
/// caller who wants the assigned id has it, rather than having to read it back out of a list.
/// </para>
/// <para>
/// <b>The two client-side rejections live in <see cref="Validate"/>, not in the init accessors.</b>
/// Both are relationships between two properties, and an init accessor sees only its own value
/// and whatever has been set before it — so the same object would be rejected or accepted
/// depending on the order the initializer happened to list its properties.
/// </para>
/// </remarks>
public sealed record Subscription
{
    /// <summary>The symbols to subscribe to.</summary>
    public required Symbols Symbols { get; init; }

    /// <summary>The record schema to stream.</summary>
    public required Schema Schema { get; init; }

    /// <summary>
    /// The symbology <see cref="Symbols"/> is expressed in. Defaults to
    /// <see cref="SType.RawSymbol"/>, as upstream does.
    /// </summary>
    public SType StypeIn { get; init; } = SType.RawSymbol;

    /// <summary>
    /// Where to replay from before transitioning to live, or <see langword="null"/> for real-time
    /// data only. <see cref="NodaConstants.UnixEpoch"/> requests everything available.
    /// </summary>
    /// <remarks>
    /// An <see cref="Instant"/> on the surface and Unix nanoseconds on the wire — the crossing is
    /// <c>DbnTime.ToUnixNanoseconds</c>. A <c>DateTimeOffset</c> here would truncate to 100 ns
    /// ticks and replay from a different moment than the caller wrote, which is why the BCL types
    /// are banned repo-wide. CLAUDE.md, "Dates and times".
    /// </remarks>
    public Instant? Start { get; init; }

    /// <summary>
    /// Whether to ask for a book snapshot before live updates. Only <see cref="Schema.Mbo"/>
    /// supports it, and it cannot be combined with <see cref="Start"/>.
    /// </summary>
    public bool UseSnapshot { get; init; }

    /// <summary>
    /// The subscription's numeric id, or <see langword="null"/> to let
    /// <see cref="LiveClient.SubscribeAsync"/> assign the next one.
    /// </summary>
    /// <remarks>
    /// The gateway quotes this id in any error it raises about the subscription, which is the
    /// only way to tell which of several concurrent subscriptions a message is about.
    /// </remarks>
    public uint? Id { get; init; }

    /// <summary>
    /// Checks the two combinations the gateway rejects, before anything reaches the socket.
    /// </summary>
    /// <param name="parameterName">The name to report on the exception.</param>
    /// <exception cref="ArgumentException">The subscription cannot be sent.</exception>
    internal void Validate(string parameterName)
    {
        if (Symbols.Kind == SymbolsKind.None)
        {
            throw new ArgumentException(
                "This subscription's Symbols is a default value, which names nothing. Build one "
                + "with Symbols.All, Symbols.From, or Symbols.FromIds.",
                parameterName);
        }

        if (UseSnapshot && Start is not null)
        {
            // Upstream's Error::BadArgument, in the same place and for the same reason: a
            // snapshot is the book as it stands now, and a replay start is a point in the past.
            // Asking for both is a contradiction, not a preference the gateway can resolve.
            throw new ArgumentException(
                "A subscription cannot request a snapshot and an intraday-replay start at the "
                + "same time: a snapshot is the current book, and Start replays from the past.",
                parameterName);
        }

        if (UseSnapshot && Schema != Schema.Mbo)
        {
            // Upstream documents this on the field and leaves the enforcement to the gateway.
            // Rejected here for the same reason HeartbeatInterval's range is: discovering it
            // costs a round trip and a closed connection, and the answer was knowable before the
            // socket was ever written to.
            throw new ArgumentException(
                $"Only the {nameof(Schema.Mbo)} schema supports snapshots; this subscription asks "
                + $"for one with {Schema}. A snapshot is the order book as it stands, and no "
                + "other schema carries a book.",
                parameterName);
        }
    }
}
