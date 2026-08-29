using System.Text.Json.Serialization;
using DatabentoDotNet.Reference.Json;

namespace DatabentoDotNet.Reference;

/// <summary>
/// Listing status.
/// </summary>
/// <remarks>
/// <para>
/// One of the nine closed reference enums: byte-backed, one character on the wire, and an
/// unrecognised code is an error rather than an opaque value. <see cref="ReferenceWireStrings"/>
/// carries the argument for all three.
/// </para>
/// <para>
/// The twelve codes are four states — listed, delisted, suspended and the three that stand alone —
/// crossed with which regime they hold under: plain, <em>reporting purposes only</em>, or
/// <em>trading permitted</em>. Nothing in the alphabet says so, which is why each member spells it
/// out rather than leaving <c>G</c>, <c>H</c> and <c>I</c> looking arbitrary.
/// </para>
/// <para>
/// Ported from <c>databento-rs/src/reference/enums.rs:3019</c>, and checked against the
/// <c>LISTSTAT</c> group of the vendored <c>corporate_actions.list_enums</c> response, which
/// reports the same twelve codes. A <c>security_master</c> response field.
/// </para>
/// </remarks>
[JsonConverter(typeof(ListingStatusJsonConverter))]
public enum ListingStatus : byte
{
    /// <summary>Delisted (<c>D</c>).</summary>
    Delisted = (byte)'D',

    /// <summary>Reporting purposes only — listed (<c>G</c>).</summary>
    RpoListed = (byte)'G',

    /// <summary>Reporting purposes only — delisted (<c>H</c>).</summary>
    RpoDelisted = (byte)'H',

    /// <summary>Reporting purposes only — suspended (<c>I</c>).</summary>
    RpoSuspended = (byte)'I',

    /// <summary>Listed (<c>L</c>).</summary>
    Listed = (byte)'L',

    /// <summary>New listing (<c>N</c>).</summary>
    New = (byte)'N',

    /// <summary>Listing pending (<c>P</c>).</summary>
    Pending = (byte)'P',

    /// <summary>Resumed (<c>R</c>).</summary>
    Resumed = (byte)'R',

    /// <summary>Suspended (<c>S</c>).</summary>
    Suspended = (byte)'S',

    /// <summary>Trading permitted — listed (<c>T</c>).</summary>
    TpListed = (byte)'T',

    /// <summary>Trading permitted — delisted (<c>U</c>).</summary>
    TpDelisted = (byte)'U',

    /// <summary>Trading permitted — suspended (<c>V</c>).</summary>
    TpSuspended = (byte)'V',
}
