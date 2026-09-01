using DatabentoDotNet.Dbn;
using NodaTime;

namespace DatabentoDotNet.Live.Tests;

/// <summary>
/// Serves the gateway's side of one live handshake — authenticate, then subscribe, then start.
/// </summary>
/// <remarks>
/// <para>
/// Six files carried their own near-identical copy of this before
/// <see href="https://github.com/jerbersoft/databentodotnet/issues/97">#97</see>, and three of M6's
/// defects were the same species: gateway sequencing written from the mock's shape rather than from
/// its contract. The two rules below were each learned once and then restated in every copy, which
/// is the arrangement that let them drift out of a reader's way.
/// </para>
/// <para>
/// <b>Rule one: the three steps are awaited in order, inside one task, started before the client
/// side runs.</b> <see cref="MockLiveGateway.ExpectSubscribeAsync"/> and
/// <see cref="MockLiveGateway.StartAsync"/> both read from the accepted connection before their
/// first <see langword="await"/>, through a <c>RequireReader()</c> that throws
/// <em>synchronously</em> when nothing has been accepted yet. Starting the three as independent
/// tasks therefore faults the later two immediately — deterministically, not as a timing race —
/// because <see cref="MockLiveGateway.AuthenticateAsync(string, Duration?, CancellationToken)"/> is
/// what accepts the connection. And the whole task has to be started <em>before</em> the client
/// side is awaited, because the client blocks inside its own handshake until the gateway answers:
/// get that backwards and both sides wait on each other rather than one of them failing loudly.
/// </para>
/// <para>
/// <b>Rule two: a transient failure is provoked with silence, never with a close.</b> Nothing here
/// does that — it belongs to the tests that drive this type — but it is the other rule the copies
/// carried. A read timeout is a failure the mock produces by doing nothing at all;
/// <see cref="MockLiveGateway.CloseAsync"/> delivers a clean end of stream, which
/// <c>IsTransient</c> does not treat as a failure, so a test that closes to provoke a reconnect
/// gets no reconnect and then hangs waiting for a second handshake nobody attempts. Close only
/// afterwards, to release the gateway's half for a fresh connection.
/// </para>
/// <para>
/// <b>The cancellation token has no default, and that is deliberate.</b> Every other entry point on
/// <see cref="MockLiveGateway"/> defaults it to <see cref="CancellationToken.None"/>, which is
/// right for a method a test awaits directly — the test's own failure surfaces first. This one is
/// started as a detached task and awaited later, so a forgotten token turns a mismatched
/// expectation into a hang for the whole run rather than one red test. Callers in a test project
/// pass <c>TestContext.Current.CancellationToken</c>; the AOT probe, which cannot see xunit, passes
/// its own.
/// </para>
/// <para>
/// <b>Lives here, beside <see cref="MockLiveGateway"/>, so the AOT probe can reach it.</b> The probe
/// publishes natively and cannot reference a test project — xunit and the test SDK are neither
/// trim-safe nor of any use to it — so it compiles this file by <c>&lt;Compile Link&gt;</c>, exactly
/// as it already does <see cref="MockLiveGateway"/> itself. Nothing in this file mentions xunit, and
/// nothing in it may.
/// </para>
/// </remarks>
public static class MockGatewayHandshake
{
    /// <summary>The one symbol <see cref="MboAapl"/> expects a subscription line to name.</summary>
    /// <remarks>
    /// A caller that also has to <em>configure</em> a client or a session to match — the AOT probe
    /// builds its configuration in memory — reads it from here rather than repeating the literal,
    /// so the configured half and the expected half cannot drift apart silently.
    /// </remarks>
    public const string Symbol = "AAPL";

    private static readonly string[] AaplOnly = [Symbol];

    /// <summary>
    /// The subscription every current caller expects: one <c>mbo</c> line over
    /// <c>raw_symbol</c> for <c>AAPL</c>.
    /// </summary>
    /// <remarks>
    /// A factory rather than a constant because <paramref name="start"/> varies: a first handshake
    /// carries the replay start its subscription asked for, and every handshake after a reconnect
    /// carries none, since <c>ResubscribeAsync</c> drops it. A caller wanting a different schema or
    /// symbol builds its own <see cref="ExpectedSubscription"/> and passes it to
    /// <see cref="ServeAsync"/> — which is the point of that parameter, and why this shape does not
    /// invite a seventh copy of the method below.
    /// </remarks>
    /// <param name="start">
    /// The intraday-replay start the line must carry, or <see langword="null"/> for a line that
    /// must carry none.
    /// </param>
    /// <returns>The expectation.</returns>
    public static ExpectedSubscription MboAapl(Instant? start = null) => new()
    {
        Schema = Schema.Mbo,
        StypeIn = SType.RawSymbol,
        Symbols = AaplOnly,
        Start = start,
    };

    /// <summary>
    /// Serves one whole handshake: accept and authenticate, read one subscription line, then read
    /// <c>start_session</c> and send the metadata.
    /// </summary>
    /// <param name="gateway">The gateway to serve.</param>
    /// <param name="cancellationToken">Cancels the exchange. No default — see the type's remarks.</param>
    /// <param name="subscription">
    /// What the one subscription line must say, or <see langword="null"/> for
    /// <see cref="MboAapl"/>'s.
    /// </param>
    /// <param name="sessionId">
    /// The <c>session_id</c> this handshake reports. A real gateway issues a fresh one per session,
    /// which is how a reconnect test tells the second session from the first.
    /// </param>
    /// <returns>A task that completes when the client has been served all three steps.</returns>
    /// <exception cref="MockGatewayException">The client sent something the expectation rejects.</exception>
    public static async Task ServeAsync(
        MockLiveGateway gateway,
        CancellationToken cancellationToken,
        ExpectedSubscription? subscription = null,
        string sessionId = MockLiveGateway.SessionId)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        await gateway.AuthenticateAsync(sessionId, cancellationToken: cancellationToken).ConfigureAwait(false);
        await gateway.ExpectSubscribeAsync(subscription ?? MboAapl(), isLast: true, cancellationToken)
                     .ConfigureAwait(false);
        await gateway.StartAsync(cancellationToken).ConfigureAwait(false);
    }
}
