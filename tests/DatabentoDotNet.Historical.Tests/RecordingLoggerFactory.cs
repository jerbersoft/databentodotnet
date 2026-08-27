using Microsoft.Extensions.Logging;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// An <see cref="ILoggerFactory"/> that keeps every entry written through it, so a test can
/// assert on what a client logged.
/// </summary>
/// <remarks>
/// <para>
/// The historical client's <c>X-Warning</c> handling has no return value and no property — D2 of
/// <see href="https://github.com/jerbersoft/databentodotnet/issues/35">#35</see> routes it
/// through <c>ILogger</c> rather than wrapping twenty-three endpoint payloads in a response type
/// to carry a header that is almost always absent. This is therefore the only place a test can
/// observe it, which is why the factory is here rather than a mocking package: the assertions
/// need the formatted message, the level and the event id, and nothing else.
/// </para>
/// <para>
/// <b><see cref="ILogger.IsEnabled"/> answers <see langword="true"/> for every level, and that
/// is load-bearing.</b> A source-generated <c>[LoggerMessage]</c> method opens by asking, and
/// returns without doing anything at all if the answer is no — so a factory that filtered would
/// record nothing and every assertion below would pass or fail for the wrong reason.
/// </para>
/// <para>
/// Thread-safe: <c>HistoricalClient</c> is documented as safe for concurrent requests, so a test
/// that sends several at once must not race this.
/// </para>
/// </remarks>
public sealed class RecordingLoggerFactory : ILoggerFactory
{
    private readonly Lock _gate = new();
    private readonly List<RecordedLogEntry> _entries = [];

    /// <summary>Every entry written through this factory, in the order it was written.</summary>
    public IReadOnlyList<RecordedLogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>The entries carrying a given event id, in order.</summary>
    /// <param name="eventId">The numeric event id — see <c>Internal/HistoricalLog.cs</c>.</param>
    /// <returns>The matching entries.</returns>
    public IReadOnlyList<RecordedLogEntry> EntriesWith(int eventId)
    {
        lock (_gate)
        {
            return [.. _entries.Where(entry => entry.EventId.Id == eventId)];
        }
    }

    /// <summary>Creates a logger that records into this factory.</summary>
    /// <param name="categoryName">The category. Recorded on each entry.</param>
    /// <returns>The logger.</returns>
    public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, categoryName);

    /// <summary>
    /// Not supported. This factory is the sink; there is nothing for a provider to add.
    /// </summary>
    /// <param name="provider">Ignored.</param>
    /// <exception cref="NotSupportedException">Always.</exception>
    public void AddProvider(ILoggerProvider provider) =>
        throw new NotSupportedException("RecordingLoggerFactory is its own provider.");

    /// <summary>Does nothing. The recorded entries outlive the factory on purpose.</summary>
    public void Dispose()
    {
        // Deliberately empty rather than clearing: a test that disposes the client it configured
        // — which `await using` does at the end of every test here — must still be able to read
        // what that client logged.
    }

    private void Record(RecordedLogEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    private sealed class RecordingLogger(RecordingLoggerFactory factory, string category) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            factory.Record(new RecordedLogEntry
            {
                Category = category,
                Level = logLevel,
                EventId = eventId,
                Message = formatter(state, exception),
                Exception = exception,
            });
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}

/// <summary>One log entry as <see cref="RecordingLoggerFactory"/> saw it.</summary>
public sealed class RecordedLogEntry
{
    /// <summary>The logger category — the client's type name.</summary>
    public required string Category { get; init; }

    /// <summary>The level the entry was written at.</summary>
    public required LogLevel Level { get; init; }

    /// <summary>
    /// The event id. Its <see cref="EventId.Id"/> is the stable identifier a caller filters on,
    /// and its <see cref="EventId.Name"/> is the source-generated method's name.
    /// </summary>
    public required EventId EventId { get; init; }

    /// <summary>The formatted message, with its arguments already substituted.</summary>
    public required string Message { get; init; }

    /// <summary>The exception the entry carried, or <see langword="null"/>.</summary>
    public Exception? Exception { get; init; }
}
