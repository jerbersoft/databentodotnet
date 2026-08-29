namespace DatabentoDotNet.Reference;

/// <summary>
/// Wire-code conversions for the nine closed reference enums, and the argument for why they are
/// <c>enum</c>s at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>These nine are closed, and the ten types behind <see cref="IReferenceCode{TSelf}"/> are
/// not.</b> The line is wire alphabet against data dictionary, not <c>#[repr(u8)]</c> against
/// <c>String</c>. A single-byte alphabet is closed because a new value in it is a wire-format
/// change; a code that comes out of Databento's growing corporate-actions dictionary is not.
/// Probing <c>corporate_actions.list_enums</c> before any of this was written found eight of these
/// nine exactly current against the live server, and found upstream already <em>behind</em> it on
/// three of the sets it models as closed — which is what moved those three to the open carrier.
/// ROADMAP.md §6 records the probe.
/// </para>
/// <para>
/// <b>Each enum keeps upstream's byte backing:</b> <c>public enum T : byte</c> with <c>Member =
/// (byte)'C'</c>, exactly as <see cref="DatabentoDotNet.Dbn.Action"/> is declared. Three reasons,
/// and none of them is habit.
/// </para>
/// <list type="number">
/// <item><description>
/// It is upstream's own representation — <c>#[repr(u8)]</c> with <c>Cancelled = b'C'</c> — and the
/// porting rules make the Rust source authoritative for wire format.
/// </description></item>
/// <item><description>
/// It makes <c>default(T)</c> an <b>undefined</b> value. Byte 0 is not a code any of these
/// alphabets uses, so a response field that was never set cannot pass for a real member. A plain
/// 0-based enum would read a missing field as its first member — <c>Fraction.Cash</c>,
/// <c>PaymentType.CashAndStock</c> — which is the silent kind of wrong this library exists to
/// prevent.
/// </description></item>
/// <item><description>
/// The codec already models char-coded enums this way, so a reader who knows
/// <see cref="DatabentoDotNet.Dbn.Action"/> already knows these.
/// </description></item>
/// </list>
/// <para>
/// <b>What differs from the codec's char enums is the wire, not the type.</b> On the DBN wire a
/// <see cref="DatabentoDotNet.Dbn.Side"/> <em>is</em> the raw ASCII byte, and that byte is its only
/// representation — there is no text form to parse. Here the byte never appears: the reference API
/// is JSON, and it carries a one-character <em>string</em> (<c>"C"</c>). So these enums do have a
/// text form, it is exactly one character long, and a string of any other length is as unrecognised
/// as an unknown letter. The <c>Json/</c> converters are where that is enforced.
/// </para>
/// <para>
/// <b>The contract of the two conversions, which is the codec's contract restated.</b>
/// <c>ToChar</c> is <c>(char)value</c> and <b>does not validate</b>: hand it a value cast from an
/// undefined byte and you get that byte back as a character, exactly as
/// <c>DatabentoDotNet.Dbn.WireStrings.ToChar</c> does. It is not the guard, and the round-trip
/// identity the tests assert — <c>code → enum → code</c> — is a claim about the defined members,
/// which is all it can be. <c>TryParse{Enum}</c> is the guard: it answers <see langword="false"/>
/// for anything the alphabet does not contain and never throws.
/// </para>
/// <para>
/// One <c>TryParse</c> method per enum rather than an overload set distinguished only by the
/// <see langword="out"/> parameter's type, for the reason
/// <c>DatabentoDotNet.Historical.MetadataWireStrings</c> gives: an overload set of that shape makes
/// the ordinary <c>out var</c> call form ambiguous and fails to compile.
/// </para>
/// <para>
/// There is deliberately no <c>ToWireString</c> here. A one-character string is what the converters
/// write, and they write it from the <see cref="char"/>; a second surface that allocates a
/// one-character <see cref="string"/> would have no caller.
/// </para>
/// </remarks>
public static class ReferenceWireStrings
{
    // ------------------------------------------------------------------------------ Action

    /// <summary>Returns the ASCII character this <see cref="Action"/> is defined as.</summary>
    /// <param name="value">The value.</param> <returns>The wire code. Not validated — see the
    /// type's remarks.</returns>
    public static char ToChar(this Action value) => (char)value;

