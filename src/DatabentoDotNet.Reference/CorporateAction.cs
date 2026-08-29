using System.Text.Json.Serialization;
using DatabentoDotNet.Reference.Json;
using NodaTime;

namespace DatabentoDotNet.Reference;

/// <summary>
/// One row of a <c>corporate_actions.get_range</c> response: what happened to a security, when
/// every stage of it happens, and what a holder receives.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>CorporateAction</c> (<c>corporate.rs:169-442</c>), field for field and in
/// its order, including the six group comments it is written under. <b>A hundred and four
/// properties, eighty-one of them optional</b> — upstream's <c>Option</c> in every case, never a
/// judgement made here. It is the largest model in this library by a factor of two.
/// </para>
/// <para>
/// <b>Three of the hundred and four are open maps, and they are the reason the other hundred and
/// one can be a fixed shape.</b> <see cref="DateInfo"/>, <see cref="RateInfo"/> and
/// <see cref="EventInfo"/> carry the payload that varies by event type — a meeting has a meeting
/// number, a rights issue has a subscription ratio, and no fixed column could hold both. What may
/// appear in each is documented by <c>corporate_actions.list_events</c>: every
/// <see cref="EventDocField"/> names a <see cref="FieldGroup"/>, and that group <em>is</em> which of
/// these three the field lands in. See <see cref="CorporateActionsClient.ListEventsAsync"/>.
/// </para>
/// <para>
/// <b>Twenty-four dates and two timestamps, which are not the same kind of thing.</b> The dates are
/// <see cref="LocalDate"/> and carry no zone because the wire does not — the fields are
/// <c>yyyy-MM-dd</c>, and attaching a zone here would invent one. The two timestamps are
/// <see cref="Instant"/>: <see cref="TsRecord"/> is when Databento last changed the record, and
/// <see cref="TsCreated"/> is when it first added it. Neither is a <c>DateTime</c> or a
/// <c>DateOnly</c> — CLAUDE.md, "Dates and times".
/// </para>
/// <para>
/// <b>Three spellings of "absent", and which one a field uses is a fact about its type rather than
/// about the field.</b> A missing <see cref="string"/>, date or number is <see langword="null"/>. A
/// missing <em>open</em> code — <see cref="Event"/>, <see cref="EventSubType"/>,
/// <see cref="SecurityType"/>, <see cref="Country"/>, <see cref="Currency"/>,
/// <see cref="OutturnStyle"/> — is that type's <see langword="default"/>, whose <c>HasValue</c> is
/// <see langword="false"/>. A missing <em>closed</em> enum needs a <see cref="Nullable{T}"/>,
/// because the nine closed enums are byte-backed so that <c>default</c> is an <em>undefined</em>
/// value rather than "no value" — which is why <see cref="PaymentType"/> and
/// <see cref="Fraction"/> are the two <c>?</c>s among the coded fields here. See
/// <see cref="ReferenceWireStrings"/>, and <see cref="SecurityMaster"/>, which draws the same
/// distinction over fewer fields.
/// </para>
/// <para>
/// <b>Rows arrive in the server's order and this library does not sort them.</b> Upstream buffers
/// the whole response into a <c>Vec</c> and sorts it by whichever date
/// <see cref="CorporateActionsGetRangeParams.Index"/> names (<c>corporate.rs:59-63</c>). A stream
/// has no buffer to rearrange. See <see cref="CorporateActionsClient.GetRangeAsync"/> and
/// ROADMAP.md §6.
/// </para>
/// </remarks>
public sealed record CorporateAction
{
    /* ------------------------------------------------------------------ *
     * Identifiers.
     * ------------------------------------------------------------------ */

    /// <summary>When the record last changed, in UTC.</summary>
    /// <remarks>
    /// One of the three fields <see cref="CorporateActionIndex"/> can filter on, and the only one of
    /// the three that is required — the other two are dates that may be absent.
    /// </remarks>
    public required Instant TsRecord { get; init; }

