namespace DatabentoDotNet.Dbn;

/// <summary>
/// Base class for every DBN-specific failure this library raises.
/// </summary>
/// <remarks>
/// <para>
/// Upstream Rust returns <c>Result&lt;T, dbn::Error&gt;</c> from every fallible operation and
/// tells the failure kinds apart by enum variant. This port follows the project's error rule
/// instead: outcomes a caller should expect and branch on use a <c>Try*</c> method, and genuinely
/// exceptional outcomes throw. Malformed wire data is exceptional — a DBN stream that does not
/// parse is not a value, it is a broken file — so it throws.
/// </para>
/// <para>
/// Every such throw is rooted here, so one <c>catch (DbnException)</c> covers the whole codec
/// without also swallowing unrelated <see cref="ArgumentException"/>s from caller mistakes.
/// </para>
/// </remarks>
public class DbnException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="DbnException"/> class.</summary>
    public DbnException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="DbnException"/> class.</summary>
    /// <param name="message">A message describing the failure.</param>
    public DbnException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="DbnException"/> class.</summary>
    /// <param name="message">A message describing the failure.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public DbnException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
