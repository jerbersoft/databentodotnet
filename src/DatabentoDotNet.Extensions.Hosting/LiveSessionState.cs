namespace DatabentoDotNet.Extensions.Hosting;

/// <summary>Where a live session is in its lifecycle. Read by the health check.</summary>
public enum LiveSessionState
{
    /// <summary>Constructed; <c>StartSessionAsync</c> has not been called.</summary>
    NotStarted = 0,

    /// <summary>Connecting, authenticating, subscribing, or starting.</summary>
    Starting = 1,

    /// <summary>Started, and reading records.</summary>
    Running = 2,

    /// <summary>The connection dropped and the backoff is running.</summary>
    Reconnecting = 3,

    /// <summary>The stream ended or the session was cancelled. Not a failure.</summary>
    Stopped = 4,

    /// <summary>The session failed. <c>LiveSessionRunner.Fault</c> says how.</summary>
    Faulted = 5,
}
