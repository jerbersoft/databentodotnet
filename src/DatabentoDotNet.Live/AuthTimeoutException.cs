using NodaTime;

namespace DatabentoDotNet.Live;

/// <summary>
/// The CRAM handshake did not complete within <see cref="LiveClient.AuthTimeout"/>.
/// </summary>
/// <remarks>
/// <para>
/// Upstream has no equivalent: its <c>authenticate</c> reads the greeting, the challenge, and the
/// response with no budget at all, so a gateway that accepts the connection and then says nothing
/// hangs the caller until the OS gives up on the socket. That is the one failure mode a live
/// client cannot afford to inherit — it is indistinguishable, from the caller's side, from a
/// market with nothing to report.
/// </para>
/// <para>
/// <b>The budget covers the whole exchange, not each read.</b> A gateway that sends the greeting
/// and then stalls has spent the client's time just as surely as one that never speaks, and a
/// per-read budget would let it stall indefinitely one line at a time.
/// </para>
/// <para>
/// <b>The socket is torn down before this is raised.</b> Authentication is not cancel-safe
/// (PORTING.md §4): the only way to abandon it is to close the connection, so a client that
/// catches this is disconnected and must reconnect rather than retry the handshake.
/// </para>
/// </remarks>
public sealed class AuthTimeoutException : LiveException
{
    /// <summary>Creates the exception with no message.</summary>
    public AuthTimeoutException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public AuthTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an underlying cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public AuthTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for a budget that elapsed.</summary>
    /// <param name="timeout">The budget that elapsed.</param>
    public AuthTimeoutException(Duration timeout)
        : base($"The live gateway handshake did not complete within {timeout}.")
        => Timeout = timeout;

    /// <summary>The handshake budget that elapsed.</summary>
    public Duration Timeout { get; }
}
