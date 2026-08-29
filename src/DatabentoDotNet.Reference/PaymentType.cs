using System.Text.Json.Serialization;
using DatabentoDotNet.Reference.Json;

namespace DatabentoDotNet.Reference;

/// <summary>
/// The payment type.
/// </summary>
/// <remarks>
/// <para>
/// One of the nine closed reference enums: byte-backed, one character on the wire, and an
/// unrecognised code is an error rather than an opaque value. <see cref="ReferenceWireStrings"/>
/// carries the argument for all three.
/// </para>
/// <para>
/// <b>A blank is legal here, and it is not a member.</b> The <c>PAYTYPE</c> group lists a null-code
/// entry described as "A Blank value is possible", and upstream models the field as
/// <c>Option&lt;PaymentType&gt;</c> (<c>corporate.rs:358</c>). So the field a response model
/// declares is <see cref="Nullable{T}"/> of this type carrying
/// <see cref="Json.NullablePaymentTypeJsonConverter"/>, which reads a blank as
/// <see langword="null"/>. <see cref="Json.PaymentTypeJsonConverter"/>, the one this type carries
/// by attribute, rejects a blank — a non-nullable field has no way to say "no value". This and
/// <see cref="Fraction"/> are the only two of the nine that allow it.
/// </para>
/// <para>
/// Ported from <c>databento-rs/src/reference/enums.rs:3220</c>, and checked against the
/// <c>PAYTYPE</c> group of the vendored <c>corporate_actions.list_enums</c> response, which reports
/// the same five codes.
/// </para>
/// </remarks>
[JsonConverter(typeof(PaymentTypeJsonConverter))]
public enum PaymentType : byte
{
    /// <summary>Cash and stock (<c>B</c>).</summary>
    CashAndStock = (byte)'B',

    /// <summary>Cash (<c>C</c>).</summary>
    Cash = (byte)'C',

    /// <summary>Dissenters rights (<c>D</c>).</summary>
    DissentersRights = (byte)'D',

    /// <summary>Stock (<c>S</c>).</summary>
    Stock = (byte)'S',

    /// <summary>To be announced (<c>T</c>).</summary>
    Tba = (byte)'T',
}
