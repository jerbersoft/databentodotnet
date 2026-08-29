using NodaTime;

namespace DatabentoDotNet.Reference;

/// <summary>
/// One row of an <c>adjustment_factors.get_range</c> response: an event that changes how a
/// security's historical prices must be scaled, and the multiplier that does it.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>AdjustmentFactor</c> (<c>adjustment.rs:92-169</c>), field for field and in
/// its order. Twenty-eight properties, fourteen of them optional — upstream's <c>Option</c> in every
/// case, never a judgement made here.
/// </para>
/// <para>
/// <b>The four rate fields are <see langword="decimal"/> where upstream has <c>f64</c>, and this is
/// the type that owns that decision for all three reference models.</b> See <see cref="Factor"/>,
/// which carries the measurement.
/// </para>
/// <para>
/// <b>Two spellings of "absent" appear below, and the difference is upstream's rather than a
/// choice.</b> A missing <see cref="string"/> is <see langword="null"/>; a missing reference code —
/// <see cref="DividendCurrency"/>, <see cref="Frequency"/> — is that type's
/// <see langword="default"/>, whose <c>HasValue</c> is <see langword="false"/>. A nullable code
/// struct would give the same field two ways to say nothing, and
/// <see cref="IReferenceCode{TSelf}"/> already defines one.
/// </para>
/// <para>
/// <b><see cref="Currency"/> is a <see cref="string"/> while <see cref="DividendCurrency"/> is a
/// <see cref="DatabentoDotNet.Reference.Currency"/>, and that asymmetry is reproduced rather than
/// tidied.</b> Upstream types the two fields differently in adjacent lines
/// (<c>adjustment.rs:147</c> against <c>:157</c>). Making them agree would be a behavioural change
/// to a field neither library has probed: the closing-price currency would start rejecting nothing
/// and start reporting <c>IsKnown</c>, which reads as new information when it would only be a new
/// guess. #57 is where a real row says which spelling the server actually uses for each.
/// </para>
/// <para>
/// <b>The rows arrive in the server's order and this library does not sort them.</b> Upstream sorts
/// its <c>Vec</c> by <see cref="ExDate"/> after buffering the whole response
/// (<c>adjustment.rs:51</c>); <see cref="AdjustmentFactorsClient.GetRangeAsync"/> streams, and a
/// stream cannot be sorted. See that method, and ROADMAP.md §6.
/// </para>
/// </remarks>
public sealed record AdjustmentFactor
{
    /// <summary>
    /// Security-level numerical ID, linking every listing of the same security together.
    /// </summary>
    public required string SecurityId { get; init; }

    /// <summary>
    /// Event identifier, unique at the event level. Links to a corporate action's <c>event_id</c>.
    /// </summary>
    public required string EventId { get; init; }

    /// <summary>The event type.</summary>
    /// <remarks>
    /// An open carrier rather than an enum: a code Databento adds to the <c>EVENT</c> dictionary is
    /// kept rather than rejected. See <see cref="DatabentoDotNet.Reference.Event"/>.
    /// </remarks>
    public required Event Event { get; init; }

    /// <summary>The issuer name.</summary>
    public required string IssuerName { get; init; }

    /// <summary>The security type.</summary>
    /// <remarks>
    /// <b>The field that makes <see cref="DatabentoDotNet.Reference.SecurityType"/>'s open-carrier
    /// shape load-bearing rather than tidy.</b> Upstream types this as a bare
    /// <c>SecurityType</c> — not an <c>Option</c> — over an enum modelling 30 of the 64 codes the
    /// live dictionary reports, so one of the 34 it does not know fails the whole row rather than
    /// one field. Here an unmodelled code arrives in <c>Code</c> with <c>IsKnown</c> false.
    /// </remarks>
    public required SecurityType SecurityType { get; init; }

    /// <summary>Exchange code for the primary security, or <see langword="null"/>.</summary>
    public string? PrimaryExchange { get; init; }

    /// <summary>Exchange code for the listing, or <see langword="null"/>.</summary>
    /// <remarks>
    /// Equivalent to the MIC but more stable: a MIC may not be published in a timely fashion, and a
    /// MIC can change while the exchange stays the same.
    /// </remarks>
    public string? Exchange { get; init; }

    /// <summary>Market Identifier Code (MIC), as an ISO 10383 string.</summary>
    public required string OperatingMic { get; init; }

