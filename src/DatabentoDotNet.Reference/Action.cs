using System.Text.Json.Serialization;
using DatabentoDotNet.Reference.Json;

namespace DatabentoDotNet.Reference;

/// <summary>
/// A corporate actions action.
/// </summary>
/// <remarks>
/// <para>
/// One of the nine closed reference enums: byte-backed, one character on the wire, and an
/// unrecognised code is an error rather than an opaque value. <see cref="ReferenceWireStrings"/>
/// carries the argument for all three of those, and for why these are <c>enum</c>s where the ten
/// types behind <see cref="IReferenceCode{TSelf}"/> are not.
/// </para>
/// <para>
/// Ported from <c>databento-rs/src/reference/enums.rs:14</c>, and checked against the <c>ACTION</c>
/// group of the vendored <c>corporate_actions.list_enums</c> response, which reports the same six
/// codes.
/// </para>
/// </remarks>
[JsonConverter(typeof(ActionJsonConverter))]
public enum Action : byte
{
    /// <summary>Cancelled (<c>C</c>).</summary>
    Cancelled = (byte)'C',

    /// <summary>Deleted (<c>D</c>).</summary>
    Deleted = (byte)'D',

    /// <summary>Inserted (<c>I</c>).</summary>
    Inserted = (byte)'I',

    /// <summary>Payment details cancelled by issuer (<c>P</c>).</summary>
    PaymentDetailsCancelledByIssuer = (byte)'P',

    /// <summary>Payment details deleted by supplier (<c>Q</c>).</summary>
    PaymentDetailsDeletedBySupplier = (byte)'Q',

    /// <summary>Updated (<c>U</c>).</summary>
    Updated = (byte)'U',
}