    /// <summary>Parses an <see cref="Action"/> wire code.</summary> <param name="value">The
    /// one-character wire code.</param> <param name="result">The parsed value, or
    /// <see langword="default"/>.</param> <returns><see langword="true"/> if
    /// <paramref name="value"/> named a defined action.</returns>
    public static bool TryParseAction(char value, out Action result)
    {
        switch (value)
        {
            case 'C': result = Action.Cancelled; return true;
            case 'D': result = Action.Deleted; return true;
            case 'I': result = Action.Inserted; return true;
            case 'P': result = Action.PaymentDetailsCancelledByIssuer; return true;
            case 'Q': result = Action.PaymentDetailsDeletedBySupplier; return true;
            case 'U': result = Action.Updated; return true;
            default: result = default; return false;
        }
    }

    // -------------------------------------------------------------------- AdjustmentStatus

    /// <summary>Returns the ASCII character this <see cref="AdjustmentStatus"/> is defined
    /// as.</summary> <param name="value">The value.</param> <returns>The wire code. Not validated —
    /// see the type's remarks.</returns>
    public static char ToChar(this AdjustmentStatus value) => (char)value;

    /// <summary>Parses an <see cref="AdjustmentStatus"/> wire code.</summary>
    /// <param name="value">The one-character wire code.</param> <param name="result">The parsed
    /// value, or <see langword="default"/>.</param> <returns><see langword="true"/> if
    /// <paramref name="value"/> named a defined status.</returns>
    public static bool TryParseAdjustmentStatus(char value, out AdjustmentStatus result)
    {
        switch (value)
        {
            case 'A': result = AdjustmentStatus.Apply; return true;
            case 'R': result = AdjustmentStatus.Rescind; return true;
            case 'P': result = AdjustmentStatus.Pending; return true;
            default: result = default; return false;
        }
    }

    // ---------------------------------------------------------------------------- Fraction

    /// <summary>Returns the ASCII character this <see cref="Fraction"/> is defined as.</summary>
    /// <param name="value">The value.</param> <returns>The wire code. Not validated — see the
    /// type's remarks.</returns>
    public static char ToChar(this Fraction value) => (char)value;

    /// <summary>Parses a <see cref="Fraction"/> wire code.</summary> <param name="value">The
    /// one-character wire code.</param> <param name="result">The parsed value, or
    /// <see langword="default"/>.</param> <returns><see langword="true"/> if
    /// <paramref name="value"/> named a defined handling.</returns>
    public static bool TryParseFraction(char value, out Fraction result)
    {
        switch (value)
        {
            case 'C': result = Fraction.Cash; return true;
            case 'D': result = Fraction.RoundDown; return true;
            case 'F': result = Fraction.Fractions; return true;
            case 'U': result = Fraction.RoundUp; return true;
            default: result = default; return false;
        }
    }

    // ------------------------------------------------------------------------ GlobalStatus

    /// <summary>Returns the ASCII character this <see cref="GlobalStatus"/> is defined
    /// as.</summary> <param name="value">The value.</param> <returns>The wire code. Not validated —
    /// see the type's remarks.</returns>
    public static char ToChar(this GlobalStatus value) => (char)value;

    /// <summary>Parses a <see cref="GlobalStatus"/> wire code.</summary> <param name="value">The
    /// one-character wire code.</param> <param name="result">The parsed value, or
    /// <see langword="default"/>.</param> <returns><see langword="true"/> if
    /// <paramref name="value"/> named a defined status.</returns>
    public static bool TryParseGlobalStatus(char value, out GlobalStatus result)
    {
        switch (value)
        {
            case 'A': result = GlobalStatus.Active; return true;
            case 'D': result = GlobalStatus.InDefault; return true;
            case 'I': result = GlobalStatus.Inactive; return true;
            default: result = default; return false;
        }
    }

    // ----------------------------------------------------------------------- ListingSource

    /// <summary>Returns the ASCII character this <see cref="ListingSource"/> is defined
    /// as.</summary> <param name="value">The value.</param> <returns>The wire code. Not validated —
    /// see the type's remarks.</returns>
    public static char ToChar(this ListingSource value) => (char)value;

    /// <summary>Parses a <see cref="ListingSource"/> wire code.</summary> <param name="value">The
    /// one-character wire code.</param> <param name="result">The parsed value, or
    /// <see langword="default"/>.</param> <returns><see langword="true"/> if
    /// <paramref name="value"/> named a defined source.</returns>
    public static bool TryParseListingSource(char value, out ListingSource result)
    {
        switch (value)
        {
            case 'M': result = ListingSource.Main; return true;
            case 'S': result = ListingSource.Secondary; return true;
            default: result = default; return false;
        }
    }

    // ----------------------------------------------------------------------- ListingStatus