    /// <summary>
    /// The unique corporate actions record identifier, which deduplicates records describing the
    /// same event.
    /// </summary>
    public required string EventUniqueId { get; init; }

    /// <summary>The event identifier, unique at the event level.</summary>
    /// <remarks>
    /// Where applicable this links every payment row of one event together, so that all the payment
    /// options an event offers can be seen at once.
    /// </remarks>
    public required string EventId { get; init; }

    /// <summary>
    /// The unique listing numerical ID — a sequence number concatenated with
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
     * Event and dates.
     * ------------------------------------------------------------------ */

    /// <summary>The record's action status — inserted, updated, deleted or cancelled.</summary>
    /// <remarks>
    /// One of the nine closed enums, and required, so absence is not a state it can hold. See
    /// <see cref="DatabentoDotNet.Reference.Action"/>.
    /// </remarks>
    public required Action EventAction { get; init; }

    /// <summary>The event type.</summary>
    /// <remarks>
    /// <b>An open carrier, and the field that decides what the three maps below contain.</b> A code
    /// Databento adds to its dictionary arrives verbatim in <c>Code</c> with <c>IsKnown</c>
    /// <see langword="false"/> rather than failing the row — upstream models 60 of the 141 codes
    /// <c>list_enums</c> reports and would fail on any of the rest. See
    /// <see cref="DatabentoDotNet.Reference.Event"/>, and
    /// <see cref="CorporateActionsGetRangeParams.Events"/> for filtering on one this library has
    /// never seen.
    /// </remarks>
    public required Event Event { get; init; }

    /// <summary>
    /// The event subtype, or <see langword="default"/> when the row carries none.
    /// </summary>
    /// <remarks>
    /// Used for the limited number of events whose data falls into distinct sub-groupings. An open
    /// carrier: <c>list_enums</c>' <c>EVENTSUBTYPE</c> group repeats codes across parent events, so
    /// a code's meaning depends on <see cref="Event"/> and the set is not closed. See
    /// <see cref="DatabentoDotNet.Reference.EventSubType"/>.
    /// </remarks>
    public EventSubType EventSubtype { get; init; }

    /// <summary>The name of the main calendar date for this event.</summary>
    /// <remarks>
    /// Names one of this record's own date columns — <c>ex_date</c>, <c>record_date</c> and so on —
    /// or an alias for it. <c>corporate_actions.list_events</c> is the authority for which:
    /// <see cref="EventDoc.CalendarDates"/> gives each event's dates and the alias each is known by.
    /// A <see cref="string"/> rather than an enum because upstream leaves it one, and because the
    /// aliases are per-event.
    /// </remarks>
    public required string EventDateLabel { get; init; }

    /// <summary>
    /// The primary date of the event — when it is scheduled to occur or take effect — or
    /// <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="CorporateActionIndex.EventDate"/>, the default index, filters on this. It is
    /// nullable, which is worth knowing before reading a range-filtered result as exhaustive.
    /// </remarks>
    public LocalDate? EventDate { get; init; }

    /// <summary>The date the event was created or recorded in the system.</summary>
    /// <remarks>
    /// One of the two required dates on this record — the other twenty-two are optional. Distinct
    /// from <see cref="TsCreated"/>, which is a timestamp about the <em>record</em> rather than a
    /// date about the event.
    /// </remarks>
    public required LocalDate EventCreatedDate { get; init; }

    /// <summary>
    /// The date the event becomes effective or is executed, or <see langword="null"/>.
    /// </summary>
    public LocalDate? EffectiveDate { get; init; }

    /// <summary>The ex-dividend date, or <see langword="null"/>.</summary>
    /// <remarks><see cref="CorporateActionIndex.ExDate"/> filters on this.</remarks>
    public LocalDate? ExDate { get; init; }

