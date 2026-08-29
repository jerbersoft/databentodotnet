using System.Text.Json;

namespace DatabentoDotNet.Reference.Json;

/// <summary>
/// The one-character wire code the nine closed reference enums are carried as, read and written in
/// one place.
/// </summary>
/// <remarks>
/// <para>
/// Eleven converters — nine for the enums and two more for the pair whose fields may be blank —
/// share exactly one reading of the wire: a JSON string of length one, or nothing. Writing that
/// eleven times would give a misreading of it eleven places to hide, and this is the file
/// <c>PORTING.md</c> §2 points at for the difference between these enums and the codec's: on the
/// DBN wire a char enum <em>is</em> the raw ASCII byte, whereas here it arrives as a one-character
/// string and so has a length that can be wrong.
/// </para>
/// <para>
/// <b>Internal, unlike the converters themselves.</b> Nothing outside this assembly reads a wire
/// code without going through a converter, and an <c>internal</c> helper can be constrained,
/// renamed or deleted when #53–#57 land the response models. The converters are public because a
/// <c>[JsonConverter]</c> attribute on a public type names them.
/// </para>
/// <para>
/// <see cref="Utf8JsonReader.GetString"/> allocates a one-character string per field, and that is
/// accepted here rather than optimised away with <see cref="Utf8JsonReader.ValueSpan"/>. The
/// zero-allocation rule in <c>CLAUDE.md</c> is about the DBN record path, where a record is
/// reinterpreted in place over the read buffer; a reference response is JSON that allocates a
/// string for every field it has, and a span read would have to handle
/// <see cref="Utf8JsonReader.HasValueSequence"/> and escaping by hand to save one of them.
/// </para>
/// </remarks>
internal static class ReferenceEnumCode
{
    /// <summary>
    /// Reads the current token as a one-character wire code, or <see langword="null"/> for a blank.
    /// </summary>
    /// <param name="reader">The reader, positioned on the value.</param> <param name="name">The
    /// enum's name, for the message.</param>
    /// <returns>
    /// The code, or <see langword="null"/> when the token is JSON <c>null</c> or the empty string.
    /// Whether that blank is legal is the caller's to decide: it is a value for
    /// <see cref="Fraction"/> and <see cref="PaymentType"/> and an error for the other seven.
    /// </returns>
    /// <exception cref="JsonException">
    /// The token is not a string, or the string is not exactly one character long. A length other
    /// than one is as unrecognised as an unknown letter, and the message says what arrived.
    /// </exception>
    internal static char? Read(ref Utf8JsonReader reader, string name)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                $"{name} is a one-character string on the wire, and this one is a {reader.TokenType} token.");
        }

        var value = reader.GetString();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return value.Length == 1
            ? value[0]
            : throw new JsonException(
                $"{name} has no wire code '{value}': a {name} code is exactly one character on the " +
                $"wire and this one is {value.Length}.");
    }

    /// <summary>Writes a wire code as the one-character JSON string the API uses.</summary>
    /// <param name="writer">The writer.</param> <param name="code">The wire code.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is
    /// <see langword="null"/>.</exception>
    internal static void Write(Utf8JsonWriter writer, char code)
    {
        ArgumentNullException.ThrowIfNull(writer);

        // A stack-allocated inline array rather than code.ToString(): one character, no allocation.
        ReadOnlySpan<char> value = [code];
        writer.WriteStringValue(value);
    }

    /// <summary>The exception for a code outside an enum's alphabet.</summary>
    /// <param name="name">The enum's name.</param> <param name="code">The offending code, which the
    /// message carries.</param> <returns>The exception to throw.</returns>
    /// <remarks>
    /// <b>Throwing is the deliberate difference from the ten open code types.</b> Upstream returns
    /// an error for an unrecognised char (<c>enums.rs:44-55</c> and its eight siblings), and
    /// probing <c>corporate_actions.list_enums</c> is what makes keeping that safe: eight of these
    /// nine alphabets are exactly current against the live server, so a code outside one really
    /// does mean this library's table is stale rather than that the caller should be handed an
    /// opaque string. See <see cref="ReferenceWireStrings"/>.
    /// </remarks>
    internal static JsonException Unrecognised(string name, char code) => new(
        $"{name} has no wire code '{code}'. The nine char-coded reference enums are closed " +
        $"alphabets checked against the server's own dictionary, so an unrecognised code means " +
        $"this library's table is out of date.");

    /// <summary>The exception for a blank where the dictionary documents none.</summary>
    /// <param name="name">The enum's name.</param> <param name="group">The <c>list_enums</c> group
    /// that documents the alphabet.</param> <returns>The exception to throw.</returns>
    internal static JsonException Blank(string name, string group) => new(
        $"{name} has no blank value. The {group} group of corporate_actions.list_enums lists no " +
        $"blank entry, so an absent value here is a malformed response rather than 'no value'.");
}
