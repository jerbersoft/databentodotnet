using NodaTime;

namespace DatabentoDotNet.Reference;

/// <summary>
/// One row of a <c>security_master.get_range</c> or <c>security_master.get_last</c> response: what
/// a listing was, where it traded, every identifier it is known by, and the window over which that
/// description held.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>SecurityMaster</c> (<c>security.rs:161-294</c>), field for field and in
/// its order, including the five group comments it is written under. Fifty properties, thirty-five
/// of them optional — upstream's <c>Option</c> in every case, never a judgement made here.
/// </para>
/// <para>
/// <b>Both endpoints return this same type</b>, which is upstream's arrangement too: the difference
/// between them is which rows are selected, never what a row contains. See
/// <see cref="SecurityMasterClient"/>.
/// </para>
/// <para>
/// <b>Three timestamps that are easy to confuse, so they are named together once here.</b>
/// <see cref="TsEffective"/> is when the details became true of the security;
/// <see cref="TsRecord"/> is when Databento last changed the record saying so; and
/// <see cref="TsCreated"/> is when Databento first added it. Only the first two can be filtered on,
/// and <see cref="SecurityMasterIndex"/> is how.
/// </para>
/// <para>
/// <b>The optionality here disagrees with <see cref="AdjustmentFactor"/> on three fields, and that
/// is reproduced rather than reconciled.</b> <c>operating_mic</c> is required there and optional
/// here; <c>exchange</c> is optional there and required here; <c>security_type</c> is a bare enum
/// there and an <c>Option</c> here. Upstream types each of the six that way
/// (<c>adjustment.rs:104-118</c> against <c>security.rs:200-215</c>), and a client that quietly
/// made them agree would be reporting a guess as though it were the API's contract. #57 is where
/// real rows can say which library is right about each.
/// </para>
/// <para>
/// <b>Two spellings of "absent", as in <see cref="AdjustmentFactor"/>.</b> A missing
/// <see cref="string"/> is <see langword="null"/>; a missing reference code — the four
/// <see cref="Country"/> and <see cref="Currency"/> properties, and <see cref="SecurityType"/> — is
/// that type's <see langword="default"/>, whose <c>HasValue</c> is <see langword="false"/>.
/// <see cref="Voting"/> is the exception that proves it is a rule about carriers rather than about
/// structs: it is one of the nine closed enums, whose <see langword="default"/> is an
/// <em>undefined</em> byte rather than "no value", so absence there needs a
/// <see cref="Nullable{T}"/> and gets one.
/// </para>
/// <para>
/// <b>Rows arrive in the server's order and this library does not sort them.</b> Upstream sorts its
/// buffered <c>Vec</c> — by the index for <c>get_range</c> (<c>security.rs:50-53</c>) and by
/// <see cref="TsEffective"/> for <c>get_last</c> (<c>security.rs:77</c>). Both stream here, and a
/// stream cannot be sorted. See <see cref="SecurityMasterClient.GetRangeAsync"/> and ROADMAP.md §6.
/// </para>
/// </remarks>
public sealed record SecurityMaster
{
    /* ------------------------------------------------------------------ *
     * Identifiers.
     * ------------------------------------------------------------------ */

    /// <summary>When the record last changed, in UTC.</summary>
    /// <remarks>
    /// An <see cref="Instant"/>, not a <c>DateTimeOffset</c> — CLAUDE.md, "Dates and times".
    /// Upstream reads this through its own <c>deserialize_date_time</c> rather than serde's
    /// default, which is exactly the set of spellings
    /// <see cref="DatabentoDotNet.Historical.Json.InstantJsonConverter"/> reads. One of the two
    /// fields <see cref="SecurityMasterIndex"/> can filter on.
    /// </remarks>
    public required Instant TsRecord { get; init; }

    /// <summary>When the record's details take effect, in UTC.</summary>
    /// <remarks>
    /// The other field <see cref="SecurityMasterIndex"/> can filter on, and its default. This is
    /// also the key upstream sorts <c>get_last</c> by after buffering; see
    /// <see cref="SecurityMasterClient.GetLastAsync"/>.
    /// </remarks>
    public required Instant TsEffective { get; init; }