    /// <summary>
    /// The date the company reviews its records to determine who is entitled, or
    /// <see langword="null"/>.
    /// </summary>
    /// <remarks>One business day after <see cref="ExDate"/>.</remarks>
    public LocalDate? RecordDate { get; init; }

    /// <summary>
    /// The record date ID, linking every event for the same security that shares a record date, or
    /// <see langword="null"/>.
    /// </summary>
    public string? RecordDateId { get; init; }

    /// <summary>
    /// The related event type, or <see langword="default"/> when the row names none.
    /// </summary>
    /// <remarks>
    /// The second <see cref="DatabentoDotNet.Reference.Event"/> on this record, and the one
    /// upstream's own test pins the open behaviour with: a <c>related_event</c> of <c>CORR</c>
    /// deserializes to an unknown code rather than failing the row (<c>corporate.rs:687-716</c>).
    /// </remarks>
    public Event RelatedEvent { get; init; }

    /// <summary>A direct link to another event, or <see langword="null"/>.</summary>
    public string? RelatedEventId { get; init; }

    /* ------------------------------------------------------------------ *
     * Listing.
     * ------------------------------------------------------------------ */

    /// <summary>The security's global listing activity status.</summary>
    /// <remarks>
    /// One of the nine closed enums. See <see cref="DatabentoDotNet.Reference.GlobalStatus"/>.
    /// </remarks>
    public required GlobalStatus GlobalStatus { get; init; }

    /// <summary>The listing's activity status at market level.</summary>
    /// <remarks>
    /// One of the nine closed enums. See <see cref="DatabentoDotNet.Reference.ListingStatus"/>.
    /// </remarks>
    public required ListingStatus ListingStatus { get; init; }

    /// <summary>Whether the listing-level data in this record is main or secondary.</summary>
    /// <remarks>
    /// One of the nine closed enums, and the smallest of them — two codes. See
    /// <see cref="DatabentoDotNet.Reference.ListingSource"/>.
    /// </remarks>
    public required ListingSource ListingSource { get; init; }

    /// <summary>The date the security was listed, or <see langword="null"/>.</summary>
    public LocalDate? ListingDate { get; init; }

    /// <summary>The date the security was delisted, or <see langword="null"/>.</summary>
    public LocalDate? DelistingDate { get; init; }

    /* ------------------------------------------------------------------ *
     * Exchange and issuer.
     * ------------------------------------------------------------------ */

    /// <summary>The issuer name.</summary>
    public required string IssuerName { get; init; }

    /// <summary>
    /// The security type, or <see langword="default"/> when the row carries none.
    /// </summary>
    /// <remarks>
    /// An open carrier: upstream models 30 of the 64 codes the live dictionary reports, so one of
    /// the 34 it does not know fails the whole row there. See
    /// <see cref="DatabentoDotNet.Reference.SecurityType"/>.
    /// </remarks>
    public SecurityType SecurityType { get; init; }

    /// <summary>The security description.</summary>
    public required string SecurityDescription { get; init; }

    /// <summary>Exchange code for the primary security, or <see langword="null"/>.</summary>
    public string? PrimaryExchange { get; init; }

    /// <summary>Exchange code for this listing.</summary>
    /// <remarks>
    /// The values <see cref="CorporateActionsGetRangeParams.Exchanges"/> filters on, and a
    /// <see cref="string"/> in both places for the same reason: <c>list_enums</c> reports no
    /// dictionary group for exchange codes, so there is no set to close over.
    /// </remarks>
    public required string Exchange { get; init; }

    /// <summary>
    /// Market Identifier Code (MIC), as an ISO 10383 string, or <see langword="null"/>.
    /// </summary>
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
    /// One of the two fields <c>allocate_isins</c> is about — see
    /// <see cref="CorporateActionsGetRangeParams.AllocateIsins"/> and <see cref="OutturnIsin"/>.
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

    /* ------------------------------------------------------------------ *
     * Country.
     * ------------------------------------------------------------------ */

