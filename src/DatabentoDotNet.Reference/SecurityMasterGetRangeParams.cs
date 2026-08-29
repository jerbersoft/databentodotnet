using DatabentoDotNet.Dbn;

namespace DatabentoDotNet.Reference;

/// <summary>
/// The parameter set <c>security_master.get_range</c> takes: which symbols, over what range of
/// which timestamp, optionally narrowed to a set of countries or security types.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>security::GetRangeParams</c> (<c>security.rs:95-130</c>). Named for its
/// endpoint rather than as a bare <c>GetRangeParams</c>, for the reason
/// <see cref="AdjustmentFactorsGetRangeParams"/> records: upstream can reuse that name in three
/// modules of one crate and C# cannot reuse it in one namespace.
/// </para>
/// <para>
/// <b>This differs from <see cref="SecurityMasterGetLastParams"/> by exactly three form fields —
/// <c>index</c>, <c>start</c> and <c>end</c> — and the two types are deliberately unrelated.</b>
/// Making one inherit the other would let a caller hand this to
/// <see cref="SecurityMasterClient.GetLastAsync"/>, where the range it names would be silently
/// dropped rather than refused. Upstream keeps them as two independent structs for its own reasons;
/// the reason to keep them so here is that the compiler then does the refusing.
/// </para>
/// <para>
/// <b><c>compression</c> is not a property, because it is not a choice.</b> Upstream hard-codes
/// <c>compression=zstd</c> on both of these endpoints (<c>security.rs:40</c>, <c>:70</c>) and so
/// does <see cref="ToFormParameters"/>. The response handler requires the frame — an uncompressed
/// body would not parse — so a caller who could set it could only break the request.
/// </para>
/// </remarks>
public sealed record SecurityMasterGetRangeParams
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
    /// Which timestamp <see cref="DateTimeRange"/> applies to. Defaults to
    /// <see cref="SecurityMasterIndex.TsEffective"/>, as upstream's builder does.
    /// </summary>
    /// <remarks>
    /// <b>The field this endpoint has and <c>get_last</c> does not.</b> It changes which rows come
    /// back, not their order: upstream also sorts its buffered response by it
    /// (<c>security.rs:50-53</c>) and <see cref="SecurityMasterClient.GetRangeAsync"/> streams, so
    /// the sort is not performed here. See <see cref="SecurityMasterIndex"/>.
    /// </remarks>
    public SecurityMasterIndex Index { get; init; } = SecurityMasterIndex.TsEffective;

    /// <summary>
    /// The symbology <see cref="Symbols"/> is expressed in. Defaults to
    /// <see cref="SType.RawSymbol"/>, as upstream's builder does.
    /// </summary>
    public SType StypeIn { get; init; } = SType.RawSymbol;

    /// <summary>
    /// The countries to filter for, or <see langword="null"/> to include every country.
    /// </summary>
    /// <remarks>
    /// An empty sequence means the same thing as <see langword="null"/>: the parameter is left out.
    /// A <see langword="default"/> <see cref="Country"/> in the list is a caller mistake and is
    /// refused — see <see cref="ReferenceCodeFilter.Render{T}"/>.
    /// </remarks>
    public IReadOnlyList<Country>? Countries { get; init; }

    /// <summary>
    /// The security types to filter for, or <see langword="null"/> to include every type.
    /// </summary>
    /// <remarks>An empty sequence behaves as <see langword="null"/>, as with <see cref="Countries"/>.</remarks>
    public IReadOnlyList<SecurityType>? SecurityTypes { get; init; }

    /// <summary>
    /// Whether the request may allocate new ISINs on an ISIN-limited plan. Defaults to
    /// <see langword="true"/>, as upstream's builder does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A billing consequence hiding in a default, and it bites harder here than anywhere else in
    /// this library.</b> This is the endpoint whose whole purpose is to return identifiers, so a
    /// request for symbols the plan has not seen before is exactly the request that can create new
    /// allocations against an ISIN-limited entitlement (<c>security.rs:126-130</c>). Set it
    /// <see langword="false"/> and the API drops the rows that would have done so rather than
    /// returning them — fewer rows, no allocation. The default is upstream's and is kept, because a
    /// client that silently returned fewer rows than upstream for the same parameters would be the
    /// worse surprise.
    /// </para>
    /// <para>
    /// <b>No test that reaches the real API may leave this <see langword="true"/> without going
    /// through #57's gate.</b> That is a rule about spending someone's entitlement, not a style
    /// preference, and it is the one thing about this property that is not merely documentation.
    /// </para>
    /// </remarks>
    public bool AllocateIsins { get; init; } = true;

    /// <summary>
    /// Renders this parameter set as the form body <c>security_master.get_range</c> posts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is upstream's push order (<c>security.rs:36-46</c>), which is not the order the
    /// properties are declared in: <c>index</c> leads, <c>stype_in</c> precedes <c>symbols</c>,
    /// <c>allocate_isins</c> and <c>compression</c> sit before the range, and the two optional
    /// filters come last. It makes no difference to the API and it makes the rendered body
    /// byte-comparable with upstream's, which is the cheapest way to tell this rendering apart from
    /// a plausible one.
    /// </para>
    /// <para>
    /// <b>The four fields shared with <see cref="SecurityMasterGetLastParams.ToFormParameters"/>
    /// are written out in both places rather than shared.</b> They are also written out a third
    /// time in <see cref="AdjustmentFactorsGetRangeParams.ToFormParameters"/>, which is upstream's
    /// arrangement (<c>security.rs:36-40</c>, <c>:66-70</c>, <c>adjustment.rs:32-36</c>) and is
    /// kept: a shared renderer used by two of the three endpoints would be worse than one
    /// used by none or by all, and #55 — which arrived with the fourth body — answered the question
    /// by writing it out again. <see cref="CorporateActionsGetRangeParams.ToFormParameters"/>
    /// carries the reasoning.
    /// </para>
    /// <para>
    /// For a request with no country or security-type filter and an open range, the key set is
    /// exactly <c>{index, stype_in, symbols, allocate_isins, compression, start}</c> — six fields,
    /// with nothing sent empty.
    /// </para>
    /// </remarks>
    /// <returns>The form fields, in upstream's push order.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Symbols"/> or <see cref="DateTimeRange"/> is left at its type's default value.
    /// <see langword="required"/> forces a caller to assign each property but does not stop them
    /// assigning <see langword="default"/>, and the accessors this reads refuse to render one.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A <see langword="default"/> <see cref="Country"/> or <see cref="SecurityType"/> appears in a
    /// filter list.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="Index"/> is not a defined <see cref="SecurityMasterIndex"/>.
    /// </exception>
    public IReadOnlyList<KeyValuePair<string, string>> ToFormParameters()
    {
        var parameters = new List<KeyValuePair<string, string>>(9)
        {
            new("index", Index.ToWireString()),
            new("stype_in", StypeIn.ToWireString()),
            new("symbols", Symbols.ToApiString()),

            // Not bool.ToString(), which is `True` and `False`. Upstream's `bool::to_string` is
            // lower case, and the difference is invisible in C# and load-bearing on the wire —
            // the same trap SubmitJobParams.Boolean documents.
            new("allocate_isins", AllocateIsins ? "true" : "false"),
            new("compression", SecurityMasterClient.RequestCompression),
        };

        parameters.AddRange(DateTimeRange.ToFormParameters());

        if (ReferenceCodeFilter.Render(Countries) is { } countries)
        {
            parameters.Add(new("countries", countries));
        }

        if (ReferenceCodeFilter.Render(SecurityTypes) is { } securityTypes)
        {
            parameters.Add(new("security_types", securityTypes));
        }

        return parameters;
    }
}
