using Microsoft.Extensions.Diagnostics.HealthChecks;

// Namespace DatabentoDotNet.Extensions.Hosting, not …Hosting.HealthChecks, even though the file
// sits in a HealthChecks folder. That follows Options/, whose public types — LiveSessionOptions,
// ResolvedLiveSession, LiveSessionResolver — all keep the root namespace: the folders in this
// project group files for a reader, and are not a second axis a consumer has to type out.
namespace DatabentoDotNet.Extensions.Hosting;

/// <summary>
/// Reports one live session's <see cref="LiveSessionRunner.State"/> as a health status.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in, and registered nowhere by default.</b> <c>AddDatabentoLive</c> does not install
/// this; <see cref="DatabentoLiveBuilder.AddHealthCheck"/> does, and a consumer who never calls it
/// has no registration, runs no check, and pays for nothing. That is the whole reason the method
/// is on the builder rather than inside the registration call.
/// </para>
/// <para>
/// <b>The mapping is a decision, so here it is stated rather than left to be read off a
/// <see langword="switch"/>:</b>
/// </para>
/// <list type="table">
///   <listheader><term>State</term><description>Result</description></listheader>
///   <item>
///     <term><see cref="LiveSessionState.Running"/></term>
///     <description>Healthy — started, and reading.</description>
///   </item>
///   <item>
///     <term><see cref="LiveSessionState.NotStarted"/>, <see cref="LiveSessionState.Starting"/></term>
///     <description>Degraded — coming up, not yet serving.</description>
///   </item>
///   <item>
///     <term><see cref="LiveSessionState.Reconnecting"/></term>
///     <description>
///     Degraded rather than unhealthy, and that is the judgement worth spelling out. The backoff
///     is running and bounded, and most drops it exists for recover on the first attempt — so a
///     reconnecting session is one that may well be serving again before the next probe. Reporting
///     it unhealthy would take a pod out of rotation for a blip and, in a fleet, would do so to
///     every pod at once. When the policy is exhausted the session faults, and <em>that</em> is
///     unhealthy.
///     </description>
///   </item>
///   <item>
///     <term><see cref="LiveSessionState.Stopped"/></term>
///     <description>
///     Unhealthy, which reads backwards until you ask what reaches this. A deliberate shutdown
///     never does: the endpoint answering the probe stops with the host, so nobody is asking. What
///     does reach it is a session whose stream ended while the host stayed up — the worker is
///     alive and doing nothing, which is precisely the failure a health probe exists to surface.
///     Reporting it healthy because "it was not an error" would hide a silently dead feed, the
///     failure class this codebase exists to make loud.
///     </description>
///   </item>
///   <item>
///     <term><see cref="LiveSessionState.Faulted"/></term>
///     <description>
///     Unhealthy, carrying <see cref="LiveSessionRunner.Fault"/>'s message as the description and
///     the exception itself on the result.
///     </description>
///   </item>
/// </list>
/// <para>
/// <b>The two failing states report
/// <see cref="HealthCheckRegistration.FailureStatus"/> rather than
/// <see cref="HealthStatus.Unhealthy"/> literally.</b> The framework applies that property only
/// when a check <em>throws</em>, so a check that returns a status of its own has to honour it
/// deliberately or the <c>failureStatus</c> argument on
/// <see cref="DatabentoLiveBuilder.AddHealthCheck"/> would be decorative. It defaults to
/// <see cref="HealthStatus.Unhealthy"/>, which is what the table above describes.
/// </para>
/// </remarks>
public sealed class LiveSessionHealthCheck : IHealthCheck
{
    private readonly LiveSessionRunner _runner;

    /// <summary>Creates a check over one session's runner.</summary>
    /// <param name="runner">The runner to report on.</param>
    public LiveSessionHealthCheck(LiveSessionRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    /// <summary>Reads <see cref="LiveSessionRunner.State"/> and maps it. Never blocks.</summary>
    /// <param name="context">
    /// The framework's context. Its <see cref="HealthCheckContext.Registration"/> supplies the
    /// failure status a caller configured.
    /// </param>
    /// <param name="cancellationToken">Unused: this check reads two fields and returns.</param>
    /// <returns>The session's status, described.</returns>
    /// <remarks>
    /// Synchronous work behind a <see cref="Task{TResult}"/>, because the interface is
    /// asynchronous and this is not. Nothing here touches the socket — a health check that probed
    /// the gateway would bill for a probe and could block the endpoint behind a network timeout;
    /// the runner already knows the answer, having been told by the loop.
    /// <para>
    /// The interpolated descriptions allocate, and that is fine here and only here: this runs once
    /// per probe, not once per record.
    /// </para>
    /// </remarks>
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var failureStatus = context.Registration.FailureStatus;
        var name = _runner.Session.Name;

        return Task.FromResult(_runner.State switch
        {
            LiveSessionState.Running => HealthCheckResult.Healthy(
                $"Session '{name}' is running; {_runner.RecordsReceived} records received."),

            LiveSessionState.NotStarted => HealthCheckResult.Degraded(
                $"Session '{name}' has not been started yet."),

            LiveSessionState.Starting => HealthCheckResult.Degraded(
                $"Session '{name}' is connecting, authenticating or subscribing."),

            LiveSessionState.Reconnecting => HealthCheckResult.Degraded(
                $"Session '{name}' lost its connection and is backing off before another attempt."),

            LiveSessionState.Stopped => new HealthCheckResult(
                failureStatus,
                $"Session '{name}' has stopped after {_runner.RecordsReceived} records; nothing is being read."),

            LiveSessionState.Faulted => new HealthCheckResult(
                failureStatus,
                $"Session '{name}' faulted: {_runner.Fault?.Message}",
                _runner.Fault),

            // Unreachable while LiveSessionState has six members, and here because the compiler is
            // right that an enum can hold any int. A new state added without a row above shows up
            // as a failure naming itself rather than as a silently healthy session.
            _ => new HealthCheckResult(
                failureStatus,
                $"Session '{name}' is in an unrecognised state ({_runner.State})."),
        });
    }
}
