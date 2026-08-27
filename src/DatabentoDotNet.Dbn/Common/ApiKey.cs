namespace DatabentoDotNet;

/// <summary>
/// A validated Databento API key: exactly 32 ASCII characters, whose last five are the bucket id
/// the gateway uses to look the key up.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>ApiKey</c> (<c>lib.rs:217-272</c>). A type rather than a
/// <see cref="string"/> parameter for two reasons: it moves the length and character checks to
/// the moment the key is supplied, rather than to the middle of a handshake, and it gives the key
/// a <see cref="ToString"/> that cannot leak it.
/// </para>
/// <para>
/// <b><see cref="ToString"/> is redacted, and that is load-bearing.</b> An API key reaches a log,
/// an exception message, or a crash dump through exactly one route: something formatted the
/// object that holds it. Upstream redacts its <c>Debug</c> impl for the same reason — though it
/// then interpolates the whole key into an <c>error!</c> line when the key is not ASCII
/// (<c>lib.rs:250</c>). That one is not ported: an invalid key is still a key, and it is very
/// often a valid key for a different account.
/// </para>
/// </remarks>
public sealed class ApiKey
{
    /// <summary>The exact length of a Databento API key, in ASCII characters.</summary>
    public const int Length = 32;

    /// <summary>The number of trailing characters of a key that form its bucket id.</summary>
    public const int BucketIdLength = 5;

    /// <summary>
    /// The placeholder that appears in Databento's own documentation and sample code. Rejected by
    /// name, because "expected 32 characters, got 13" would send a reader looking for a typo
    /// rather than for the line they forgot to fill in.
    /// </summary>
    public const string Placeholder = "$YOUR_API_KEY";

    private readonly string _value;

    /// <summary>Validates <paramref name="key"/> and wraps it.</summary>
    /// <param name="key">The API key.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The key is the documentation placeholder, is not exactly <see cref="Length"/> characters,
    /// or contains a non-ASCII character. The message never contains the key itself.
    /// </exception>
    public ApiKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (string.Equals(key, Placeholder, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The API key is still the documentation placeholder '{Placeholder}'. Pass a real key.",
                nameof(key));
        }

        if (key.Length != Length)
        {
            throw new ArgumentException(
                $"A Databento API key is exactly {Length} characters; this one is {key.Length}.",
                nameof(key));
        }

        if (!System.Text.Ascii.IsValid(key))
        {
            // Deliberately not naming the offending character or its position: both narrow down
            // the key itself, and this message may well end up in a log.
            throw new ArgumentException(
                "A Databento API key is composed of ASCII characters only; this one is not.",
                nameof(key));
        }

        _value = key;
    }

    /// <summary>
    /// The key itself. Only the CRAM handshake needs this — everything else should use
    /// <see cref="BucketId"/> or <see cref="ToString"/>.
    /// </summary>
    public string Value => _value;

    /// <summary>
    /// The last <see cref="BucketIdLength"/> characters of the key, which the gateway uses to
    /// find it. Safe to log: it identifies the key without being usable as one.
    /// </summary>
    public string BucketId => _value[^BucketIdLength..];

    /// <summary>
    /// The key with everything but its bucket id elided — <c>…iller</c>. Never the whole key.
    /// </summary>
    /// <returns>The redacted form.</returns>
    public override string ToString() => $"…{BucketId}";
}
