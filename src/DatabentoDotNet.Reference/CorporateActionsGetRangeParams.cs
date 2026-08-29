using DatabentoDotNet.Dbn;

namespace DatabentoDotNet.Reference;

/// <summary>
/// The parameter set <c>corporate_actions.get_range</c> takes: which symbols, over what range of
/// which date, optionally narrowed to a set of events, countries, exchanges or security types.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>corporate::GetRangeParams</c> (<c>corporate.rs:124-167</c>). Named for its
/// endpoint rather than as a bare <c>GetRangeParams</c>, for the reason
/// <see cref="AdjustmentFactorsGetRangeParams"/> records: upstream can reuse that name in three
/// modules of one crate and C# cannot reuse it in one namespace.
/// </para>
/// <para>
/// <b>The widest filter set in this namespace — four lists where the other two endpoints have
/// two.</b> <see cref="Events"/> and <see cref="Exchanges"/> exist only here, because only this
/// endpoint returns rows that have an event and a listing exchange to filter on. All four behave
/// alike: <see langword="null"/> or empty means the parameter is left out entirely rather than sent
/// blank.
/// </para>
/// <para>
/// <b><c>compression</c> is not a property, because it is not a choice.</b> Upstream hard-codes
/// <c>compression=zstd</c> (<c>corporate.rs:42</c>) and so does <see cref="ToFormParameters"/>. The
/// response handler requires the frame — an uncompressed body would not parse — so a caller who
/// could set it could only break the request.
/// </para>
/// </remarks>
public sealed record CorporateActionsGetRangeParams
{
    /// <summary>The symbols to filter for.</summary>
    public required Symbols Symbols { get; init; }

    /// <summary>The request range: inclusive start, optional exclusive end.</summary>
    /// <remarks>
    /// Filters on <see cref="Index"/> — which is what makes that property a request parameter here
    /// rather than a presentation choice. Omit the end and the response runs to the end of the
    /// data; see <see cref="ReferenceDateTimeRange"/>, which also records that the exclusive end is
    /// documented rather than probed.
    /// </remarks>
    public required ReferenceDateTimeRange DateTimeRange { get; init; }

    /// <summary>
    /// Which date <see cref="DateTimeRange"/> applies to. Defaults to
    /// <see cref="CorporateActionIndex.EventDate"/>, as upstream's builder does.
    /// </summary>
    /// <remarks>
    /// It changes which rows come back, not their order: upstream also sorts its buffered response
    /// by it (<c>corporate.rs:59-63</c>) and <see cref="CorporateActionsClient.GetRangeAsync"/>
    /// streams, so the sort is not performed here. See <see cref="CorporateActionIndex"/>, which
    /// also notes that two of the three name a nullable column.
    /// </remarks>
    public CorporateActionIndex Index { get; init; } = CorporateActionIndex.EventDate;

    /// <summary>
    /// The symbology <see cref="Symbols"/> is expressed in. Defaults to
    /// <see cref="SType.RawSymbol"/>, as upstream's builder does.
    /// </summary>
    public SType StypeIn { get; init; } = SType.RawSymbol;

    /// <summary>
    /// The event types to filter for, or <see langword="null"/> to include every event.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An open carrier, so a code this library has never seen still reaches the server.</b> That
    /// is the case #51 made <see cref="Event"/> a <c>readonly record struct</c> for rather than an
    /// <see langword="enum"/>: a caller who read an unrecognised event code out of a response —
    /// where <see cref="CorporateAction.Event"/> keeps it verbatim — can turn round and filter on
    /// it. A plain C# enum would have nothing to put in the list.
    /// </para>
    /// <para>
    /// An empty sequence means the same thing as <see langword="null"/>: the parameter is left out.
    /// A <see langword="default"/> <see cref="Event"/> in the list is a caller mistake and is
    /// refused — see <see cref="ReferenceCodeFilter.Render{T}"/>.
    /// </para>
    /// </remarks>
    public IReadOnlyList<Event>? Events { get; init; }

    /// <summary>
    /// The countries to filter for, or <see langword="null"/> to include every country.
    /// </summary>
    /// <remarks>An empty sequence behaves as <see langword="null"/>, as with <see cref="Events"/>.</remarks>
    public IReadOnlyList<Country>? Countries { get; init; }

    /// <summary>
    /// The listing exchanges to filter for, or <see langword="null"/> to include every exchange.
    /// </summary>
    /// <remarks>
    /// <b>Bare strings, not a code type, and that is not an omission.</b> <c>list_enums</c> reports
    /// no group for exchange codes, so there is no dictionary to close over and nothing for an
    /// <see cref="IReferenceCode{T}"/> to be open against — <see cref="CorporateAction.Exchange"/>
    /// is a <see cref="string"/> for the same reason. A blank entry is refused rather than dropped;
    /// see <see cref="ReferenceCodeFilter.Render(IEnumerable{string})"/>.
    /// </remarks>
    public IReadOnlyList<string>? Exchanges { get; init; }