    /// <summary>The query input symbol this row matched, or <see langword="null"/>.</summary>
    public string? Symbol { get; init; }

    /// <summary>The Nasdaq Integrated Platform suffix-convention symbol, or <see langword="null"/>.</summary>
    public string? NasdaqSymbol { get; init; }

    /// <summary>The local code, or <see langword="null"/>.</summary>
    /// <remarks>
    /// Usually unique at market level, with exceptions. Either an alphabetic string or a number, so
    /// it stays a <see cref="string"/> either way.
    /// </remarks>
    public string? LocalCode { get; init; }

    /// <summary>The resulting local code where applicable and known, or <see langword="null"/>.</summary>
    public string? LocalCodeResulting { get; init; }

    /// <summary>The ISIN global identifier, as an ISO 6166 string, or <see langword="null"/>.</summary>
    public string? Isin { get; init; }

    /// <summary>The resulting ISIN where applicable and known, or <see langword="null"/>.</summary>
    public string? IsinResulting { get; init; }

    /// <summary>The US domestic CUSIP, or <see langword="null"/>.</summary>
    public string? UsCode { get; init; }

    /// <summary>The adjustment status.</summary>
    /// <remarks>
    /// One of the nine closed enums: three single-character codes, and an unrecognised one is an
    /// error rather than an opaque value. See <see cref="AdjustmentStatus"/>, which records that it
    /// is also the one of the nine with no independent check yet.
    /// </remarks>
    public required AdjustmentStatus Status { get; init; }

    /// <summary>The date from which the event is effective.</summary>
    /// <remarks>
    /// A <see cref="LocalDate"/>, not a <c>DateOnly</c> — CLAUDE.md, "Dates and times". It carries
    /// no zone because the wire does not: the field is <c>yyyy-MM-dd</c>, and attaching a zone here
    /// would invent one. This is the key upstream sorts its buffered response by.
    /// </remarks>
    public required LocalDate ExDate { get; init; }

    /// <summary>The adjustment factor to apply.</summary>
    /// <remarks>
    /// <para>
    /// <b><see langword="decimal"/> rather than upstream's <c>f64</c>, and this property is where
    /// that decision was made for all four rate fields here and for the eight in #54 and #55.</b>
    /// This is the multiplier applied to historical prices, so it is the field where the choice
    /// bites hardest; <see cref="DatabentoDotNet.Historical.MetadataClient.GetCostAsync"/> and
    /// <see cref="DatabentoDotNet.Historical.BatchJob.CostUsd"/> already made the same call for money on the historical side.
    /// </para>
    /// <para>
    /// <b>What was measured, on .NET 10, because the obvious argument for it is wrong.</b> The
    /// claim this decision was originally written around — that a rate round-trips through
    /// <see langword="decimal"/> and not through <see langword="double"/> — does not survive a
    /// probe. <c>System.Text.Json</c> writes a <see langword="double"/> in shortest-round-trip form,
    /// so any wire value of seventeen significant digits or fewer comes back out spelled exactly as
    /// it arrived; upstream's own fixture value <c>0.995833170541121</c> does, and so does
    /// <c>0.3333333333333333</c>. What <see langword="double"/> actually loses is not the text but
    /// the <em>value</em>: <c>0.995833170541121 * 51.19</c> is <c>50.97669999999998399</c> exactly
    /// and <c>50.97669999999998</c> in binary floating point. A factor exists to be multiplied by a
    /// price, so that is the number that matters, and it is the reason the answer here is still
    /// <see langword="decimal"/>.
    /// </para>
    /// <para>
    /// <b>The cost, also measured rather than assumed, and it is two-sided.</b> Above
    /// <see cref="decimal.MaxValue"/> (~7.9 × 10^28) <c>System.Text.Json</c> throws a
    /// <see cref="System.Text.Json.JsonException"/> naming the property path — loud, diagnosable,
    /// and confined to the row. Below ~10^-28 it does <em>not</em> throw: the value silently reads
    /// as zero, which is the worse of the two failures and the one to know about. Neither bound is
    /// reachable by a price, a dividend, a ratio near one, or a split factor, so the risk is remote
    /// rather than absent.
    /// </para>
    /// <para>
    /// <b>The magnitudes actually present in a live response are unprobed.</b>
    /// <c>adjustment_factors.get_range</c> bills, so asking is not free and was not done here; #57
    /// owns the gated request that can. This is the disclosure the issue asked for in place of the
    /// probe, not a claim that the probe happened.
    /// </para>
    /// </remarks>
    public required decimal Factor { get; init; }

