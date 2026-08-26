namespace DatabentoDotNet.Dbn;

/*
 * Fixed C-string fields.
 *
 * Upstream declares every text field inside a record as `[c_char; N]`: a fixed-width block of
 * ASCII padded to N bytes with NULs. The block lives inside the record's own wire bytes, so it
 * is reinterpreted in place along with the rest of the record.
 *
 * Each width gets its own `[InlineArray]` type (CStr4 ... CStr303) because an inline array's
 * length is an attribute argument and so must be a compile-time constant — C# has no const
 * generic parameter to express `CStr<N>`. The types are otherwise identical, and every one of
 * them delegates its two decoding decisions to this file so there is exactly one place where
 * "where does the text end" and "how are the bytes turned into chars" are decided.
 *
 * Decoding is deliberately lazy. Turning a symbol into a `string` allocates, and a decoder that
 * did it per record would allocate per record — which is precisely what G4 (zero-copy) exists to
 * prevent. So `AsSpan()`/`AsTextSpan()` are allocation-free views over the record's own buffer,
 * and only `ToString()` allocates, and only when a caller asks for it.
 */

/// <summary>
/// The decoding rules shared by every fixed C-string field type.
/// </summary>
internal static class CStr
{
    /// <summary>
    /// The text portion of a fixed C-string field: everything before the first NUL, or the whole
    /// field when it contains no NUL at all.
    /// </summary>
    /// <remarks>
    /// The no-NUL case is real, not defensive: a symbol that fills all N bytes leaves no room for
    /// a terminator. Upstream Rust's <c>c_chars_to_str</c> rejects that case
    /// (<c>CStr::from_bytes_until_nul</c> requires a NUL); this port returns the full field
    /// instead, which is what <c>databento-cpp</c>'s <c>.data()</c> accessors assume and what a
    /// reader actually wants. The divergence only ever adds characters that are genuinely on the
    /// wire.
    /// </remarks>
    /// <param name="field">All of the field's wire bytes, NUL padding included.</param>
    /// <returns>The bytes before the first NUL.</returns>
    internal static ReadOnlySpan<byte> Text(ReadOnlySpan<byte> field)
    {
        var nul = field.IndexOf((byte)0);
        return nul < 0 ? field : field[..nul];
    }

    /// <summary>
    /// Decodes the text portion of a fixed C-string field. This allocates a
    /// <see cref="string"/>; it is never called on the decode path.
    /// </summary>
    /// <remarks>
    /// The fields are documented as ASCII, and UTF-8 decodes ASCII identically. Malformed bytes
    /// become the Unicode replacement character rather than throwing, because this backs
    /// <see cref="object.ToString"/>, which must not throw. Callers that need to distinguish
    /// "not text" from "text" should compare <see cref="CStr71.AsTextSpan"/> against bytes
    /// instead.
    /// </remarks>
    /// <param name="field">All of the field's wire bytes, NUL padding included.</param>
    /// <returns>The decoded text, without trailing NUL padding.</returns>
    internal static string ToText(ReadOnlySpan<byte> field)
        => System.Text.Encoding.UTF8.GetString(Text(field));
}
