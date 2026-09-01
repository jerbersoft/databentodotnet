using Microsoft.Extensions.Logging;
using NodaTime;

namespace DatabentoDotNet.Extensions.Hosting.Internal;

/// <summary>Source-generated log messages for the hosted live session.</summary>
/// <remarks>
/// <para>
/// <b>Event ids are stable identifiers and are not to be renumbered.</b> A caller can filter on
/// one; changing it out from under them silently breaks that, in a way no compiler catches. Add a
/// new id for a new message rather than reusing or shifting one of these.
/// </para>
/// <para>
/// <b>Nothing here is per record, and that is both PORTING.md §2's rule and the allocation
/// guarantee agreeing.</b> The rule is that this library logs only what the caller cannot
/// otherwise see — and a caller sees every record, because they are handed each one. What they
/// cannot see is a reconnect: it happens between their calls, and without these lines a session
/// that dropped and recovered at 03:00 is indistinguishable from one that never dropped.
/// </para>
/// </remarks>
internal static partial class ExtensionsLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Live session '{Session}' started on {Dataset} with {Subscriptions} subscription(s).")]
    public static partial void SessionStarted(ILogger logger, string session, string dataset, int subscriptions);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Live session '{Session}' dropped; reconnect attempt {Attempt} of {MaxAttempts} in {Delay}.")]
    public static partial void ReconnectAttempted(ILogger logger, string session, int attempt, int maxAttempts, Duration delay, Exception cause);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "Live session '{Session}' reconnected after {Attempt} attempt(s). This is a newly billed session.")]
    public static partial void ReconnectSucceeded(ILogger logger, string session, int attempt);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error,
        Message = "Live session '{Session}' gave up after {Attempt} consecutive failed reconnects.")]
    public static partial void ReconnectExhausted(ILogger logger, string session, int attempt, Exception cause);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information,
        Message = "Live session '{Session}' ended after {Records} record(s).")]
    public static partial void SessionEnded(ILogger logger, string session, long records);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
        Message = "Live session '{Session}' did not close within {Timeout}; the socket is being dropped instead.")]
    public static partial void CloseTimedOut(ILogger logger, string session, Duration timeout);
}
