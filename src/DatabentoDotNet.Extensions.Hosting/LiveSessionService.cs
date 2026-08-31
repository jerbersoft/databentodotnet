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
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _runner.RunAsync(stoppingToken);
}
