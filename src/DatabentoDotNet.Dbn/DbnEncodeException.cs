namespace DatabentoDotNet.Dbn;

/// <summary>
/// Thrown when a value cannot be represented in DBN: a symbol that is not ASCII or does not fit
/// the stream's fixed symbol width, or metadata carrying a version this library will not emit.
/// </summary>
/// <remarks>
/// The counterpart of upstream's <c>Error::Encode</c> and the <c>Error::Conversion</c> raised by
/// <c>encode_fixed_len_cstr</c>. Note that upstream truncates nothing: an over-long symbol is a
/// hard error there and here, because silently shortening a symbol would corrupt the symbology
/// mapping rather than surface a bug.
/// </remarks>
public class DbnEncodeException : DbnException
{
    /// <summary>Initializes a new instance of the <see cref="DbnEncodeException"/> class.</summary>
    public DbnEncodeException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="DbnEncodeException"/> class.</summary>
    /// <param name="message">A message describing the failure.</param>
    public DbnEncodeException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="DbnEncodeException"/> class.</summary>
    /// <param name="message">A message describing the failure.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public DbnEncodeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
