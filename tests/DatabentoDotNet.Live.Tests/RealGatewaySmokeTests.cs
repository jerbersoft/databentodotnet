using DatabentoDotNet.Dbn;
using NodaTime;

namespace DatabentoDotNet.Live.Tests;

/// <summary>
/// Opt-in tests against the <b>real</b> Databento live gateway.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these exist.</b> <see cref="MockLiveGateway"/> is a port of upstream's mock gateway —
/// a fiction ported from a fiction. Everything the client believes about the wire came from
/// reading `live/protocol.rs`, and nothing had ever checked it against the gateway itself. These
/// tests are that check, and they need only <see cref="LiveClient.ConnectAsync"/> and
/// <see cref="LiveClient.AuthenticateAsync"/>, which is why they land before subscriptions rather
/// than after the record loop: an assumption that is wrong here is an assumption three later
/// issues would have been built on.
/// </para>
/// <para>
/// <b>The line these do not cross is <c>start_session</c>, not <c>subscribe</c>.</b> Live
/// streaming is billed by data volume, and no data moves until the session is started — a
/// subscription sent before that tells the gateway what to send later and moves nothing itself.
/// So one of these does subscribe, and none of them start a session, which is what makes the
/// whole class free to run.
/// </para>
/// <para>
/// <b>The rule is that a session needs its own opt-in, not that no test may ever start one.</b>
/// That test exists — <see cref="RealGatewaySessionTests"/>, which runs the whole lifecycle
/// because the mock gateway and the client were written from the same reading of
/// <c>live/protocol.rs</c> and so cannot confirm the metadata block or the record framing between
/// them. It carries <see cref="LiveCredentials.SessionVariable"/> as a second gate on top of this
/// class's <c>Category=Live</c>, so it never runs by accident.
/// </para>
/// <para>
/// <b>Nothing new belongs in <em>this</em> class past that line.</b> A smoke test that quietly
/// grows a <c>start_session</c> is a smoke test that quietly grows a bill, and it would take the
/// whole class's "free to run" guarantee with it. Anything that needs a session goes next door,
/// behind the second gate. See ROADMAP.md §4.
/// </para>
/// <para>
/// <b>They skip rather than fail when no key is configured</b>, and CI filters the category out
/// by name as well. See <see cref="LiveCredentials"/>.
/// </para>
/// <para>
/// <b>A live data license is a separate entitlement from historical access.</b> An account with
/// full historical access to a dataset is still answered
/// <c>success=0|error=A live data license is required to access XNAS.ITCH.</c> when it
/// authenticates against that dataset's live gateway. <see cref="LiveCredentials.DefaultDataset"/>
/// therefore names a feed a plain subscription tends to carry, and
/// <see cref="LiveCredentials.DatasetVariable"/> overrides it.
/// </para>
/// </remarks>
[Trait("Category", "Live")]
public class RealGatewaySmokeTests
{
    /// <summary>Gate for every <c>SkipUnless</c> in this class.</summary>
    public static bool IsConfigured => LiveCredentials.IsConfigured;