    /// <summary>
    /// Unique listing numerical ID — a sequence number concatenated with
    /// <see cref="ListingGroupId"/>.
    /// </summary>
    public required string ListingId { get; init; }

    /// <summary>
    /// Groups every listing of the same security on one exchange, often in different trading
    /// currencies.
    /// </summary>
    public required string ListingGroupId { get; init; }

    /// <summary>
    /// Security-level numerical ID, linking every listing of the same security together.
    /// </summary>
    public required string SecurityId { get; init; }

    /// <summary>
    /// Issuer-level numerical ID, linking every security of one company together.
    /// </summary>
    public required string IssuerId { get; init; }

    /* ------------------------------------------------------------------ *
     * Listing.
     * ------------------------------------------------------------------ */

    /// <summary>The listing's activity status at market level.</summary>
    /// <remarks>
    /// One of the nine closed enums: twelve single-character codes, and an unrecognised one is an
    /// error rather than an opaque value. See
    /// <see cref="DatabentoDotNet.Reference.ListingStatus"/>.
    /// </remarks>
    public required ListingStatus ListingStatus { get; init; }

    /// <summary>Whether the listing-level data in this record is main or secondary.</summary>
    /// <remarks>
    /// One of the nine closed enums, and the smallest of them — two codes. See
    /// <see cref="DatabentoDotNet.Reference.ListingSource"/>.
    /// </remarks>
    public required ListingSource ListingSource { get; init; }

    /// <summary>The date the listing was created.</summary>
    /// <remarks>
    /// A <see cref="LocalDate"/>, not a <c>DateOnly</c> — CLAUDE.md, "Dates and times". It carries
    /// no zone because the wire does not: the field is <c>yyyy-MM-dd</c>, and attaching a zone here
    /// would invent one. The same holds for the other three dates below.
    /// </remarks>
    public required LocalDate ListingCreatedDate { get; init; }

    /// <summary>The date the security was listed, or <see langword="null"/>.</summary>
    public LocalDate? ListingDate { get; init; }

    /// <summary>The date the security was delisted, or <see langword="null"/>.</summary>
    public LocalDate? DelistingDate { get; init; }

    /* ------------------------------------------------------------------ *
     * Exchange.
     * ------------------------------------------------------------------ */

    /// <summary>The issuer name.</summary>
    public required string IssuerName { get; init; }

    /// <summary>
    /// The security type, or <see langword="default"/> when the row carries none.
    /// </summary>
    /// <remarks>
    /// An open carrier rather than an enum: a code Databento adds to the dictionary is kept rather
    /// than rejected. Upstream models 30 of the 64 codes the live dictionary reports, so one of the
    /// 34 it does not know fails the whole row there. See
    /// <see cref="DatabentoDotNet.Reference.SecurityType"/>, and this type's remarks for why the
    /// field is optional here and required on <see cref="AdjustmentFactor"/>.
    /// </remarks>
    public SecurityType SecurityType { get; init; }

    /// <summary>The security description.</summary>
    public required string SecurityDescription { get; init; }

    /// <summary>Exchange code for the primary security, or <see langword="null"/>.</summary>
    public string? PrimaryExchange { get; init; }

    /// <summary>Exchange code for this listing.</summary>
    /// <remarks>
    /// Equivalent to the MIC but more stable: a MIC may not be published in a timely fashion, and a
    /// MIC can change while the exchange stays the same. Required here and optional on
    /// <see cref="AdjustmentFactor"/> — upstream's asymmetry, reproduced.
    /// </remarks>
    public required string Exchange { get; init; }

    /// <summary>
    /// Market Identifier Code (MIC), as an ISO 10383 string, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Optional here and required on <see cref="AdjustmentFactor"/>. See this type's remarks.
    /// </remarks>
    public string? OperatingMic { get; init; }