    /// <summary>
    /// The listing country, or <see langword="default"/> when the row carries none.
    /// </summary>
    /// <remarks>
    /// The values <see cref="CorporateActionsGetRangeParams.Countries"/> filters on. Unlike
    /// <see cref="SecurityMaster"/>, this record carries no incorporation country — every
    /// <see cref="Country"/> on it is optional.
    /// </remarks>
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

    /// <summary>The market segment name, or <see langword="null"/>.</summary>
    public string? SegmentMicName { get; init; }

    /// <summary>
    /// The segment Market Identifier Code (MIC), as an ISO 10383 string, or
    /// <see langword="null"/>.
    /// </summary>
    public string? SegmentMic { get; init; }

    /* ------------------------------------------------------------------ *
     * Event and financials.
     * ------------------------------------------------------------------ */

    /// <summary>Whether participation in the event is mandatory, voluntary, or a mix.</summary>
    /// <remarks>
    /// One of the nine closed enums. See <see cref="DatabentoDotNet.Reference.MandVolu"/>.
    /// </remarks>
    public required MandVolu MandVoluFlag { get; init; }

    /// <summary>The record-date priority sequence number, or <see langword="null"/>.</summary>
    /// <remarks>
    /// Where populated, this is the order the rows have to be applied in to calculate the resulting
    /// cash and stock outcomes correctly. It is not a sort key for the response — see
    /// <see cref="CorporateActionsClient.GetRangeAsync"/> for what the library does and does not
    /// order.
    /// </remarks>
    public uint? RdPriority { get; init; }

    /// <summary>
    /// The lot size — the fewest shares acquirable in one transaction — or <see langword="null"/>.
    /// </summary>
    public uint? LotSize { get; init; }

    /// <summary>The par value amount, or <see langword="null"/>.</summary>
    /// <remarks>
    /// <b><see langword="decimal"/> where upstream has <c>f64</c>.</b> #53 settled that for every
    /// rate field across the three reference models and this issue does not re-open it;
    /// <see cref="AdjustmentFactor.Factor"/> carries the measurement, including what
    /// <see langword="decimal"/> costs at magnitudes no par value reaches.
    /// </remarks>
    public decimal? ParValue { get; init; }

    /// <summary>
    /// The currency <see cref="ParValue"/> is denominated in, or <see langword="default"/> when the
    /// row carries none.
    /// </summary>
    public Currency ParValueCurrency { get; init; }

    /// <summary>
    /// The date the dividend or payment is made to eligible holders, or <see langword="null"/>.
    /// </summary>
    public LocalDate? PaymentDate { get; init; }

    /// <summary>The due bills redemption date, or <see langword="null"/>.</summary>
    public LocalDate? DuebillsRedemptionDate { get; init; }

    /// <summary>
    /// The earliest date from which the event is valid, active or exercisable, or
    /// <see langword="null"/>.
    /// </summary>
    public LocalDate? FromDate { get; init; }

    /// <summary>
    /// The final date by which the event is valid, active or must be completed, or
    /// <see langword="null"/>.
    /// </summary>
    public LocalDate? ToDate { get; init; }

    /// <summary>The registration date, or <see langword="null"/>.</summary>
    public LocalDate? RegistrationDate { get; init; }

    /// <summary>
    /// The date the event begins or becomes effective, or <see langword="null"/>.
    /// </summary>
    public LocalDate? StartDate { get; init; }

    /// <summary>
    /// The final date by which the event is valid, active or must be completed, or
    /// <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Upstream's description is word for word <see cref="ToDate"/>'s, and both are reproduced
    /// rather than reconciled. What distinguishes the two pairs is #57's to find out against real
    /// rows; <see cref="SecurityMaster"/> records the same policy for the three fields it and
    /// <see cref="AdjustmentFactor"/> disagree about.
    /// </remarks>
    public LocalDate? EndDate { get; init; }

    /// <summary>
    /// The date the event opens for participation, or <see langword="null"/>.
    /// </summary>
    public LocalDate? OpenDate { get; init; }