    /// <summary>
    /// The security types to filter for, or <see langword="null"/> to include every type.
    /// </summary>
    /// <remarks>An empty sequence behaves as <see langword="null"/>, as with <see cref="Events"/>.</remarks>
    public IReadOnlyList<SecurityType>? SecurityTypes { get; init; }

    /// <summary>
    /// Whether the request may allocate new ISINs on an ISIN-limited plan. Defaults to
    /// <see langword="true"/>, as upstream's builder does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A billing consequence hiding in a default: left alone, a request for symbols the plan has not
    /// seen before can create new allocations against an ISIN-limited entitlement
    /// (<c>corporate.rs:163-167</c>). Set it <see langword="false"/> and the API drops the rows that
    /// would have done so rather than returning them. The default is upstream's and is kept, for the
    /// reason <see cref="SecurityMasterGetRangeParams.AllocateIsins"/> gives at length — that is the
    /// endpoint where it bites hardest, and this property is the same decision.
    /// </para>
    /// <para>
    /// <b>No test that reaches the real API may leave this <see langword="true"/> without going
    /// through #57's gate.</b> That is a rule about spending someone's entitlement, not a style
    /// preference.
    /// </para>
    /// </remarks>
    public bool AllocateIsins { get; init; } = true;

    /// <summary>
    /// Renders this parameter set as the form body <c>corporate_actions.get_range</c> posts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is upstream's push order (<c>corporate.rs:37-50</c>), which is not the order the
    /// properties are declared in: <c>index</c> leads, <c>stype_in</c> precedes <c>symbols</c>,
    /// <c>allocate_isins</c> and <c>compression</c> sit before the range, and the four optional
    /// filters come last in the order <c>events</c>, <c>countries</c>, <c>exchanges</c>,
    /// <c>security_types</c>. It makes no difference to the API and it makes the rendered body
    /// byte-comparable with upstream's, which is the cheapest way to tell this rendering apart from
    /// a plausible one.
    /// </para>
    /// <para>
    /// <b>The five fields this shares with the other three reference renderers are written out here
    /// too, and that is #55 answering the question <see cref="SecurityMasterGetRangeParams.ToFormParameters"/>
    /// left it.</b> The four bodies do share a contiguous <c>{stype_in, symbols, allocate_isins,
    /// compression}</c> core, with <c>index</c> prepended by two of them. A helper for that core
    /// would still leave each caller assembling around it, so a reader asking "what goes on the
    /// wire for this endpoint" would need two files instead of one — and being readable in one place
    /// is the whole value of these methods, which is also what their per-endpoint tests assert
    /// against. Four explicit lists it is. <see cref="DatabentoDotNet.Historical.SubmitJobParams"/>
    /// keeps its own <c>Boolean</c> helper private for the same reason.
    /// </para>
    /// <para>
    /// For a request with no event, country, exchange or security-type filter and an open range,
    /// the key set is exactly <c>{index, stype_in, symbols, allocate_isins, compression, start}</c>
    /// — six fields, with nothing sent empty.
    /// </para>
    /// </remarks>
    /// <returns>The form fields, in upstream's push order.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Symbols"/> or <see cref="DateTimeRange"/> is left at its type's default value.
    /// <see langword="required"/> forces a caller to assign each property but does not stop them
    /// assigning <see langword="default"/>, and the accessors this reads refuse to render one.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A <see langword="default"/> <see cref="Event"/>, <see cref="Country"/> or
    /// <see cref="SecurityType"/>, or a blank exchange, appears in a filter list.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="Index"/> is not a defined <see cref="CorporateActionIndex"/>.
    /// </exception>
    public IReadOnlyList<KeyValuePair<string, string>> ToFormParameters()
    {
        var parameters = new List<KeyValuePair<string, string>>(11)
        {
            new("index", Index.ToWireString()),
            new("stype_in", StypeIn.ToWireString()),
            new("symbols", Symbols.ToApiString()),

            // Lower case, not bool.ToString()'s `True`/`False`. The difference is invisible in C#
            // and load-bearing on the wire; SecurityMasterGetRangeParams.ToFormParameters writes
            // the trap out in full.
            new("allocate_isins", AllocateIsins ? "true" : "false"),
            new("compression", CorporateActionsClient.RequestCompression),
        };

        parameters.AddRange(DateTimeRange.ToFormParameters());

        if (ReferenceCodeFilter.Render(Events) is { } events)
        {
            parameters.Add(new("events", events));
        }

        if (ReferenceCodeFilter.Render(Countries) is { } countries)
        {
            parameters.Add(new("countries", countries));
        }

        if (ReferenceCodeFilter.Render(Exchanges) is { } exchanges)
        {
            parameters.Add(new("exchanges", exchanges));
        }

        if (ReferenceCodeFilter.Render(SecurityTypes) is { } securityTypes)
        {
            parameters.Add(new("security_types", securityTypes));
        }

        return parameters;
    }
}