    /// <summary>
    /// A syntactically valid key that is not a real one, so <see cref="ApiKey"/> accepts it and
    /// the gateway is the thing that rejects it.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="ApiKey.Length"/> rather than typed out. The hand-counted literal
    /// this replaced was 30 characters, and <see cref="ApiKey"/> rejected it before the test ever
    /// reached the gateway — a fine outcome for the library, and a waste of a round trip for the
    /// test.
    /// </remarks>
    private static readonly string NotARealKey = "db-" + new string('0', ApiKey.Length - 3);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact(SkipUnless = nameof(IsConfigured), Skip = LiveCredentials.SkipReason)]
    public async Task Handshake_AgainstTheRealGateway_AuthenticatesAndOpensASession()
    {
        await using var client = new LiveClient
        {
            ApiKey = LiveCredentials.ApiKey,
            Dataset = LiveCredentials.Dataset,
            ConnectTimeout = Duration.FromSeconds(15),
            AuthTimeout = Duration.FromSeconds(15),
        };

        // No Gateway override: this resolves the host through LiveGateway.For and a real DNS
        // lookup, which is the only thing that ever exercises that transformation for real.
        await client.ConnectAsync(Cancel);

        try
        {
            await client.AuthenticateAsync(Cancel);
        }
        catch (DatabentoAuthenticationException rejected)
            when (rejected.Error?.Contains("live data license", StringComparison.OrdinalIgnoreCase) == true)
        {
            Assert.Fail(
                $"The account has no *live* data license for '{LiveCredentials.Dataset}'. Historical "
                + $"access to a dataset does not carry live access to it. Set "
                + $"{LiveCredentials.DatasetVariable} in .env to a dataset this account is licensed "
                + $"for live. The gateway said: {rejected.Error}");
        }

        Assert.True(client.IsAuthenticated);
        Assert.NotNull(client.SessionId);
        Assert.NotEmpty(client.SessionId);

        // Stop here. The next line on the wire would be a subscription, and this test bills nothing.
        await client.CloseAsync();
        Assert.False(client.IsAuthenticated);
    }

    [Fact(SkipUnless = nameof(IsConfigured), Skip = LiveCredentials.SkipReason)]
    public async Task Handshake_TheRealGreeting_IsTheShapeTheMockGatewaySends()
    {
        // The reconciliation this whole file exists for. Upstream's mock — and ours, until this
        // test — greets with a bare token; the real gateway sends `lsg_version=N.N.N`. A mock that
        // disagrees with the gateway about the very first line teaches every test written against
        // it something false.
        await using var client = new LiveClient
        {
            ApiKey = LiveCredentials.ApiKey,
            Dataset = LiveCredentials.Dataset,
            ConnectTimeout = Duration.FromSeconds(15),
            AuthTimeout = Duration.FromSeconds(15),
        };

        await client.ConnectAsync(Cancel);
        try
        {
            await client.AuthenticateAsync(Cancel);
        }
        catch (DatabentoAuthenticationException)
        {
            // The greeting arrives before the credentials are judged, so it is readable either way.
        }

        Assert.NotNull(client.Greeting);
        Assert.StartsWith(
            MockLiveGateway.GreetingPrefix, client.Greeting, StringComparison.Ordinal);
    }

    [Fact(SkipUnless = nameof(IsConfigured), Skip = LiveCredentials.SkipReason)]
    public async Task Handshake_WithAKeyTheGatewayRejects_ThrowsWithoutLeakingIt()
    {
        // Deterministic regardless of what this account is entitled to: a well-formed key that is
        // not a real one is refused by every gateway, so this exercises the rejection path against
        // the real thing without depending on any license.
        await using var client = new LiveClient
        {
            ApiKey = new ApiKey(NotARealKey),
            Dataset = LiveCredentials.Dataset,
            ConnectTimeout = Duration.FromSeconds(15),
            AuthTimeout = Duration.FromSeconds(15),
        };

        await client.ConnectAsync(Cancel);

        var rejected = await Assert.ThrowsAsync<DatabentoAuthenticationException>(
            () => client.AuthenticateAsync(Cancel));

        Assert.NotNull(rejected.Error);

        // The key must not reach the message, the properties, or anything ToString renders. The
        // gateway *echoes the client's auth field back* on a malformed reply, so this is not a
        // theoretical concern — see DatabentoAuthenticationException.Response.
        var rendered = rejected.ToString();
        Assert.DoesNotContain(NotARealKey, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(
            NotARealKey[..^ApiKey.BucketIdLength], rendered, StringComparison.Ordinal);

        // And the real key, which was never sent on this connection, certainly must not appear.
        Assert.DoesNotContain(
            LiveCredentials.ApiKey.Value, rendered, StringComparison.Ordinal);

        Assert.False(client.IsConnected);
    }

    [Fact(SkipUnless = nameof(IsConfigured), Skip = LiveCredentials.SkipReason)]
    public async Task Subscribe_AgainstTheRealGateway_IsAcceptedWithoutStartingASession()
    {
        // Free, for the same reason the handshake tests are: subscription lines travel before
        // start_session, so the gateway parses this one and sends nothing back. Nothing is billed.
        await using var client = new LiveClient
        {
            ApiKey = LiveCredentials.ApiKey,
            Dataset = LiveCredentials.Dataset,
            ConnectTimeout = Duration.FromSeconds(15),
            AuthTimeout = Duration.FromSeconds(15),
        };

        await client.ConnectAsync(Cancel);
        await client.AuthenticateAsync(Cancel);

        var sent = await client.SubscribeAsync(
            new Subscription { Schema = Schema.Trades, Symbols = Symbols.From("AAPL") },
            Cancel);

        Assert.Equal(1u, sent.Id);
        Assert.Single(client.Subscriptions);
        Assert.True(client.IsConnected);

        // What this proves and what it does not. It proves the whole path — gateway resolution,
        // the handshake, and a subscription line built by this client — survives contact with the
        // real gateway, and that the gateway does not reset the connection on the shape of the
        // line. It does not prove the gateway *accepted* the subscription: a rejection arrives as
        // an error line, and nothing in the client can read one until #22 lands the record loop.
        // The stronger version of this test belongs there.
        await client.CloseAsync();
    }
}