    /// <summary>
    /// The final date by which participation must be completed, or <see langword="null"/>.
    /// </summary>
    public LocalDate? CloseDate { get; init; }

    /// <summary>The date the subscription period begins, or <see langword="null"/>.</summary>
    public LocalDate? StartSubscriptionDate { get; init; }

    /// <summary>The date the subscription period ends, or <see langword="null"/>.</summary>
    public LocalDate? EndSubscriptionDate { get; init; }

    /// <summary>
    /// The deadline by which participants must elect an option, or <see langword="null"/>.
    /// </summary>
    public LocalDate? OptionElectionDate { get; init; }

    /// <summary>
    /// The date withdrawal rights become effective, letting participants retract their election, or
    /// <see langword="null"/>.
    /// </summary>
    public LocalDate? WithdrawalRightsFromDate { get; init; }

    /// <summary>
    /// The final date by which withdrawal rights can be exercised, or <see langword="null"/>.
    /// </summary>
    public LocalDate? WithdrawalRightsToDate { get; init; }

    /// <summary>
    /// The date the event notification is issued or made public, or <see langword="null"/>.
    /// </summary>
    public LocalDate? NotificationDate { get; init; }

    /// <summary>
    /// The closing date of the company's financial year, or <see langword="null"/>.
    /// </summary>
    public LocalDate? FinancialYearEndDate { get; init; }

    /// <summary>
    /// The date the event or its related transaction is expected to complete, or
    /// <see langword="null"/>.
    /// </summary>
    public LocalDate? ExpCompletionDate { get; init; }

    /// <summary>The payment type, or <see langword="null"/>.</summary>
    /// <remarks>
    /// <b>One of the two closed enums on this record that a blank is legal for.</b> The
    /// <c>PAYTYPE</c> group of <c>corporate_actions.list_enums</c> carries an entry with a null
    /// code, so a blank means "no value" rather than a malformed response — and a
    /// <see cref="Nullable{T}"/> is the only way a byte-backed enum can hold that. The converter is
    /// named on the property because <c>[JsonConverter]</c> on the type can only name one, and that
    /// one is the non-nullable <see cref="PaymentTypeJsonConverter"/>. See
    /// <see cref="NullablePaymentTypeJsonConverter"/>.
    /// </remarks>
    [JsonConverter(typeof(NullablePaymentTypeJsonConverter))]
    public PaymentType? PaymentType { get; init; }

    /// <summary>
    /// The option number of the event, or <see langword="null"/>. Options are ORs — a holder picks
    /// one.
    /// </summary>
    public string? OptionId { get; init; }

    /// <summary>
    /// The serial number of the event, or <see langword="null"/>. Serials are ANDs — a holder
    /// receives all of them.
    /// </summary>
    public string? SerialId { get; init; }

    /// <summary>
    /// Whether this is the benefit a holder receives by default when several options are offered,
    /// or <see langword="null"/>.
    /// </summary>
    public bool? DefaultOptionFlag { get; init; }

    /// <summary>
    /// The payment currency, or <see langword="default"/> when the row carries none.
    /// </summary>
    public Currency RateCurrency { get; init; }

    /// <summary>
    /// The ratio denominator — the existing holding — or <see langword="null"/>.
    /// </summary>
    /// <remarks><see langword="decimal"/> for the reason <see cref="ParValue"/> gives.</remarks>
    public decimal? RatioOld { get; init; }

    /// <summary>The ratio numerator — the new holding — or <see langword="null"/>.</summary>
    /// <remarks><see langword="decimal"/> for the reason <see cref="ParValue"/> gives.</remarks>
    public decimal? RatioNew { get; init; }

    /// <summary>
    /// How fractions are handled in settlement calculations, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// The other closed enum on this record that a blank is legal for — the <c>FRACCD</c> group
    /// carries a null-code entry, described as "A Blank value is possible". See
    /// <see cref="NullableFractionJsonConverter"/>, and <see cref="PaymentType"/> for why the
    /// converter is named on the property.
    /// </remarks>
    [JsonConverter(typeof(NullableFractionJsonConverter))]
    public Fraction? Fraction { get; init; }