    /* ------------------------------------------------------------------ *
     * Symbology.
     * ------------------------------------------------------------------ */

    /// <summary>The query input symbol this row matched, or <see langword="null"/>.</summary>
    public string? Symbol { get; init; }

    /// <summary>
    /// The Nasdaq Integrated Platform suffix-convention symbol, or <see langword="null"/>.
    /// </summary>
    public string? NasdaqSymbol { get; init; }

    /// <summary>The local code, or <see langword="null"/>.</summary>
    /// <remarks>
    /// Usually unique at market level, with exceptions. Either an alphabetic string or a number, so
    /// it stays a <see cref="string"/> either way.
    /// </remarks>
    public string? LocalCode { get; init; }

    /// <summary>
    /// The ISIN global identifier, as an ISO 6166 string, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// The field <c>allocate_isins</c> is about. Left at its default, a request may create a new
    /// allocation to populate this on an ISIN-limited plan; set it <see langword="false"/> and the
    /// row is dropped instead. See <see cref="SecurityMasterGetRangeParams.AllocateIsins"/>.
    /// </remarks>
    public string? Isin { get; init; }

    /// <summary>The US domestic CUSIP, or <see langword="null"/>.</summary>
    public string? UsCode { get; init; }

    /// <summary>The Bloomberg composite global ID, or <see langword="null"/>.</summary>
    public string? BbgCompId { get; init; }

    /// <summary>The Bloomberg composite ticker, or <see langword="null"/>.</summary>
    public string? BbgCompTicker { get; init; }

    /// <summary>The Bloomberg FIGI — the exchange-level ID — or <see langword="null"/>.</summary>
    public string? Figi { get; init; }

    /// <summary>The Bloomberg exchange-level ticker, or <see langword="null"/>.</summary>
    public string? FigiTicker { get; init; }

    /// <summary>The Financial Instrument Short Name, or <see langword="null"/>.</summary>
    public string? Fisn { get; init; }

    /// <summary>The Legal Entity Identifier, or <see langword="null"/>.</summary>
    public string? Lei { get; init; }

    /// <summary>The Standard Industrial Classification code, or <see langword="null"/>.</summary>
    public string? Sic { get; init; }

    /// <summary>The Central Index Key, or <see langword="null"/>.</summary>
    public string? Cik { get; init; }

    /// <summary>The Global Industry Standard Classification, or <see langword="null"/>.</summary>
    public string? Gics { get; init; }

    /// <summary>
    /// The North American Industrial Classification System code, or <see langword="null"/>.
    /// </summary>
    public string? Naics { get; init; }

    /// <summary>The Complementary Identification Code, or <see langword="null"/>.</summary>
    public string? Cic { get; init; }

    /// <summary>
    /// The Classification of Financial Instruments, as an ISO 10962 string, or
    /// <see langword="null"/>.
    /// </summary>
    public string? Cfi { get; init; }

    /* ------------------------------------------------------------------ *
     * Country.
     * ------------------------------------------------------------------ */

    /// <summary>The issuer's country of incorporation.</summary>
    /// <remarks>
    /// The one reference code on this record upstream does not wrap in an <c>Option</c>. It is
    /// still an open carrier here, so a country outside the modelled set arrives in <c>Code</c>
    /// with <c>IsKnown</c> false rather than failing the row.
    /// </remarks>
    public required Country IncorporationCountry { get; init; }

    /// <summary>
    /// The listing country, or <see langword="default"/> when the row carries none.
    /// </summary>
    public Country ListingCountry { get; init; }

    /// <summary>
    /// The register country, or <see langword="default"/> when the row carries none.
    /// </summary>
    public Country RegisterCountry { get; init; }

    /// <summary>
    /// The trading currency, or <see langword="default"/> when the row carries none.
    /// </summary>
    public Currency TradingCurrency { get; init; }

    /// <summary>
    /// <see langword="true"/> when the market currently carries more than one listing of this
    /// security.
    /// </summary>
    public required bool MultiCurrency { get; init; }

