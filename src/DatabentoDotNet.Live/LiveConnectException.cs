using System.Net;

namespace DatabentoDotNet.Live;

/// <summary>
/// The client could not open a TCP connection to the gateway.
/// </summary>
/// <remarks>
/// <para>
/// Wraps whatever the socket raised — usually a <see cref="System.Net.Sockets.SocketException"/>
/// — so the endpoint that failed appears in the message. A bare
/// <c>SocketException: Connection refused</c> does not say <em>to what</em>, and the host was
/// derived from the dataset rather than supplied by the caller, so it is the one thing they
/// cannot work out for themselves.
/// </para>
/// <para>
/// <see cref="ConnectTimeoutException"/> derives from this, so catching this type catches both
/// ways a connection attempt can fail.
/// </para>
/// </remarks>
public class LiveConnectException : LiveException
{
    /// <summary>Creates the exception with no message.</summary>
    public LiveConnectException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public LiveConnectException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an underlying cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public LiveConnectException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for a failed attempt on <paramref name="endPoint"/>.</summary>
    /// <param name="endPoint">The gateway endpoint the client was connecting to.</param>
    /// <param name="innerException">What the socket raised.</param>
    public LiveConnectException(EndPoint endPoint, Exception innerException)
        : base($"Could not connect to the live gateway at {endPoint}.", innerException)
        => EndPoint = endPoint;

    /// <summary>
    /// Creates the exception with a message of the derived type's choosing, still recording the
    /// endpoint. <see cref="ConnectTimeoutException"/> uses this: its cause is an elapsed budget
    /// rather than an exception, so it has nothing to pass as an inner.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="endPoint">The gateway endpoint the client was connecting to.</param>
    protected LiveConnectException(string message, EndPoint endPoint)
        : base(message)
        => EndPoint = endPoint;

    /// <summary>The gateway endpoint the client was connecting to, when it is known.</summary>
    public EndPoint? EndPoint { get; }
}