    /// <summary>
    /// The style of the outturn security, or <see langword="default"/> when the row carries none.
    /// </summary>
    /// <remarks>
    /// An open carrier rather than a closed enum, so a style outside the modelled set arrives in
    /// <c>Code</c> rather than failing the row. See
    /// <see cref="DatabentoDotNet.Reference.OutturnStyle"/>.
    /// </remarks>
    public OutturnStyle OutturnStyle { get; init; }

    /// <summary>
    /// The outturn security's asset type, or <see langword="default"/> when the row carries none.
    /// </summary>
    /// <remarks>The second <see cref="DatabentoDotNet.Reference.SecurityType"/> on this record.</remarks>
    public SecurityType OutturnSecurityType { get; init; }

    /// <summary>The outturn security ID, or <see langword="null"/>.</summary>
    public string? OutturnSecurityId { get; init; }

    /// <summary>The outturn ISIN, or <see langword="null"/>.</summary>
    /// <remarks>
    /// The second field <c>allocate_isins</c> is about; see
    /// <see cref="CorporateActionsGetRangeParams.AllocateIsins"/>.
    /// </remarks>
    public string? OutturnIsin { get; init; }

    /// <summary>The outturn CUSIP, or <see langword="null"/>.</summary>
    public string? OutturnUsCode { get; init; }

    /// <summary>The outturn local code, or <see langword="null"/>.</summary>
    public string? OutturnLocalCode { get; init; }

    /// <summary>The outturn Bloomberg composite ID, or <see langword="null"/>.</summary>
    public string? OutturnBbgCompId { get; init; }

    /// <summary>The outturn Bloomberg composite ticker, or <see langword="null"/>.</summary>
    public string? OutturnBbgCompTicker { get; init; }

    /// <summary>
    /// The outturn FIGI — the Bloomberg exchange-level ID — or <see langword="null"/>.
    /// </summary>
    public string? OutturnFigi { get; init; }

    /// <summary>
    /// The outturn Bloomberg exchange-level ticker, or <see langword="null"/>.
    /// </summary>
    public string? OutturnFigiTicker { get; init; }

    /// <summary>
    /// The smallest quantity a holder may offer from their holding, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <b>A <see cref="ulong"/>, upstream's <c>u64</c>, for all six quantity fields.</b> A share
    /// count is not a rate, so the <see langword="decimal"/> argument that governs
    /// <see cref="ParValue"/> does not reach it: the value is an exact integer on the wire and an
    /// exact integer here.
    /// </remarks>
    public ulong? MinOfferQty { get; init; }

    /// <summary>
    /// The largest quantity a holder may offer from their holding, or <see langword="null"/>.
    /// </summary>
    public ulong? MaxOfferQty { get; init; }

    /// <summary>
    /// The smallest holding that qualifies to take part in the event, or <see langword="null"/>.
    /// </summary>
    public ulong? MinQualifyQty { get; init; }

    /// <summary>
    /// The largest holding that qualifies to take part in the event, or <see langword="null"/>.
    /// </summary>
    public ulong? MaxQualifyQty { get; init; }

    /// <summary>
    /// The smallest total the company will accept from all tendering holders for the event to bind
    /// the offeror, or <see langword="null"/>.
    /// </summary>
    public ulong? MinAcceptQty { get; init; }

    /// <summary>
    /// The largest total the company will accept from all tendering holders for the event to bind
    /// the offeror, or <see langword="null"/>.
    /// </summary>
    public ulong? MaxAcceptQty { get; init; }

    /// <summary>
    /// For a tender, the cut-off price at which all bids are accepted, or <see langword="null"/>.
    /// </summary>
    /// <remarks><see langword="decimal"/> for the reason <see cref="ParValue"/> gives.</remarks>
    public decimal? TenderStrikePrice { get; init; }

