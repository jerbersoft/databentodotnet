namespace DatabentoDotNet.Live;

/// <summary>
/// Base class for every error the live client raises for itself, as opposed to the ones it lets
/// through from the codec (<see cref="DatabentoDotNet.Dbn.DbnException"/>) or from the socket.
/// </summary>
/// <remarks>
/// Port of the live half of upstream's <c>crate::Error</c> (<c>error.rs</c>), which is one enum
/// spanning live, historical, and reference. A .NET exception hierarchy splits it by module
/// instead, so a caller can catch the live client's failures without also catching an HTTP error
/// from a historical query it never made.
/// </remarks>
public class LiveException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public LiveException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public LiveException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an underlying cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public LiveException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
