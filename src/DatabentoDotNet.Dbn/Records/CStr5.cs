using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// A fixed 5-byte NUL-padded C-string field: <c>exchange</c> in an instrument definition.
/// </summary>
/// <remarks>
/// The buffer is part of the record's own wire bytes and is never decoded to a
/// <see cref="string"/> as part of decoding. <see cref="AsSpan"/> and <see cref="AsTextSpan"/>
/// allocate nothing; only <see cref="ToString"/> does. See <c>CStr.cs</c> for why each width is
/// its own type.
/// </remarks>
[InlineArray(5)]
public struct CStr5
{
    /// <summary>The field's fixed width on the wire, in bytes.</summary>
    public const int Length = 5;

    private byte _element0;

    /// <summary>
    /// All <see cref="Length"/> wire bytes, NUL padding included. The span points into the
    /// record, so it is valid only for as long as the record's own buffer is.
    /// </summary>
    /// <returns>The field's wire bytes.</returns>
    [UnscopedRef]
    public readonly ReadOnlySpan<byte> AsSpan() => this;

    /// <summary>
    /// The bytes before the first NUL, or all <see cref="Length"/> of them when the text fills
    /// the field and leaves no room for a terminator. Allocates nothing.
    /// </summary>
    /// <returns>The field's text bytes, without NUL padding.</returns>
    [UnscopedRef]
    public readonly ReadOnlySpan<byte> AsTextSpan() => CStr.Text(this);

    /// <summary>
    /// Decodes the field to a string, without trailing NUL padding. <strong>This allocates</strong>
    /// — prefer <see cref="AsTextSpan"/> on the decode path.
    /// </summary>
    /// <returns>The decoded text.</returns>
    public override readonly string ToString() => CStr.ToText(this);
}