    /// <summary>
    /// For a tender, the price step bids may be placed in, or <see langword="null"/>.
    /// </summary>
    /// <remarks><see langword="decimal"/> for the reason <see cref="ParValue"/> gives.</remarks>
    public decimal? TenderPriceStep { get; init; }

    /// <summary>The option expiry time, or <see langword="null"/>.</summary>
    /// <remarks>
    /// <b>A <see cref="string"/> rather than a <see cref="LocalTime"/>, and deliberately.</b>
    /// Upstream leaves all four <c>*_time</c> fields as <c>Option&lt;String&gt;</c> and documents no
    /// format for them, unlike the twenty-four dates and two timestamps, whose formats it parses.
    /// Picking a pattern here would be inventing a contract; the value is handed over as the server
    /// wrote it, together with its zone in <see cref="OptionExpiryTz"/>. #57 is where a real row can
    /// say what these hold.
    /// </remarks>
    public string? OptionExpiryTime { get; init; }

    /// <summary>The time zone <see cref="OptionExpiryTime"/> is expressed in, or <see langword="null"/>.</summary>
    /// <remarks>
    /// A <see cref="string"/> and not a <c>DateTimeZone</c>: it is not known to be an IANA zone ID,
    /// so resolving it against a time-zone database would be a guess. See
    /// <see cref="OptionExpiryTime"/>.
    /// </remarks>
    public string? OptionExpiryTz { get; init; }

    /// <summary>The withdrawal rights flag, or <see langword="null"/>.</summary>
    public bool? WithdrawalRightsFlag { get; init; }

    /// <summary>The withdrawal rights expiry time, or <see langword="null"/>.</summary>
    /// <remarks>A <see cref="string"/> for the reason <see cref="OptionExpiryTime"/> gives.</remarks>
    public string? WithdrawalRightsExpiryTime { get; init; }

    /// <summary>
    /// The time zone <see cref="WithdrawalRightsExpiryTime"/> is expressed in, or
    /// <see langword="null"/>.
    /// </summary>
    public string? WithdrawalRightsExpiryTz { get; init; }

    /// <summary>The expiry time, or <see langword="null"/>.</summary>
    /// <remarks>A <see cref="string"/> for the reason <see cref="OptionExpiryTime"/> gives.</remarks>
    public string? ExpiryTime { get; init; }

    /// <summary>
    /// The time zone <see cref="ExpiryTime"/> is expressed in, or <see langword="null"/>.
    /// </summary>
    public string? ExpiryTz { get; init; }

    /* ------------------------------------------------------------------ *
     * The open maps, and when Databento added the record.
     * ------------------------------------------------------------------ */

