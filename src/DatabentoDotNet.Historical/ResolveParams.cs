using DatabentoDotNet.Dbn;

namespace DatabentoDotNet.Historical;

/// <summary>
/// The parameters for <c>symbology.resolve</c>: which symbols to resolve, from which symbology to
/// which, over which UTC dates.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>ResolveParams</c> (<c>symbology.rs:60-81</c>). Its <c>bon::Builder</c>
/// with two <c>#[builder(default = ...)]</c> attributes becomes <see langword="required"/> init
/// properties for the two fields with no sensible default and plain initialisers for the two with
/// one — PORTING.md's "type-state builders → <see langword="required"/> init properties".
/// </para>
/// <para>
/// <b>The two conversions are the reason this type is worth more than its five properties.</b>
/// <see cref="FromMetadata"/> exists because a historical <c>ALL_SYMBOLS</c> request comes back
/// with <em>no mappings of its own</em>: the stream names every instrument by id and nothing in it
/// says what those ids were called. Resolving afterwards from the metadata the stream itself
/// carries is the only way to name what arrived, and it is a real workflow rather than a
/// convenience. <see cref="FromQuery"/> does the same for a request you are about to send.
/// </para>
/// </remarks>
public sealed record ResolveParams
{
    /// <summary>The dataset code, for example <c>GLBX.MDP3</c>.</summary>
    public required string Dataset { get; init; }

    /// <summary>The symbols to resolve, in <see cref="StypeIn"/>'s symbology.</summary>
    public required Symbols Symbols { get; init; }

    /// <summary>
    /// The UTC date range to resolve over: inclusive <see cref="DatabentoDotNet.Historical.DateRange.Start"/>,
    /// exclusive <see cref="DatabentoDotNet.Historical.DateRange.End"/>, exactly as this type's
    /// usual contract reads — and exactly as the endpoint reads it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>That the two agree is a probed fact, not an assumption inherited from the type.</b> #45
    /// found <c>metadata.get_dataset_condition</c> reading <c>end_date</c> as <em>inclusive</em>
    /// while its neighbour <c>metadata.list_datasets</c> read it as exclusive, and closed with the
    /// rule that each new endpoint gets asked rather than assumed. So this one was asked, before
    /// <see cref="ToFormParameters"/> was written: <c>2024-01-02 .. 2024-01-03</c> for <c>ESH4</c>
    /// on <c>GLBX.MDP3</c> returns a single interval covering that one day, and
    /// <c>2024-01-02 .. 2024-01-05</c> returns the end sent, verbatim.
    /// </para>
    /// <para>
    /// The decisive answer came from the third probe, where the server states the contract itself:
    /// <c>start_date == end_date</c> is rejected with HTTP 422
    /// <c>data_date_range_start_on_or_after_end</c> — "<c>start_date</c> 2024-01-02 cannot be on or
    /// after <c>end_date</c> 2024-01-02". An endpoint reading <c>end_date</c> as inclusive has to
    /// accept that, because it is how such an endpoint spells a single day;
    /// <c>get_dataset_condition</c> does accept it. This one refuses it. Hence
    /// <see cref="DatabentoDotNet.Historical.DateRange.ToExclusiveEndDateParameters"/>, and hence
    /// <c>DateRange.OnDay(d)</c> resolving <c>d</c> alone rather than <c>d</c> and the day after.
    /// </para>
    /// <para>
    /// A <see cref="DatabentoDotNet.Historical.DateRange"/> and not a
    /// <see cref="DateTimeRange"/>, because symbology resolves per whole UTC day and there is
    /// nothing below a day for an intraday bound to mean. <see cref="FromQuery"/> and
    /// <see cref="FromMetadata"/>, whose sources both carry nanosecond bounds, narrow through
    /// <see cref="DateTimeRange.ToDateRange"/>, which rounds a partial end day <em>up</em> so the
    /// resolution never covers less than the query did.
    /// </para>
    /// </remarks>
    public required DateRange DateRange { get; init; }

    /// <summary>
    /// The symbology <see cref="Symbols"/> is expressed in. Defaults to
    /// <see cref="SType.RawSymbol"/> (<c>symbology.rs:69</c>), and is pushed onto the form
    /// unconditionally rather than omitted when unchanged, as upstream pushes it
    /// (<c>symbology.rs:32</c>).
    /// </summary>
    public SType StypeIn { get; init; } = SType.RawSymbol;

    /// <summary>
    /// The symbology to resolve <em>to</em>. Defaults to <see cref="SType.InstrumentId"/>, which
    /// with the <see cref="StypeIn"/> default makes the unconfigured request the common one:
    /// <c>ESM2</c> → <c>3403</c>.
    /// </summary>
    /// <remarks>
    /// Not every pairing with <see cref="StypeIn"/> is valid, and the invalid ones are rejected by
    /// the API rather than here — the supported set is documented per dataset and moves without a
    /// release of this library, so a client-side table would go stale silently. See
    /// <see href="https://databento.com/docs/standards-and-conventions/symbology">the symbology
    /// reference</see>.
    /// </remarks>
    public SType StypeOut { get; init; } = SType.InstrumentId;

