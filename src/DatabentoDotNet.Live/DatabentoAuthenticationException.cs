namespace DatabentoDotNet.Live;

/// <summary>
/// The live gateway rejected the client's credentials: its authentication response did not carry
/// <c>success=1</c>.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>Error::Auth(String)</c> (<c>error.rs</c>), raised from
/// <c>AuthResponse::parse</c> when the response is missing <c>success</c> or carries anything but
/// <c>1</c>. Upstream's message is the <c>error=</c> field when there is one and the whole
/// response line when there is not; both are kept here, but as separate properties rather than
/// collapsed into one string, so a caller can branch on <see cref="Error"/> without parsing a
/// message.
/// </para>
/// <para>
/// <b>This means the credentials were refused, not that the exchange went wrong.</b> A gateway
/// that answers with something that is not an authentication response at all raises
/// <see cref="LiveProtocolException"/> instead. Telling a caller their API key was rejected when
/// the real fault was a malformed challenge would send them to rotate a key that was never the
/// problem.
/// </para>
/// <para>
/// <b>The API key is never in the message.</b> Only <see cref="ApiKey.ToString"/>'s redacted form
/// appears — enough to say <em>which</em> key a process with several was refused on, and not
/// enough to be one.
/// </para>
/// </remarks>
public sealed class DatabentoAuthenticationException : LiveException
{
    /// <summary>Creates the exception with no message.</summary>
    public DatabentoAuthenticationException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message. Must not contain the API key.</param>
    public DatabentoAuthenticationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an underlying cause.</summary>
    /// <param name="message">The message. Must not contain the API key.</param>
    /// <param name="innerException">The underlying cause.</param>
    public DatabentoAuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// The gateway's <c>error=</c> text, or <see langword="null"/> when it rejected the request
    /// without giving a reason.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// The gateway's authentication response, verbatim and without its terminator.
    /// </summary>
    /// <remarks>
    /// <b>The gateway echoes the client's <c>auth=</c> field back on a malformed reply</b>, so
    /// this is not simply "text the gateway wrote". Asking the real gateway for a reply whose
    /// bucket suffix was sliced from the wrong end of the key came back as
    /// <c>CRAM reply string malformed, was '&lt;digest&gt;-&lt;the bucket that was sent&gt;'</c>.
    /// In correct operation what travels in that field is the digest and the bucket id, and the
    /// bucket id is safe to log by design — but the safety comes from what the client puts on the
    /// wire, not from the gateway declining to repeat it. Anything that ever widens the auth
    /// field has to be weighed against this property carrying it straight into a log.
    /// </remarks>
    public string? Response { get; init; }
}