    /// <summary>
    /// Event-specific dates, keyed by the name the server files each under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Required, not optional — a row without this key is a malformed row.</b> Upstream declares
    /// no <c>#[serde(default)]</c> anywhere in <c>corporate.rs</c>, so an absent map fails
    /// deserialization there, and <see langword="required"/> makes it fail here. An <em>empty</em>
    /// map is an ordinary answer and by far the commonest one — upstream's own fixture row sends
    /// <c>"date_info": {}</c>. The two are different responses and this library keeps them apart.
    /// </para>
    /// <para>
    /// <b>Inside the map, a key carrying <see langword="null"/> is a value and not an absence.</b>
    /// The server saying "this event has a <c>meeting_date</c>, and it is not yet set" is not the
    /// same statement as saying nothing about <c>meeting_date</c> at all. The value type is
    /// therefore <c>Instant?</c> and a caller distinguishes the two with <c>ContainsKey</c> —
    /// exactly as upstream's <c>HashMap&lt;String, Option&lt;OffsetDateTime&gt;&gt;</c> does. Its
    /// own fixture exercises this on <see cref="RateInfo"/>, whose two keys both carry
    /// <c>null</c> (<c>corporate.rs:551</c>).
    /// </para>
    /// <para>
    /// <b>Keys arrive as the server wrote them and are matched ordinally.</b> No naming policy
    /// transforms a dictionary key on the way in, and the comparer is the default ordinal one — so
    /// <c>meeting_date</c> is not <c>Meeting_Date</c>. What may legally appear is documented by
    /// <c>corporate_actions.list_events</c>: every <see cref="EventDocField"/> whose
    /// <see cref="EventDocField.Group"/> is <c>date_info</c> names a key this map may carry for that
    /// event. It is documentation, not a constraint — an undocumented key still arrives, which is
    /// the point of an open map.
    /// </para>
    /// <para>
    /// <b>This library reads a wider set of timestamp spellings here than upstream does, and that is
    /// a divergence worth naming.</b> Upstream parses these values with
    /// <c>deserialize_opt_date_time_hash_map</c>, which accepts ISO 8601 and nothing else, while the
    /// two fixed timestamps on this record go through <c>deserialize_date_time</c>, which falls back
    /// to a legacy space-separated form (<c>databento-rs/src/deserialize.rs:7-53</c>). The asymmetry
    /// looks like an oversight rather than a rule: the two formats are mutually unambiguous, so
    /// accepting both cannot change how any value is read, only whether a row is rejected. This map
    /// uses the same <see cref="DatabentoDotNet.Historical.Json.InstantJsonConverter"/> as
    /// <see cref="TsRecord"/> and therefore accepts both.
    /// </para>
    /// </remarks>
    public required IReadOnlyDictionary<string, Instant?> DateInfo { get; init; }

    /// <summary>
    /// Event-specific payment figures, keyed by the name the server files each under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Required, keyed ordinally, and distinguishing a <see langword="null"/> value from an absent
    /// key, all for the reasons <see cref="DateInfo"/> gives. The
    /// <see cref="EventDocField.Group"/> that documents this one is <c>rate_info</c>.
    /// </para>
    /// <para>
    /// <b><see langword="decimal"/> where upstream has <c>f64</c>, matching the fixed rate columns
    /// rather than diverging from them.</b> #53 settled that for every rate field across the three
    /// reference models; a map of rates is still rates. See <see cref="ParValue"/> and
    /// <see cref="AdjustmentFactor.Factor"/>.
    /// </para>
    /// <para>
    /// <b>The three maps are three types and not one, because their values are.</b> Collapsing them
    /// into a single <c>Dictionary&lt;string, string&gt;</c> would hand every caller the parsing
    /// this library exists to do, and collapsing them into a single <c>object</c>-valued map would
    /// hand them a cast. Upstream keeps three for the same reason.
    /// </para>
    /// </remarks>
    public required IReadOnlyDictionary<string, decimal?> RateInfo { get; init; }

    /// <summary>
    /// Additional event-specific information, keyed by the name the server files each under.
    /// </summary>
    /// <remarks>
    /// Required, keyed ordinally, and distinguishing a <see langword="null"/> value from an absent
    /// key, all for the reasons <see cref="DateInfo"/> gives. The
    /// <see cref="EventDocField.Group"/> that documents this one is <c>event_info</c>, and it is the
    /// broadest of the three — <c>list_events</c> files a meeting's <c>meeting_number</c> here, for
    /// instance. The values stay <see cref="string"/> because that is all the server promises about
    /// them.
    /// </remarks>
    public required IReadOnlyDictionary<string, string?> EventInfo { get; init; }

    /// <summary>When Databento added the record, in UTC.</summary>
    /// <remarks>
    /// Distinct from <see cref="TsRecord"/>, which moves every time the record changes, and from
    /// <see cref="EventCreatedDate"/>, which is a date about the event rather than a timestamp about
    /// the record. <see cref="CorporateActionIndex"/> cannot filter on this one.
    /// </remarks>
    public required Instant TsCreated { get; init; }
}