    /// <summary>
    /// Builds the parameters that resolve the symbols a decoded stream was requested with — the
    /// <c>ALL_SYMBOLS</c> workflow this type's remarks describe.
    /// </summary>
    /// <remarks>
    /// Port of upstream's <c>TryFrom&lt;Metadata&gt;</c> (<c>symbology.rs:85-107</c>). Paired with
    /// <see cref="TryFromMetadata"/> exactly as <see cref="DbnTime.ToInstant"/> is paired with
    /// <see cref="DbnTime.TryToInstant"/>: this one names the missing field in its message, which
    /// is what a caller who expected the conversion to work needs; the other reports the same
    /// three absences as <see langword="false"/>, which is what a caller holding metadata that may
    /// legitimately lack them needs.
    /// </remarks>
    /// <param name="metadata">The decoded stream metadata.</param>
    /// <returns>Parameters resolving <paramref name="metadata"/>'s symbols over its own range.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="metadata"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="metadata"/> has no <see cref="Metadata.StypeIn"/>, no
    /// <see cref="Metadata.End"/>, or no <see cref="Metadata.Symbols"/> — the three absences
    /// described on <see cref="TryFromMetadata"/>. The message names which. A symbol carrying a
    /// character the API uses as a separator throws from
    /// <see cref="DatabentoDotNet.Symbols.From(IEnumerable{string})"/> with its own message.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="Metadata.Start"/> is <see cref="DbnConstants.UndefTimestamp"/>, which is not a
    /// time. Unlike the three absences this is not a state a real stream reaches —
    /// <see cref="Metadata.End"/> decodes the same sentinel to <see langword="null"/>, but
    /// <see cref="Metadata.Start"/> is not nullable and a stream with no start is not a stream.
    /// </exception>
    public static ResolveParams FromMetadata(Metadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (metadata.StypeIn is not { } stypeIn)
        {
            throw new ArgumentException(
                "This metadata has no stype_in, so there is no symbology to resolve its symbols "
                + "from. A live stream that mixed several leaves the field absent.",
                nameof(metadata));
        }

        if (metadata.End is not { } end)
        {
            throw new ArgumentException(
                "This metadata has no end, so there is no date range to resolve over. An "
                + "open-ended query leaves the field absent.",
                nameof(metadata));
        }

        if (metadata.Symbols.Count == 0)
        {
            throw new ArgumentException(
                "This metadata names no symbols, so there is nothing to resolve.",
                nameof(metadata));
        }

        return new ResolveParams
        {
            Dataset = metadata.Dataset,
            Symbols = Symbols.From(metadata.Symbols),
            StypeIn = stypeIn,
            StypeOut = metadata.StypeOut,
            DateRange = DateTimeRange
                .Between(DbnTime.ToInstant(metadata.Start), DbnTime.ToInstant(end))
                .ToDateRange(),
        };
    }

    /// <summary>
    /// <see cref="FromMetadata"/> for metadata that may legitimately not support a resolution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three absences, each a normal state for a real stream rather than a corruption.</b>
    /// <see cref="Metadata.StypeIn"/> is <see langword="null"/> when a stream mixed several input
    /// symbologies, the ordinary case for live data. <see cref="Metadata.End"/> is
    /// <see langword="null"/> for an open-ended query — every live session. Either one alone makes
    /// a resolution request unformulable, and upstream's <c>TryFrom</c> reports exactly these two
    /// (<c>symbology.rs:89-97</c>).
    /// </para>
    /// <para>
    /// <b>The third is this port's, and it is not upstream's oversight so much as a difference in
    /// what the two <c>Symbols</c> types permit.</b> Upstream builds
    /// <c>Symbols::Symbols(metadata.symbols)</c> unconditionally, so an empty list renders as
    /// <c>symbols=</c> and asks the API to resolve nothing;
    /// <see cref="DatabentoDotNet.Symbols.From(IEnumerable{string})"/> refuses to construct an
    /// empty set at all, for the reasons that type documents. Reporting it here rather than
    /// letting the factory throw is what keeps this method's contract — a
    /// <see langword="false"/> for every expected absence.
    /// </para>
    /// <para>
    /// <b>What still throws, deliberately.</b> Two things, and both describe a corrupt file rather
    /// than an ordinary stream: a symbol carrying a character the wire uses as a separator, which
    /// <see cref="DatabentoDotNet.Symbols.From(IEnumerable{string})"/> refuses, and a
    /// <see cref="Metadata.Start"/> holding <see cref="DbnConstants.UndefTimestamp"/>. Swallowing
    /// either into a <see langword="false"/> would report "this metadata does not support
    /// resolution" for what is actually a broken stream, which is the wrong thing to tell a caller
    /// who would then go looking at their symbology arguments.
    /// </para>
    /// </remarks>
    /// <param name="metadata">The decoded stream metadata.</param>
    /// <param name="parameters">
    /// The parameters, or <see langword="null"/> when <paramref name="metadata"/> lacks one of the
    /// three.
    /// </param>
    /// <returns><see langword="true"/> when a request could be built.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="metadata"/> is <see langword="null"/>.</exception>
    public static bool TryFromMetadata(Metadata metadata, out ResolveParams? parameters)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (metadata.StypeIn is null || metadata.End is null || metadata.Symbols.Count == 0)
        {
            parameters = null;
            return false;
        }