    /* ------------------------------------------------------------------ *
     * Financials.
     * ------------------------------------------------------------------ */

    /// <summary>The market segment name, or <see langword="null"/>.</summary>
    public string? SegmentMicName { get; init; }

    /// <summary>
    /// The segment Market Identifier Code (MIC), as an ISO 10383 string, or
    /// <see langword="null"/>.
    /// </summary>
    public string? SegmentMic { get; init; }

    /// <summary>The security structure, or <see langword="null"/>.</summary>
    public string? Structure { get; init; }

    /// <summary>
    /// The lot size — the fewest shares acquirable in one transaction — or <see langword="null"/>.
    /// </summary>
    public uint? LotSize { get; init; }

    /// <summary>The par value amount, or <see langword="null"/>.</summary>
    /// <remarks>
    /// <b><see langword="decimal"/> where upstream has <c>f64</c>.</b> #53 settled that for all
    /// twelve rate fields across the three reference models and this issue does not re-open it;
    /// <see cref="AdjustmentFactor.Factor"/> carries the measurement, including what
    /// <see langword="decimal"/> costs at magnitudes no par value reaches.
    /// </remarks>
    public decimal? ParValue { get; init; }

    /// <summary>
    /// The currency <see cref="ParValue"/> is denominated in, or <see langword="default"/> when the
    /// row carries none.
    /// </summary>
    public Currency ParValueCurrency { get; init; }

    /// <summary>The voting rights carried, or <see langword="null"/>.</summary>
    /// <remarks>
    /// <para>
    /// <b>The one <see cref="Nullable{T}"/> among this record's coded fields, and the only place a
    /// closed enum meets an <c>Option</c>.</b> The nine closed enums are byte-backed so that
    /// <c>default</c> is an undefined value rather than a member — see
    /// <see cref="ReferenceWireStrings"/> — which is exactly why absence cannot be spelled as
    /// <c>default</c> here the way it is for the four reference codes above.
    /// </para>
    /// <para>
    /// <b>It needs no second converter, and that was checked.</b>
    /// <see cref="System.Text.Json"/> answers a <c>null</c> token for a <see cref="Nullable{T}"/>
    /// itself, without reaching <see cref="Json.VotingJsonConverter"/> — so only the empty
    /// <em>string</em> would, and the <c>VOTING</c> group of <c>corporate_actions.list_enums</c>
    /// lists no blank entry. That is the difference from <see cref="Fraction"/> and
    /// <see cref="PaymentType"/>, which do get one. See
    /// <see cref="DatabentoDotNet.Reference.Voting"/>.
    /// </para>
    /// </remarks>
    public Voting? Voting { get; init; }

    /// <summary>The number of votes per security, or <see langword="null"/>.</summary>
    /// <remarks><see langword="decimal"/> for the reason <see cref="ParValue"/> gives.</remarks>
    public decimal? VotePerSec { get; init; }

    /// <summary>The number of shares outstanding, or <see langword="null"/>.</summary>
    /// <remarks>
    /// A <see cref="ulong"/>, upstream's <c>u64</c>. A share count is not a rate, so the
    /// <see langword="decimal"/> argument does not reach it: the value is an exact integer on the
    /// wire and an exact integer here.
    /// </remarks>
    public ulong? SharesOutstanding { get; init; }

    /// <summary>
    /// The date <see cref="SharesOutstanding"/> is effective from, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// The fourth <see cref="LocalDate"/> on this record, and the one outside the listing group —
    /// it belongs to the share count above rather than to the listing's lifecycle.
    /// </remarks>
    public LocalDate? SharesOutstandingDate { get; init; }

    /// <summary>When Databento added the record, in UTC.</summary>
    /// <remarks>
    /// Distinct from <see cref="TsRecord"/>, which moves every time the record changes, and from
    /// <see cref="TsEffective"/>, which is about the security rather than about the record. Neither
    /// endpoint filters on this one.
    /// </remarks>
    public required Instant TsCreated { get; init; }
}
