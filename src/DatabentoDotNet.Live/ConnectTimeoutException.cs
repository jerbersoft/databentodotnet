using System.Net;
using NodaTime;

namespace DatabentoDotNet.Live;

/// <summary>
/// The connection to the gateway did not complete within
/// <see cref="LiveClient.ConnectTimeout"/>.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>Error::ConnectTimeout(Duration)</c> (<c>error.rs:51</c>), which likewise
/// carries the budget that elapsed rather than only saying that one did.
/// </para>
/// <para>
/// <b>This is not what a closed port produces.</b> A refused connection comes back immediately as
/// a <see cref="LiveConnectException"/> wrapping the socket error; this type means the attempt was
/// still outstanding when the budget ran out, which is what a firewalled or black-holed address
/// looks like. Both derive from <see cref="LiveConnectException"/>.
/// </para>
/// </remarks>
public sealed class ConnectTimeoutException : LiveConnectException
{
    /// <summary>Creates the exception with no message.</summary>
    public ConnectTimeoutException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public ConnectTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an underlying cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public ConnectTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for a budget that elapsed.</summary>
    /// <param name="timeout">The budget that elapsed.</param>
    /// <param name="endPoint">The gateway endpoint the client was connecting to.</param>
    public ConnectTimeoutException(Duration timeout, EndPoint endPoint)
        : base($"Connecting to the live gateway at {endPoint} did not complete within {timeout}.", endPoint)
        => Timeout = timeout;

    /// <summary>The connect budget that elapsed.</summary>
    public Duration Timeout { get; }
}
