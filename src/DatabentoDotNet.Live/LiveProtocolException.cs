namespace DatabentoDotNet.Live;

/// <summary>
/// The live gateway said something the protocol does not allow, or stopped speaking in the middle
/// of saying it.
/// </summary>
/// <remarks>
/// <para>
/// Covers the control-line half of the session: a challenge that does not begin with
/// <c>cram=</c>, a line that never terminates, and end-of-stream reached while a reply was still
/// outstanding. Upstream folds all of these into <c>Error::Internal</c> and
/// <c>Error::Io</c>.
/// </para>
/// <para>
/// <b>Deliberately not <see cref="DatabentoAuthenticationException"/>.</b> Both can end a
/// handshake, but they mean opposite things: one says the credentials were refused, this one says
/// the exchange never got far enough to judge them. Reporting a malformed challenge as a rejected
/// key would send a caller to rotate credentials that were never at fault.
/// </para>
/// </remarks>
public sealed class LiveProtocolException : LiveException
{
    /// <summary>Creates the exception with no message.</summary>
    public LiveProtocolException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public LiveProtocolException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an underlying cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public LiveProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
