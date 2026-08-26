namespace DatabentoDotNet.Live.Tests;

/// <summary>
/// Raised by <see cref="MockLiveGateway"/> when a client speaks the live protocol wrongly, or
/// stops speaking it at all.
/// </summary>
/// <remarks>
/// <para>
/// Upstream's <c>MockGateway</c> uses bare <c>assert!</c>s. A dedicated exception is the .NET
/// equivalent that keeps this harness's own tests honest: the definition of done for
/// <see href="https://github.com/jerbersoft/databentodotnet/issues/18">#18</see> requires proving
/// that the gateway's assertions <em>fail</em> on a malformed auth line, and
/// <c>Assert.ThrowsAsync&lt;MockGatewayException&gt;</c> says that precisely, where
/// <c>ThrowsAnyAsync&lt;Exception&gt;</c> would also pass if the socket had simply died.
/// </para>
/// <para>
/// Every message carries the offending line. A protocol failure detected on a background task,
/// several layers from the test that caused it, is otherwise near-impossible to read.
/// </para>
/// </remarks>
public sealed class MockGatewayException : Exception
{
    /// <summary>Creates the exception with no message. Present for the standard exception shape.</summary>
    public MockGatewayException()
    {
    }

    /// <summary>Creates the exception with a message describing the protocol violation.</summary>
    /// <param name="message">The message.</param>
    public MockGatewayException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an underlying cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public MockGatewayException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
