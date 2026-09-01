using Microsoft.Extensions.Hosting;

namespace DatabentoDotNet.Extensions.Hosting;

/// <summary>Runs one live session for as long as the host is up.</summary>
/// <remarks>
/// <para>
/// <b>Thin by construction.</b> Everything worth testing is in <see cref="LiveSessionRunner"/>,
/// which takes a resolved session and a handler and needs no host and no container — so
/// <c>MockLiveGateway</c> drives it directly. What is left here is the two lines that make a
/// runner a hosted service, and they are the two lines below.
/// </para>
/// <para>
/// <b><see cref="StartAsync"/> is overridden so a bad session fails the host's boot.</b>
/// <see cref="BackgroundService.StartAsync"/> awaits <see cref="ExecuteAsync"/> only until its
/// first yield, so a session established inside <c>ExecuteAsync</c> would fail in the background
/// with the host already up and serving traffic it cannot fulfil. Connecting, authenticating,
/// subscribing and starting therefore happen here, before <c>base.StartAsync</c>.
/// </para>
/// <para>
/// <b>Nothing here calls <c>IHostApplicationLifetime</c>.</b>
/// <c>HostOptions.BackgroundServiceExceptionBehavior</c> — a type this package does not
/// reference, and does not need to; see <c>Microsoft.Extensions.Hosting.Abstractions</c>'s own
/// package-choice remarks in <c>Directory.Packages.props</c> — has defaulted to
/// <c>StopHost</c> since .NET 6, so an exception out of <see cref="ExecuteAsync"/> — which is
/// what a faulted handler becomes — already stops the host. A second mechanism for the same
/// outcome would differ only in its log line.
/// </para>
/// <para>
/// <b><see cref="StopAsync"/> is overridden too, and for a reason as narrow as it is easy to
/// delete by mistake — do not remove it as "redundant with the base class" without reading this
/// first.</b> <c>BackgroundService.StartAsync</c> schedules <c>ExecuteAsync</c> as
/// <c>Task.Run(() =&gt; ExecuteAsync(_stoppingCts.Token), _stoppingCts.Token)</c> — the
/// <em>same</em> token both gates whether the thread pool ever invokes the delegate and is the
/// <c>stoppingToken</c> <see cref="ExecuteAsync"/> receives. If
/// <c>BackgroundService.StopAsync</c> cancels that token before the thread pool dequeues the
/// work item, <c>Task.Run</c> does not invoke the delegate at all — this is documented
/// <see cref="Task.Run(Func{Task}, CancellationToken)"/> behaviour, not a bug in it — and the
/// returned task completes <see cref="TaskStatus.Canceled"/> with
/// <see cref="LiveSessionRunner.RunAsync"/> never entered. <c>BackgroundService.StopAsync</c>
/// then awaits that task via
/// <c>WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing)</c>,
/// which swallows the resulting <see cref="OperationCanceledException"/> and returns as though
/// shutdown completed cleanly. A caller who had just called <c>host.StopAsync()</c> would
/// observe it return with no exception while <see cref="Runner"/>'s
/// <see cref="LiveSessionRunner.State"/> was still <see cref="LiveSessionState.Running"/> and
/// <see cref="LiveSessionRunner.Fault"/> still <see langword="null"/> — a live session, already
/// billed by <see cref="LiveSessionRunner.StartSessionAsync"/>, left connected and undrained
/// after the host reported itself stopped, and a health check reading <c>Runner.State</c> would
/// call it healthy. Verified against the exact
/// <c>Microsoft.Extensions.Hosting.Abstractions</c> <c>net10.0</c> assembly this package
/// references, and reproduced in isolation against the live runtime with a single-worker thread
/// pool — see #95.
/// </para>
/// <para>
/// <c>RunAsync</c>'s cooperative-cancellation catch admits exactly one exit, for the token this
/// method itself was handed; any other exception raised inside its pump loop — including an
/// unrelated <see cref="OperationCanceledException"/> — falls to the generic catch, which moves
/// <see cref="LiveSessionRunner.State"/> off <see cref="LiveSessionState.Running"/> before it
/// rethrows. So <see cref="BackgroundService.ExecuteTask"/> ending
/// <see cref="TaskStatus.Canceled"/> is the common shape of "the delegate was never dispatched",
/// but it is not a guarantee of it by itself — an async method that lets any
/// <see cref="OperationCanceledException"/> escape also leaves its task
/// <see cref="TaskStatus.Canceled"/>, whether or not the delegate ran. What actually carries the
/// correctness of the guard below is <see cref="LiveSessionRunner.State"/> still being
/// <see cref="LiveSessionState.Running"/>: any exception <c>RunAsync</c>'s own try/catch lets
/// past it has already moved <c>State</c> away from <see cref="LiveSessionState.Running"/>, since
/// the generic catch sets it before rethrowing — so it is the two conditions together, not
/// <c>IsCanceled</c> on its own, that make it safe to run <c>RunAsync</c> again. That second run
/// reuses <c>RunAsync</c>'s own
/// already-cancelled-token path (loop condition false on entry, straight to the shared close) —
/// exactly what a dispatched-and-immediately-cancelled run would have done — so this is not a
/// second code path to trust: it is the same one, invoked from the place that discovered it was
/// never going to run on its own.
/// </para>
/// <para>
/// <b>A narrower gap this fix does not close:</b> if <c>RunAsync</c> is still genuinely running
/// when <c>HostOptions.ShutdownTimeout</c> (thirty seconds, by default) elapses,
/// <c>ExecuteTask.IsCanceled</c> is <see langword="false"/> — the task is merely incomplete, not
/// cancelled — so the fallback correctly does not fire, and <c>Runner.State</c> can still be
/// <see cref="LiveSessionState.Running"/> when <see cref="StopAsync"/> returns. That is out of
/// scope for #95, which is the dispatch race described above, not a slow shutdown; closing it
/// would mean guessing whether a task that may still be legitimately in flight has finished,
/// which risks the same double-close this guard exists to avoid.
/// </para>
/// </remarks>
public sealed class LiveSessionService : BackgroundService
{
    private readonly LiveSessionRunner _runner;

    /// <summary>Creates a hosted service around one runner.</summary>
    public LiveSessionService(LiveSessionRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    /// <summary>The runner this service drives.</summary>
    public LiveSessionRunner Runner => _runner;

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await _runner.StartSessionAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // See the type-level remarks: ExecuteTask.IsCanceled is the common shape of "the
        // delegate never ran", but not a guarantee of it alone — State == Running is what
        // actually carries the correctness here, since RunAsync's own try/catch always moves
        // State off Running before letting anything propagate. Both conditions together
        // identify the one case RunAsync cannot have handled itself, and both stay false on
        // every StopAsync call after the first, which is what makes this idempotent.
        if (ExecuteTask is { IsCanceled: true } && _runner.State == LiveSessionState.Running)
        {
            await _runner.RunAsync(new CancellationToken(canceled: true)).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _runner.RunAsync(stoppingToken);
}
