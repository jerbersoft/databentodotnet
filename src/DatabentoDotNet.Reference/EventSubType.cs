using System.Collections.Frozen;
using System.Text.Json.Serialization;
using DatabentoDotNet.Reference.Json;

namespace DatabentoDotNet.Reference;

/// <summary>
/// A corporate-action event sub-type.
/// </summary>
/// <remarks>
/// <para>
/// <b>An open set: a code this library does not know is carried, not lost.</b> Upstream ends
/// this enum in an <c>Unknown(String)</c> variant (<c>enums.rs:2365</c>) so a code Databento adds
/// next month round-trips untouched, and a C# <c>enum</c> cannot hold a payload. See
/// <see cref="IReferenceCode{TSelf}"/> for the shape this takes instead and why.
/// </para>
/// <para>
/// The members come from the <c>EVENTSUBTYPE</c> group of the vendored <c>corporate_actions.list_enums</c> response, which is the oracle rather than a count typed into an issue.
/// </para>
/// <para>
/// The dictionary carries 80 entries for 67 distinct codes: six codes appear more than once with a description that depends on the parent event, and seven entries carry no code at all. The members here are deduplicated by code, and a member whose code has more than one description names all of them — the description belongs to the event, not to the sub-type.
/// </para>
/// </remarks>
[JsonConverter(typeof(ReferenceCodeJsonConverter<EventSubType>))]
public readonly record struct EventSubType : IReferenceCode<EventSubType>
{
    private static readonly FrozenSet<string> Codes = FrozenSet.ToFrozenSet(
    [
        "AGM",
        "AMT",
        "BB",
        "BBED",
        "BBRD",
        "BHM",
        "BON",
        "CALL",
        "CAPDIST",
        "CAPGAIN",
        "CAPRD",
        "CGM",
        "CLAIMSET",
        "CONSD",
        "CORR",
        "CU",
        "DEFPY",
        "DIST",
        "DIV",
        "DIVACC",
        "DIVINC",
        "DMRGR",
        "DPRCPDIV",
        "DR",
        "DRL",
        "DT",
        "DUTCHAUCT",
        "ECONV",
        "EGM",
        "ER",
        "F",
        "GM",
        "INT",
        "INTACC",
        "INTDIV",
        "INTINC",
        "LIQ",
        "MAT",
        "MRGR",
        "MWC",
        "N",
        "NRENRTS",
        "OPOFF",
        "ORD",
        "P",
        "PF",
        "POFF",
        "PRO",
        "PROACC",
        "PROINC",
        "PUT",
        "RCAP",
        "REDEMCLAIM",
        "RES",
        "RM",
        "ROD",
        "SD",
        "SGM",
        "SOA",
        "SPA",
        "SPP",
        "TEND",
        "TENDMRGR",
        "TKOVRMINI",
        "U",
        "UKWNSUBTYP",
        "WRTDN",
    ], StringComparer.Ordinal);

    private readonly string? _code;

    /// <summary>
    /// Wraps a wire code, known or not. Prefer a named member such as
    /// <see cref="Agm"/> where one exists, and <see cref="From"/> where the value came
    /// from the server.
    /// </summary>
    /// <param name="code">The wire code.</param>
    /// <exception cref="ArgumentException"><paramref name="code"/> is null or empty. A blank code is the absence of a value, which is <see langword="default"/>.</exception>
    public EventSubType(string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        _code = code;
    }

    /// <summary>
    /// Every code the reference API reported for this type when the fixture was captured —
    /// 67 of them.
    /// </summary>
    public static IReadOnlySet<string> KnownCodes => Codes;

    /// <inheritdoc/>
    public string? Code => _code;

    /// <inheritdoc/>
    public bool HasValue => _code is not null;

    /// <inheritdoc/>
    public bool IsKnown => _code is not null && Codes.Contains(_code);

    /// <summary>
    /// Reads a wire code, mapping <see langword="null"/> and the empty string to
    /// <see langword="default"/> — the absence of a value.
    /// </summary>
    /// <param name="code">The wire code, or <see langword="null"/>.</param>
    /// <returns>The value.</returns>
    public static EventSubType From(string? code) => string.IsNullOrEmpty(code) ? default : new(code);

    /// <summary>The wire code, or the empty string when this names no value.</summary>
    /// <returns>The wire code.</returns>
    public override string ToString() => _code ?? string.Empty;

    /// <summary>Annual General Meeting (<c>AGM</c>).</summary>
    public static EventSubType Agm => new("AGM");

    /// <summary>Amortisation (<c>AMT</c>).</summary>
    public static EventSubType Amt => new("AMT");

    /// <summary>Buyback (<c>BB</c>).</summary>
    public static EventSubType Bb => new("BB");

    /// <summary>Buyback Early Deadline (<c>BBED</c>).</summary>
    public static EventSubType Bbed => new("BBED");

    /// <summary>Buyback Regular Deadline (<c>BBRD</c>).</summary>
    public static EventSubType Bbrd => new("BBRD");

    /// <summary>Bond Holder Meeting (<c>BHM</c>).</summary>
    public static EventSubType Bhm => new("BHM");

    /// <summary>Bonus (<c>BON</c>).</summary>
    public static EventSubType Bon => new("BON");

    /// <summary>Call Option Exercised (<c>CALL</c>).</summary>
    public static EventSubType Call => new("CALL");

    /// <summary>Capital Distribution (<c>CAPDIST</c>).</summary>
    public static EventSubType Capdist => new("CAPDIST");

    /// <summary>Capital Gain (<c>CAPGAIN</c>).</summary>
    public static EventSubType Capgain => new("CAPGAIN");

    /// <summary>Capital Reduction (<c>CAPRD</c>).</summary>
    public static EventSubType Caprd => new("CAPRD");

    /// <summary>Court Ordered General Meeting (<c>CGM</c>).</summary>
    public static EventSubType Cgm => new("CGM");

    /// <summary>Claim Settled (<c>CLAIMSET</c>).</summary>
    public static EventSubType Claimset => new("CLAIMSET");

    /// <summary>Reverse Split, currently only used in US events / Consolidation (<c>CONSD</c>).</summary>
    public static EventSubType Consd => new("CONSD");

    /// <summary>Correction (<c>CORR</c>).</summary>
    public static EventSubType Corr => new("CORR");

    /// <summary>Clean Up (<c>CU</c>).</summary>
    public static EventSubType Cu => new("CU");

    /// <summary>Default Payment (<c>DEFPY</c>).</summary>
    public static EventSubType Defpy => new("DEFPY");

    /// <summary>Spin-Off, currently only used in US events / Distribution (<c>DIST</c>).</summary>
    public static EventSubType Dist => new("DIST");

    /// <summary>Forward Split, currently only used in US events / Dividend (<c>DIV</c>).</summary>
    public static EventSubType Div => new("DIV");

    /// <summary>Dividend Accumulation (<c>DIVACC</c>).</summary>
    public static EventSubType Divacc => new("DIVACC");

    /// <summary>Dividend Income (<c>DIVINC</c>).</summary>
    public static EventSubType Divinc => new("DIVINC");

    /// <summary>Spin-Off, currently only used in US events / Demerger (<c>DMRGR</c>).</summary>
    public static EventSubType Dmrgr => new("DMRGR");

    /// <summary>Depository Receipt Dividend (<c>DPRCPDIV</c>).</summary>
    public static EventSubType Dprcpdiv => new("DPRCPDIV");

    /// <summary>Drawings (<c>DR</c>).</summary>
    public static EventSubType Dr => new("DR");

    /// <summary>Drawings by lottery (<c>DRL</c>).</summary>
    public static EventSubType Drl => new("DRL");

    /// <summary>Tax Free Dividend Component (<c>DT</c>).</summary>
    public static EventSubType Dt => new("DT");

    /// <summary>Dutch Auction (<c>DUTCHAUCT</c>).</summary>
    public static EventSubType Dutchauct => new("DUTCHAUCT");

    /// <summary>Early Conversion (<c>ECONV</c>).</summary>
    public static EventSubType Econv => new("ECONV");

    /// <summary>Extraordinary General Meeting (<c>EGM</c>).</summary>
    public static EventSubType Egm => new("EGM");

    /// <summary>Early Redemption (<c>ER</c>).</summary>
    public static EventSubType Er => new("ER");

    /// <summary>Fully franked (<c>F</c>).</summary>
    public static EventSubType F => new("F");

    /// <summary>General Meeting (<c>GM</c>).</summary>
    public static EventSubType Gm => new("GM");

    /// <summary>Interest Basis Unknown (<c>INT</c>).</summary>
    public static EventSubType Int => new("INT");

    /// <summary>Interest Accumulation (<c>INTACC</c>).</summary>
    public static EventSubType Intacc => new("INTACC");

    /// <summary>Derived from Interest Payment (<c>INTDIV</c>).</summary>
    public static EventSubType Intdiv => new("INTDIV");

    /// <summary>Interest Income (<c>INTINC</c>).</summary>
    public static EventSubType Intinc => new("INTINC");

    /// <summary>Liquidation (<c>LIQ</c>).</summary>
    public static EventSubType Liq => new("LIQ");

    /// <summary>Maturity (<c>MAT</c>).</summary>
    public static EventSubType Mat => new("MAT");

    /// <summary>Merger (<c>MRGR</c>).</summary>
    public static EventSubType Mrgr => new("MRGR");

    /// <summary>Make Whole Call (<c>MWC</c>).</summary>
    public static EventSubType Mwc => new("MWC");

    /// <summary>Not known (<c>N</c>).</summary>
    public static EventSubType N => new("N");

    /// <summary>Non Renounceable Rights (<c>NRENRTS</c>).</summary>
    public static EventSubType Nrenrts => new("NRENRTS");

    /// <summary>Open Offer (<c>OPOFF</c>).</summary>
    public static EventSubType Opoff => new("OPOFF");

    /// <summary>Ordinary (<c>ORD</c>).</summary>
    public static EventSubType Ord => new("ORD");

    /// <summary>Partially franked (<c>P</c>).</summary>
    public static EventSubType P => new("P");

    /// <summary>Purchase Fund (<c>PF</c>).</summary>
    public static EventSubType Pf => new("PF");

    /// <summary>Priority Offer (<c>POFF</c>).</summary>
    public static EventSubType Poff => new("POFF");

    /// <summary>Property Basis Unknown (<c>PRO</c>).</summary>
    public static EventSubType Pro => new("PRO");

    /// <summary>Property Accumulation (<c>PROACC</c>).</summary>
    public static EventSubType Proacc => new("PROACC");

    /// <summary>Property Income (<c>PROINC</c>).</summary>
    public static EventSubType Proinc => new("PROINC");

    /// <summary>Put Option Exercised (<c>PUT</c>).</summary>
    public static EventSubType Put => new("PUT");

    /// <summary>Return of Capital Component (<c>RCAP</c>).</summary>
    public static EventSubType Rcap => new("RCAP");

    /// <summary>Redemption Claim (<c>REDEMCLAIM</c>).</summary>
    public static EventSubType Redemclaim => new("REDEMCLAIM");

    /// <summary>Reserves (<c>RES</c>).</summary>
    public static EventSubType Res => new("RES");

    /// <summary>Residual Maturity (<c>RM</c>).</summary>
    public static EventSubType Rm => new("RM");

    /// <summary>Repayment of Debt Component (<c>ROD</c>).</summary>
    public static EventSubType Rod => new("ROD");

    /// <summary>Subdivision / Forward Split, currently only used in US events (<c>SD</c>).</summary>
    public static EventSubType Sd => new("SD");

    /// <summary>Special General Meeting (<c>SGM</c>).</summary>
    public static EventSubType Sgm => new("SGM");

    /// <summary>Sale of Assets (<c>SOA</c>).</summary>
    public static EventSubType Soa => new("SOA");

    /// <summary>Share Premium Account (<c>SPA</c>).</summary>
    public static EventSubType Spa => new("SPA");

    /// <summary>Share Purchase Plan (<c>SPP</c>).</summary>
    public static EventSubType Spp => new("SPP");

    /// <summary>Tender Offer (<c>TEND</c>).</summary>
    public static EventSubType Tend => new("TEND");

    /// <summary>Tender resulting in Merger (<c>TENDMRGR</c>).</summary>
    public static EventSubType Tendmrgr => new("TENDMRGR");

    /// <summary>Mini-Takeover (<c>TKOVRMINI</c>).</summary>
    public static EventSubType Tkovrmini => new("TKOVRMINI");

    /// <summary>Unfranked (<c>U</c>).</summary>
    public static EventSubType U => new("U");

    /// <summary>Insufficient data to assign a TKOVR event subtype (<c>UKWNSUBTYP</c>).</summary>
    public static EventSubType Ukwnsubtyp => new("UKWNSUBTYP");

    /// <summary>Write Down (<c>WRTDN</c>).</summary>
    public static EventSubType Wrtdn => new("WRTDN");
}
