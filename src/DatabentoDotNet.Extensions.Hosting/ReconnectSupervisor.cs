using NodaTime;

namespace DatabentoDotNet.Extensions.Hosting;

/// <summary>
/// The reconnection schedule for one live session: exponential backoff with jitter, bounded by
/// consecutive failures.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all, given PORTING.md §4.</b> That file says twice that <c>reconnect</c>
/// and <c>resubscribe</c> are deliberately separate and are not to be fused into an
/// auto-reconnect. That is a rule about <c>LiveClient</c>, and <b>a hosted service is precisely
/// the caller it defers to</b>: the library still does not fuse them, and this type makes the
/// caller's decision once, explicitly, with a bound on it.
/// </para>
/// <para>
/// <b><see cref="ResolvedReconnect.MaxAttempts"/> bounds <em>consecutive</em> failures</b> and
/// <see cref="RecordSuccess"/> resets the counter, so a gateway that flaps every ten minutes
/// reconnects indefinitely. That is deliberate — the alternative silently stops a worker
/// overnight. <b>Every successful reconnect starts a newly billed session</b>, so a reconnect
/// storm is a billing event and not merely a connection event; the bound is what caps it.
/// </para>
/// <para>
/// <b>Equal jitter, and it is not configurable.</b> Each delay is uniform between half the base
/// and the base. Full jitter — uniform between zero and the base — turns a bounded backoff into a
/// tight retry loop against a gateway that is already struggling, and each attempt costs money.
/// The purpose of any jitter here is to stop a restarted fleet reconnecting in lockstep, and a
/// knob for that is a knob whose correct value is never anything but "on".
/// </para>
/// </remarks>
public sealed class ReconnectSupervisor
{
    private readonly ResolvedReconnect _policy;
    private int _consecutiveFailures;

    /// <summary>Creates a supervisor for one policy.</summary>
    public ReconnectSupervisor(ResolvedReconnect policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
    }

    /// <summary>The policy this supervisor enforces.</summary>
    public ResolvedReconnect Policy => _policy;

    /// <summary>How many attempts have been handed out since the last <see cref="RecordSuccess"/>.</summary>
    public int ConsecutiveFailures => _consecutiveFailures;

    /// <summary>
    /// Supplies the jitter factor, in <c>[0, 1)</c>. Defaults to <see cref="Random.Shared"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A seam so a test can state the schedule, not a knob: nothing in the options model reaches
    /// this, and nothing should.
    /// </para>
    /// <para>
    /// <b>The <c>[0, 1)</c> above is enforced, not merely requested.</b>
    /// <see cref="TryNextDelay"/> clamps whatever this returns, because both properties the
    /// schedule actually promises depend on it: a factor above 1 would put a delay past the
    /// <see cref="ResolvedReconnect.MaxDelay"/> ceiling <see cref="BaseDelay"/> just capped it at,
    /// and one below 0 would put it under the half-the-base floor that is the whole difference
    /// between equal jitter and full jitter. This is a public <c>init</c> property, so "a caller
    /// would not do that" is an assumption about a caller rather than a property of this type.
    /// </para>
    /// </remarks>
    public Func<double> Jitter { get; init; } = Random.Shared.NextDouble;

    /// <summary>Waits out a delay. Defaults to a real wait.</summary>
    /// <remarks>
    /// The same kind of seam as <see cref="Jitter"/>, and it is what lets
    /// <c>LiveSessionReconnectTests</c> assert a thirty-second backoff without taking thirty
    /// seconds. <c>Duration.ToTimeSpan()</c> rather than the banned type by name.
    /// </remarks>
    public Func<Duration, CancellationToken, Task> Delay { get; init; } =
        static (delay, cancellationToken) => Task.Delay(delay.ToTimeSpan(), cancellationToken);

    /// <summary>
    /// Takes the next delay, or reports that the policy is exhausted or disabled.
    /// </summary>
    /// <param name="delay">The delay to wait before the next attempt, or zero on <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when another attempt is allowed.</returns>
    public bool TryNextDelay(out Duration delay)
    {
        if (!_policy.Enabled || _consecutiveFailures >= _policy.MaxAttempts)
        {
            delay = Duration.Zero;
            return false;
        }

        _consecutiveFailures++;

        var expected = BaseDelay(_consecutiveFailures).ToInt64Nanoseconds();
        var half = expected / 2;

        // The clamp closes the interval at 1 rather than reproducing the half-open [0, 1) exactly,
        // and that costs nothing: a factor of exactly 1 yields the base delay, which is the value
        // BaseDelay already capped at MaxDelay, so the schedule's two guarantees still hold. What
        // it buys is that neither of them depends on a supplier honouring the contract.
        var jitter = Math.Clamp(Jitter(), 0.0, 1.0);

        delay = Duration.FromNanoseconds(half + (long)(half * jitter));
        return true;
    }

    /// <summary>Records that a session started, resetting the consecutive-failure count.</summary>
    public void RecordSuccess() => _consecutiveFailures = 0;

    /// <summary>
    /// The un-jittered delay for a one-based attempt number: the initial delay doubled once per
    /// previous attempt, capped at the ceiling.
    /// </summary>
    /// <remarks>
    /// Written as a loop rather than as <c>initial &lt;&lt; (attempt - 1)</c> because the shift
    /// overflows long before the <c>Math.Min</c> that would have capped it, and an overflowed
    /// backoff is a negative delay.
    /// </remarks>
    private Duration BaseDelay(int attempt)
    {
        var ceiling = _policy.MaxDelay.ToInt64Nanoseconds();
        var scaled = _policy.InitialDelay.ToInt64Nanoseconds();

        for (var i = 1; i < attempt && scaled < ceiling; i++)
        {
            scaled = scaled > ceiling / 2 ? ceiling : scaled * 2;
        }

        return Duration.FromNanoseconds(Math.Min(scaled, ceiling));
    }
}
