using NodaTime;

namespace DatabentoDotNet.Live;

/// <summary>
/// No bytes arrived from the live gateway within <see cref="LiveClient.EffectiveReadTimeout"/>.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>Error::HeartbeatTimeout</c> (<c>live/client.rs</c>), and it keeps the
/// upstream name even though the budget it reports is
/// <see cref="LiveClient.ReadTimeout"/> — because the name is the explanation. <b>A silent socket
/// is only evidence of a problem because the gateway promises not to be silent.</b> It emits a
/// <c>SystemMsg</c> heartbeat whenever nothing else is due, so a gap longer than that interval
/// means the connection is dead rather than the market is quiet. Without that promise, thirty-five
/// seconds without a trade at 3am would be perfectly ordinary and no read timeout could be
/// justified at all. Calling this a <c>ReadTimeoutException</c> would name the mechanism and hide
/// the reason.
/// </para>
/// <para>
/// <b>The connection is spent when this is raised.</b> <see cref="LiveClient.IsClosed"/> is set
/// and the socket is torn down, matching upstream, which marks itself closed and requires a
/// reconnect rather than retrying. There is no way to abandon a pending socket read in .NET
/// without disposing the socket, so the timeout could not be non-destructive even if the protocol
/// allowed it.
/// </para>
/// </remarks>
public sealed class HeartbeatTimeoutException : LiveException
{
    /// <summary>Creates the exception with no message.</summary>
    public HeartbeatTimeoutException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public HeartbeatTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an underlying cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public HeartbeatTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for a budget that elapsed.</summary>
    /// <param name="timeout">The budget that elapsed.</param>
    /// <param name="waitingFor">
    /// What the client was waiting for, in words — "the session metadata", "the next record". It
    /// is the difference between a gateway that never started the session and one that started it
    /// and then went quiet, which is otherwise invisible from the message alone.
    /// </param>
    public HeartbeatTimeoutException(Duration timeout, string waitingFor)
        : base($"The live gateway sent nothing for {timeout} while the client was waiting for {waitingFor}. "
               + "The gateway emits a heartbeat when no other record is due, so a gap this long means the "
               + "connection is dead rather than the market quiet.")
        => Timeout = timeout;

    /// <summary>The read budget that elapsed.</summary>
    public Duration Timeout { get; }
}
