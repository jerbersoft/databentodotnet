using NodaTime;

namespace DatabentoDotNet.Extensions.Hosting.Tests;

/// <summary>
/// The backoff policy, settled without a socket and without waiting.
/// </summary>
/// <remarks>
/// <para>
/// <b>Jitter is injected, so the arithmetic has an answer known before the test runs.</b> The
/// production default is <see cref="Random.Shared"/>, which is exactly what makes a fleet of
/// restarted workers stop reconnecting in lockstep and exactly what makes an assertion about a
/// delay impossible. A <see cref="Func{TResult}"/> returning a fixed value turns the whole schedule
/// into something a table can state.
/// </para>
/// <para>
/// <b>Jitter is applied and is not configurable</b>, which is why there is no option for it and no
/// test that it can be turned off. Its only purpose is to stop a restarted fleet reconnecting
/// together, and a knob for that is a knob whose correct value is never anything but "on".
/// </para>
/// </remarks>
public class ReconnectSupervisorTests
{
    private static ResolvedReconnect Policy(int maxAttempts = 10, int initialSeconds = 1, int maxSeconds = 30) => new()
    {
        Enabled = true,
        InitialDelay = Duration.FromSeconds(initialSeconds),
        MaxDelay = Duration.FromSeconds(maxSeconds),
        MaxAttempts = maxAttempts,
    };

    /// <summary>Jitter at its ceiling, so the delay is the un-jittered base and the doubling shows.</summary>
    private static ReconnectSupervisor Supervisor(ResolvedReconnect policy) =>
        new(policy) { Jitter = () => 1.0 };

    [Fact]
    public void TryNextDelay_DoublesFromTheInitialDelayToTheCeiling()
    {
        var supervisor = Supervisor(Policy());

        var delays = new List<Duration>();
        while (supervisor.TryNextDelay(out var delay))
        {
            delays.Add(delay);
        }

        Assert.Equal(
            [
                Duration.FromSeconds(1),  Duration.FromSeconds(2),  Duration.FromSeconds(4),
                Duration.FromSeconds(8),  Duration.FromSeconds(16), Duration.FromSeconds(30),
                Duration.FromSeconds(30), Duration.FromSeconds(30), Duration.FromSeconds(30),
                Duration.FromSeconds(30),
            ],
            delays);
    }

    [Fact]
    public void TryNextDelay_StopsAfterMaxAttempts()
    {
        var supervisor = Supervisor(Policy(maxAttempts: 3));

        Assert.True(supervisor.TryNextDelay(out _));
        Assert.True(supervisor.TryNextDelay(out _));
        Assert.True(supervisor.TryNextDelay(out _));
        Assert.False(supervisor.TryNextDelay(out var exhausted));
        Assert.Equal(Duration.Zero, exhausted);
        Assert.Equal(3, supervisor.ConsecutiveFailures);
    }

    [Fact]
    public void RecordSuccess_ResetsTheCounterSoAFlappingGatewayReconnectsIndefinitely()
    {
        // MaxAttempts bounds *consecutive* failures. A gateway that drops every ten minutes and
        // reconnects each time is a gateway this keeps serving — the alternative silently stops a
        // worker overnight. Every reconnect is a newly billed session, which is what MaxAttempts
        // is really bounding.
        var supervisor = Supervisor(Policy(maxAttempts: 2));

        Assert.True(supervisor.TryNextDelay(out _));
        Assert.True(supervisor.TryNextDelay(out _));
        Assert.False(supervisor.TryNextDelay(out _));

        supervisor.RecordSuccess();

        Assert.Equal(0, supervisor.ConsecutiveFailures);
        Assert.True(supervisor.TryNextDelay(out var delay));
        // And the schedule restarts, rather than resuming at the ceiling.
        Assert.Equal(Duration.FromSeconds(1), delay);
    }

    [Fact]
    public void TryNextDelay_WhenReconnectionIsDisabled_IsFalseImmediately()
    {
        var supervisor = Supervisor(Policy() with { Enabled = false });

        Assert.False(supervisor.TryNextDelay(out _));
        Assert.Equal(0, supervisor.ConsecutiveFailures);
    }

    [Theory]
    [InlineData(0.0, 500)]   // the floor: half the base
    [InlineData(0.5, 750)]
    [InlineData(1.0, 1000)]  // the ceiling: the base itself
    public void TryNextDelay_AppliesEqualJitterBetweenHalfTheBaseAndTheBase(double jitter, int expectedMilliseconds)
    {
        // Equal jitter rather than full jitter: a delay that can be arbitrarily close to zero
        // turns a bounded backoff into a tight retry loop against a gateway that is already
        // struggling, and every attempt is a billed session.
        var supervisor = new ReconnectSupervisor(Policy()) { Jitter = () => jitter };

        Assert.True(supervisor.TryNextDelay(out var delay));
        Assert.Equal(Duration.FromMilliseconds(expectedMilliseconds), delay);
    }

    [Fact]
    public void TryNextDelay_WithAnInitialDelayPastTheCeiling_UsesTheCeiling()
    {
        // A misconfiguration the resolver already rejects, asserted here anyway: this type is
        // public and constructible directly, so it may not assume the resolver ran.
        var supervisor = Supervisor(Policy(initialSeconds: 60, maxSeconds: 30));

        Assert.True(supervisor.TryNextDelay(out var delay));
        Assert.Equal(Duration.FromSeconds(30), delay);
    }

    [Fact]
    public async Task Delay_ByDefault_IsARealWaitAndAbandonsItWhenCancelled()
    {
        // #100: this half used to be carried by a name and by nothing else — the test below
        // replaced the delegate on its first line, so the default was never once exercised.
        //
        // Thirty seconds against a token that is already cancelled, and both halves of that are the
        // assertion. A seam left as (_, _) => Task.CompletedTask completes and does not throw, so
        // catching the cancellation is what says the default is a real wait; and a real wait that
        // ignored the token would hold the whole run for thirty seconds, so finishing at all is
        // what says it is abandonable. Neither is observable from the other, and a backoff a
        // shutting-down host cannot abandon is a thirty-second stall in every consumer's deploy.
        var supervisor = new ReconnectSupervisor(Policy());

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => supervisor.Delay(Duration.FromSeconds(30), cancellation.Token));
    }

    [Fact]
    public async Task Delay_IsReplaceable()
    {
        // The seam LiveSessionReconnectTests rests on: the delay a test observes is the delay the
        // schedule computed, without the test waiting it out.
        var asked = new List<Duration>();
        var supervisor = new ReconnectSupervisor(Policy())
        {
            Jitter = () => 1.0,
            Delay = (delay, _) => { asked.Add(delay); return Task.CompletedTask; },
        };

        Assert.True(supervisor.TryNextDelay(out var first));
        await supervisor.Delay(first, TestContext.Current.CancellationToken);

        Assert.Equal([Duration.FromSeconds(1)], asked);
    }
}