    /// <summary>The closing price on <see cref="ExDate"/>, or <see langword="null"/>.</summary>
    /// <remarks><see langword="decimal"/> for the reason <see cref="Factor"/> gives.</remarks>
    public decimal? Close { get; init; }

    /// <summary>The currency of <see cref="Close"/>, or <see langword="null"/>.</summary>
    /// <remarks>
    /// A bare <see cref="string"/>, unlike <see cref="DividendCurrency"/>. Upstream's asymmetry,
    /// reproduced deliberately — see this type's remarks.
    /// </remarks>
    public string? Currency { get; init; }

    /// <summary>
    /// Market sentiment: the previous close divided by today's open — the market's reaction to the
    /// event.
    /// </summary>
    /// <remarks>
    /// Only meaningful when the factor calculation required the previous close. Upstream carries
    /// the same caveat and the same non-optional type, so a row without one still reports a number.
    /// <see langword="decimal"/> for the reason <see cref="Factor"/> gives.
    /// </remarks>
    public required decimal Sentiment { get; init; }

    /// <summary>The reason code, distinguishing event types within <see cref="Event"/>.</summary>
    /// <remarks>
    /// <para>
    /// <b>A bare <see cref="uint"/>, and that was checked rather than assumed.</b> #53's porting
    /// notes asked whether this is really a closed set before shipping one. The vendored
    /// <c>corporate_actions.list_enums</c> response — the oracle the ten open carriers and the nine
    /// closed enums were both transcribed from — has 235 groups and describes this field in none of
    /// them. Its <c>REASON</c> group is a different vocabulary entirely (<c>C</c>, <c>H</c>, blank);
    /// the four groups whose codes are numeric are <c>CLASSCODE</c>, <c>INDUS</c>, <c>MKTSG</c> and
    /// <c>REPAYSRC</c>, none of which is an adjustment reason. That is consistent with
    /// <see cref="AdjustmentStatus"/>: the dictionary documents <em>corporate actions</em>, and
    /// this is an <c>adjustment_factors</c> field.
    /// </para>
    /// <para>
    /// So there is no table to model against and an enum here would be invented rather than ported.
    /// #57 is where real rows can say what values occur.
    /// </para>
    /// </remarks>
    public required uint Reason { get; init; }

    /// <summary>
    /// The dividend before taxes or fees — the total declared by the company — or
    /// <see langword="null"/>.
    /// </summary>
    /// <remarks><see langword="decimal"/> for the reason <see cref="Factor"/> gives.</remarks>
    public decimal? GrossDividend { get; init; }

    /// <summary>
    /// The currency the dividend is paid in, or <see langword="default"/> when the row carries none.
    /// </summary>
    /// <remarks>
    /// A <see cref="DatabentoDotNet.Reference.Currency"/> where <see cref="Currency"/> is a
    /// <see cref="string"/> — upstream's asymmetry, reproduced. See this type's remarks.
    /// </remarks>
    public Currency DividendCurrency { get; init; }

    /// <summary>
    /// How often the dividend is paid, or <see langword="default"/> when the row carries none.
    /// </summary>
    public Frequency Frequency { get; init; }

    /// <summary>
    /// The choice or option number, where shareholders were given several ways to take the benefit
    /// — cash or scrip, for instance.
    /// </summary>
    /// <remarks>
    /// A bare <see cref="uint"/>, for the reason <see cref="Reason"/> records: the vendored
    /// dictionary describes no vocabulary for it either. Named <c>Option</c> on the wire; the C#
    /// property keeps that name because it is what the field is called, and nothing in this
    /// language reserves it.
    /// </remarks>
    public required uint Option { get; init; }

    /// <summary>A human-readable description of the event.</summary>
    public required string Detail { get; init; }

    /// <summary>When Databento added the record, in UTC.</summary>
    /// <remarks>
    /// An <see cref="Instant"/>, not a <c>DateTimeOffset</c>. Upstream reads this through its own
    /// <c>deserialize_date_time</c> rather than serde's default, which is exactly the set of
    /// spellings <see cref="DatabentoDotNet.Historical.Json.InstantJsonConverter"/> reads.
    /// </remarks>
    public required Instant TsCreated { get; init; }
}