        parameters = FromMetadata(metadata);
        return true;
    }

    /// <summary>
    /// Builds the parameters that resolve the symbols of a request you are about to send, or have
    /// just priced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of upstream's <c>From&lt;GetRangeParams&gt;</c> (<c>symbology.rs:109-119</c>), which
    /// reads five fields off its source. This reads four, and takes the fifth as an argument.
    /// </para>
    /// <para>
    /// <b><paramref name="stypeOut"/> is a parameter rather than a field of
    /// <paramref name="query"/>, because <see cref="MetadataQueryParams"/> does not have one — and
    /// that is a genuine gap rather than a porting choice.</b> Upstream keeps two distinct types
    /// here: <c>GetQueryParams</c>, for the three billing endpoints, carries no <c>stype_out</c>
    /// (<c>metadata.rs:328-346</c>), while <c>GetRangeParams</c>, for
    /// <c>timeseries.get_range</c>, does (<c>timeseries.rs:189</c>) and posts it
    /// alongside <c>encoding</c> and <c>compression</c> (<c>timeseries.rs:131-134</c>), which the
    /// billing endpoints also do not send. <see cref="MetadataQueryParams"/> is a port of the
    /// first, so it cannot describe a <c>get_range</c> request; whether #38 widens it or
    /// introduces a second type is #38's call to make, and this method composes with either.
    /// </para>
    /// <para>
    /// <b>Requiring it is what stops the failure this gap would otherwise cause.</b> Defaulting to
    /// <see cref="SType.InstrumentId"/> — the value upstream's builder defaults to, and the
    /// obvious thing to write — would silently resolve to instrument ids for the caller who is
    /// about to request <c>raw_symbol</c> output, which is the one caller for whom the answer
    /// matters. A resolution named in the wrong symbology is not an error anywhere: every symbol
    /// resolves, nothing lands in <see cref="Resolution.NotFound"/>, and the names are simply
    /// wrong. There is no default that is right, so there is no default.
    /// </para>
    /// <para>
    /// Upstream's second conversion, <c>From&lt;GetRangeToFileParams&gt;</c>, has no counterpart
    /// because it delegates to this one after discarding a file path
    /// (<c>symbology.rs:121-125</c>).
    /// </para>
    /// </remarks>
    /// <param name="query">The query whose symbols to resolve.</param>
    /// <param name="stypeOut">
    /// The symbology to resolve to — the same value the eventual <c>get_range</c> will ask for.
    /// </param>
    /// <returns>Parameters resolving <paramref name="query"/>'s symbols over its own range.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="query"/>'s <see cref="MetadataQueryParams.DateTimeRange"/> is a default
    /// value, which names no range.
    /// </exception>
    public static ResolveParams FromQuery(MetadataQueryParams query, SType stypeOut)
    {
        ArgumentNullException.ThrowIfNull(query);

        return new ResolveParams
        {
            Dataset = query.Dataset,
            Symbols = query.Symbols,
            StypeIn = query.StypeIn,
            StypeOut = stypeOut,
            DateRange = query.DateTimeRange.ToDateRange(),
        };
    }

    /// <summary>
    /// Renders this parameter set as the form body <c>symbology.resolve</c> posts.
    /// </summary>
    /// <remarks>
    /// The order is upstream's push order (<c>symbology.rs:30-36</c>), which puts both symbology
    /// types before the symbols they describe, and appends the date pair last through the same
    /// <c>add_to_form</c> the billing endpoints use.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Symbols"/> or <see cref="DateRange"/> is left at its type's default value.
    /// <see langword="required"/> forces a caller to assign each but does not stop them assigning
    /// <see langword="default"/>, so both renderers refuse one, as their own accessors document.
    /// </exception>
    /// <returns>The form fields, in upstream's push order.</returns>
    public IReadOnlyList<KeyValuePair<string, string>> ToFormParameters()
    {
        var parameters = new List<KeyValuePair<string, string>>(6)
        {
            new("dataset", Dataset),
            new("stype_in", StypeIn.ToWireString()),
            new("stype_out", StypeOut.ToWireString()),
            new("symbols", Symbols.ToApiString()),
        };

        parameters.AddRange(DatabentoDotNet.Historical.DateRange.ToExclusiveEndDateParameters(DateRange));
        return parameters;
    }
}
