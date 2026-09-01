using System.Collections.Immutable;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Dbn.Publishers;
using DatabentoDotNet.Live;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace DatabentoDotNet.Extensions.Hosting.Tests;

/// <summary>
/// The courteous-close ceiling: <see cref="LiveSessionRunner.AwaitCloseAsync"/>, and the warning it
/// writes when the ceiling expires.
/// </summary>
/// <remarks>
/// <para>
/// <b>The close is handed in rather than provoked, because it cannot be provoked.</b>
/// <c>LiveClient.CloseAsync()</c> is local teardown — it disposes the reader, the stream and the
/// socket, and waits for no gateway — so over <c>MockLiveGateway</c> it finishes before the timer
/// in every realistic case, and often synchronously, in which case <c>Task.WhenAny</c> returns it
/// without the timer getting a look in. A test that lowered the ceiling far enough to try to
/// out-race it would be asserting on which of two things the scheduler got to first, and would go
/// green or red for reasons unrelated to the branch. Passing a task that never completes makes the
/// expiry the only possible outcome.
/// </para>
/// <para>
/// Before #98 this branch had no test at all, and no configuration key reached
/// <see cref="LiveSessionRunner.CloseTimeout"/> either — so <c>ExtensionsLog.CloseTimedOut</c>, event
/// id 6, was a line the package could not execute for any consumer using the hosted service.
/// </para>
/// </remarks>
public class CloseTimeoutTests
{
    private static ResolvedLiveSession Session() => new()
    {
        Name = "equities",
        ApiKey = new ApiKey(new string('0', ApiKey.Length - 3).Insert(0, "db-")),
        Dataset = Dataset.XnasItch.ToWireString(),
        Subscriptions = [new Subscription { Schema = Schema.Mbo, Symbols = Symbols.From(["AAPL"]) }],
        Reconnect = ResolvedReconnect.Default with { Enabled = false },
    };

    private static LiveSessionRunner Runner(ILogger<LiveSessionRunner> logger, Duration closeTimeout) =>
        new(
            Session(),
            new RecordingHandler(),
            new ReconnectSupervisor(ResolvedReconnect.Default with { Enabled = false }),
            logger)
        {
            CloseTimeout = closeTimeout,
        };

    [Fact]
    public async Task AwaitCloseAsync_WhenTheCloseFinishesFirst_ReportsClosedAndSaysNothing()
    {
        var log = new CapturingLogger();

        Assert.True(await Runner(log, Duration.FromSeconds(30)).AwaitCloseAsync(Task.CompletedTask));
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task AwaitCloseAsync_WhenTheCeilingExpiresFirst_ReportsTimedOutAndWarns()
    {
        var log = new CapturingLogger();

        // Never completes, so the timer is the only thing that can win.
        var neverCloses = new TaskCompletionSource().Task;

        Assert.False(
            await Runner(log, Duration.FromMilliseconds(20)).AwaitCloseAsync(neverCloses));

        var entry = Assert.Single(log.Entries);
        Assert.Equal(6, entry.EventId.Id);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("did not close within", entry.Message, StringComparison.Ordinal);
        Assert.Contains("equities", entry.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ceiling is the configured one, not the five-second default — so a session that raised it
    /// is not cut off at five seconds by something that ignored the key.
    /// </summary>
    [Fact]
    public async Task AwaitCloseAsync_ReportsTheConfiguredCeiling_NotTheDefault()
    {
        var log = new CapturingLogger();

        Assert.False(
            await Runner(log, Duration.FromMilliseconds(20)).AwaitCloseAsync(new TaskCompletionSource().Task));

        // Duration's own formatting, which is what the log renders.
        Assert.Contains(
            Duration.FromMilliseconds(20).ToString(),
            Assert.Single(log.Entries).Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A close that fails inside the ceiling still throws, rather than being reported as a clean
    /// close.
    /// </summary>
    /// <remarks>
    /// The <c>await closing</c> after the race is what makes this true, and it is easy to lose:
    /// <c>Task.WhenAny</c> does not observe its winner's exception, so returning without that
    /// second await would swallow a genuine teardown failure and report success.
    /// </remarks>
    [Fact]
    public async Task AwaitCloseAsync_WhenTheCloseFaultsInsideTheCeiling_Throws()
    {
        var log = new CapturingLogger();
        var failed = Task.FromException(new IOException("the socket went away"));

        var thrown = await Assert.ThrowsAsync<IOException>(
            () => Runner(log, Duration.FromSeconds(30)).AwaitCloseAsync(failed));

        Assert.Equal("the socket went away", thrown.Message);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task AwaitCloseAsync_WithNoClose_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => Runner(new CapturingLogger(), Duration.FromSeconds(1)).AwaitCloseAsync(null!));
    }

    /// <summary>
    /// A runner constructed directly, with no host and no configuration, still gets the documented
    /// five seconds.
    /// </summary>
    [Fact]
    public void CloseTimeout_WhenNobodySetsIt_IsFiveSeconds()
    {
        var runner = new LiveSessionRunner(
            Session(),
            new RecordingHandler(),
            new ReconnectSupervisor(ResolvedReconnect.Default with { Enabled = false }));

        Assert.Equal(Duration.FromSeconds(5), runner.CloseTimeout);
    }

    /// <summary>
    /// The smallest <see cref="ILogger"/> that can answer "was this written, and which one".
    /// </summary>
    /// <remarks>
    /// This package's tests had no logger double before #98 — <c>ObservabilityTests</c> asserts on
    /// metrics, which have <c>IMeterFactory</c> for the purpose, and nothing else needed to read a
    /// log line back. Kept deliberately minimal: it records what was written and does not pretend to
    /// implement scopes.
    /// </remarks>
    private sealed class CapturingLogger : ILogger<LiveSessionRunner>
    {
        private readonly List<LogEntry> _entries = [];

        public ImmutableArray<LogEntry> Entries => [.. _entries];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            _entries.Add(new LogEntry
            {
                Level = logLevel,
                EventId = eventId,
                Message = formatter(state, exception),
            });
        }
    }

    /// <summary>One captured log line.</summary>
    private readonly record struct LogEntry
    {
        public required LogLevel Level { get; init; }

        public required EventId EventId { get; init; }

        public required string Message { get; init; }
    }
}
