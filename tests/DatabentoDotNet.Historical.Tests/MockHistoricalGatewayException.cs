namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// Raised by <see cref="MockHistoricalGateway.ThrowIfRejected"/> when the gateway refused to
/// answer a request — because the client authenticated wrongly, carried the API key somewhere it
/// does not belong, or asked for a route no test registered.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <c>MockGatewayException</c> in the Live test project, and it exists for the
/// same reason: the definition of done for
/// <see href="https://github.com/jerbersoft/databentodotnet/issues/34">#34</see> requires proving
/// that the harness's own assertions <em>fail</em> on a malformed request, and
/// <c>Assert.Throws&lt;MockHistoricalGatewayException&gt;</c> says that precisely where
/// <c>Assert.ThrowsAny&lt;Exception&gt;</c> would also pass if the server had simply fallen over.
/// </para>
/// <para>
/// <b>Why it is thrown from a method rather than from the request handler.</b> The live gateway
/// throws in the middle of the exchange, because a test awaits it directly. This one runs inside
/// Kestrel: an exception on a request thread reaches the client as a 500 and the test as nothing
/// at all. So a refusal is answered on the wire — <c>401</c> for a credential the API would not
/// accept, <c>501</c> for a route nobody registered — and held for
/// <see cref="MockHistoricalGateway.ThrowIfRejected"/> to raise on the test's own thread, where
/// its message is attached to the test that caused it.
/// </para>
/// <para>
/// <b>No message this type carries ever contains the API key.</b> Nothing from the request is
/// interpolated into a refusal message except a query parameter name drawn from a fixed list the
/// harness owns — see <see cref="MockHistoricalGateway"/>.
/// </para>
/// </remarks>
public sealed class MockHistoricalGatewayException : Exception
{
    /// <summary>Creates the exception with no message. Present for the standard exception shape.</summary>
    public MockHistoricalGatewayException()
    {
    }

    /// <summary>Creates the exception with a message describing the refusal.</summary>
    /// <param name="message">The message.</param>
    public MockHistoricalGatewayException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an underlying cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public MockHistoricalGatewayException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