    /// <summary>Returns the ASCII character this <see cref="ListingStatus"/> is defined
    /// as.</summary> <param name="value">The value.</param> <returns>The wire code. Not validated —
    /// see the type's remarks.</returns>
    public static char ToChar(this ListingStatus value) => (char)value;

    /// <summary>Parses a <see cref="ListingStatus"/> wire code.</summary> <param name="value">The
    /// one-character wire code.</param> <param name="result">The parsed value, or
    /// <see langword="default"/>.</param> <returns><see langword="true"/> if
    /// <paramref name="value"/> named a defined status.</returns>
    public static bool TryParseListingStatus(char value, out ListingStatus result)
    {
        switch (value)
        {
            case 'D': result = ListingStatus.Delisted; return true;
            case 'G': result = ListingStatus.RpoListed; return true;
            case 'H': result = ListingStatus.RpoDelisted; return true;
            case 'I': result = ListingStatus.RpoSuspended; return true;
            case 'L': result = ListingStatus.Listed; return true;
            case 'N': result = ListingStatus.New; return true;
            case 'P': result = ListingStatus.Pending; return true;
            case 'R': result = ListingStatus.Resumed; return true;
            case 'S': result = ListingStatus.Suspended; return true;
            case 'T': result = ListingStatus.TpListed; return true;
            case 'U': result = ListingStatus.TpDelisted; return true;
            case 'V': result = ListingStatus.TpSuspended; return true;
            default: result = default; return false;
        }
    }

    // ---------------------------------------------------------------------------- MandVolu

    /// <summary>Returns the ASCII character this <see cref="MandVolu"/> is defined as.</summary>
    /// <param name="value">The value.</param> <returns>The wire code. Not validated — see the
    /// type's remarks.</returns>
    public static char ToChar(this MandVolu value) => (char)value;

    /// <summary>Parses a <see cref="MandVolu"/> wire code.</summary> <param name="value">The
    /// one-character wire code.</param> <param name="result">The parsed value, or
    /// <see langword="default"/>.</param> <returns><see langword="true"/> if
    /// <paramref name="value"/> named a defined value.</returns>
    public static bool TryParseMandVolu(char value, out MandVolu result)
    {
        switch (value)
        {
            case 'M': result = MandVolu.Mandatory; return true;
            case 'V': result = MandVolu.Voluntary; return true;
            case 'W': result = MandVolu.MandVolu; return true;
            default: result = default; return false;
        }
    }

    // ------------------------------------------------------------------------- PaymentType

    /// <summary>Returns the ASCII character this <see cref="PaymentType"/> is defined as.</summary>
    /// <param name="value">The value.</param> <returns>The wire code. Not validated — see the
    /// type's remarks.</returns>
    public static char ToChar(this PaymentType value) => (char)value;

    /// <summary>Parses a <see cref="PaymentType"/> wire code.</summary> <param name="value">The
    /// one-character wire code.</param> <param name="result">The parsed value, or
    /// <see langword="default"/>.</param> <returns><see langword="true"/> if
    /// <paramref name="value"/> named a defined type.</returns>
    public static bool TryParsePaymentType(char value, out PaymentType result)
    {
        switch (value)
        {
            case 'B': result = PaymentType.CashAndStock; return true;
            case 'C': result = PaymentType.Cash; return true;
            case 'D': result = PaymentType.DissentersRights; return true;
            case 'S': result = PaymentType.Stock; return true;
            case 'T': result = PaymentType.Tba; return true;
            default: result = default; return false;
        }
    }

    // ------------------------------------------------------------------------------ Voting

    /// <summary>Returns the ASCII character this <see cref="Voting"/> is defined as.</summary>
    /// <param name="value">The value.</param> <returns>The wire code. Not validated — see the
    /// type's remarks.</returns>
    public static char ToChar(this Voting value) => (char)value;

    /// <summary>Parses a <see cref="Voting"/> wire code.</summary> <param name="value">The
    /// one-character wire code.</param> <param name="result">The parsed value, or
    /// <see langword="default"/>.</param> <returns><see langword="true"/> if
    /// <paramref name="value"/> named a defined type.</returns>
    public static bool TryParseVoting(char value, out Voting result)
    {
        switch (value)
        {
            case 'L': result = Voting.Limited; return true;
            case 'M': result = Voting.Multiple; return true;
            case 'N': result = Voting.No; return true;
            case 'V': result = Voting.Voting; return true;
            default: result = default; return false;
        }
    }
}
