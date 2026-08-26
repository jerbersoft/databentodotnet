namespace DatabentoDotNet.Dbn;

/// <summary>
/// Thrown when a byte sequence is not valid DBN: bad magic, an unsupported version, a truncated
/// or self-inconsistent header, or a field whose raw value is not one the format defines.
/// </summary>
/// <remarks>
/// The counterpart of upstream's <c>Error::Decode</c> (and <c>Error::Conversion</c> for an
/// out-of-range enum discriminant, which this port does not separate — both mean the same thing
/// to a caller: these bytes are not decodable).
/// </remarks>
public class DbnDecodeException : DbnException
{
    /// <summary>Initializes a new instance of the <see cref="DbnDecodeException"/> class.</summary>
    public DbnDecodeException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="DbnDecodeException"/> class.</summary>
    /// <param name="message">A message describing the failure.</param>
    public DbnDecodeException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="DbnDecodeException"/> class.</summary>
    /// <param name="message">A message describing the failure.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public DbnDecodeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
