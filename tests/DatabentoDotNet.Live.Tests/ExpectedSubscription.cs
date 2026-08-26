using DatabentoDotNet.Dbn;
using NodaTime;

namespace DatabentoDotNet.Live.Tests;

/// <summary>
/// What <see cref="MockLiveGateway.ExpectSubscribeAsync"/> expects one subscription line to say.
/// </summary>
/// <remarks>
/// <para>
/// Upstream's <c>MockGateway::subscribe</c> takes the client's own <c>Subscription</c> and
/// formats the expected fields out of it. That works there because the mock lives inside the
/// crate it tests; here it would mean the harness could not exist until
/// <c>DatabentoDotNet.Live</c> did, inverting the order
/// <see href="https://github.com/jerbersoft/databentodotnet/issues/10">#10</see> is sequenced in —
/// and it would weaken the check, because a test would then be handing the expectation and the
/// implementation the same object.
/// </para>
/// <para>
/// So the expectation is stated in the harness's own terms and the client's <c>Subscription</c>
/// never crosses this boundary. A test writes the symbols, schema and stype it means, twice: once
/// into the client and once into this. That duplication is the point.
/// </para>
/// <para>
/// <b><see cref="Symbols"/> is one chunk, not the whole subscription.</b> The client splits a
/// subscription at 500 symbols per line, so a 1001-symbol subscription is three
/// <see cref="MockLiveGateway.ExpectSubscribeAsync"/> calls with three different
/// <see cref="ExpectedSubscription"/> values, only the last of which is <c>is_last</c>. Upstream
/// compares against the un-chunked symbol list, which is why its own chunking test cannot use
/// <c>expect_subscribe</c> at all.
/// </para>
/// </remarks>
public sealed record ExpectedSubscription
{
    /// <summary>The schema the line must name.</summary>
    public required Schema Schema { get; init; }

    /// <summary>The input symbology the line must name.</summary>
    public required SType StypeIn { get; init; }

    /// <summary>
    /// The symbols this one line must carry, in order. Joined with commas to form the expected
    /// <c>symbols=</c> value, which is how the wire format spells a symbol list.
    /// </summary>
    public required IReadOnlyList<string> Symbols { get; init; }

    /// <summary>Whether the line must request an initial snapshot (<c>snapshot=1</c>).</summary>
    public bool UseSnapshot { get; init; }

    /// <summary>
    /// The intraday-replay start time the line must carry, or <see langword="null"/> if the line
    /// must not carry a <c>start=</c> field at all. Compared as UNIX nanoseconds.
    /// </summary>
    public Instant? Start { get; init; }

    /// <summary>
    /// The subscription id the line must carry, or <see langword="null"/> to require only that
    /// <c>id=</c> is present and parses — which is what upstream asserts, because the client
    /// assigns ids by auto-incrementing and a test rarely knows the number in advance.
    /// </summary>
    public uint? Id { get; init; }

    /// <summary>The expected <c>symbols=</c> value: <see cref="Symbols"/> joined with commas.</summary>
    internal string SymbolsWireValue => string.Join(',', Symbols);
}
