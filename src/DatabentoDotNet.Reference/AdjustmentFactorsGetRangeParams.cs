using DatabentoDotNet.Dbn;

namespace DatabentoDotNet.Reference;

/// <summary>
/// The parameter set <c>adjustment_factors.get_range</c> takes: which symbols, over what range,
/// optionally narrowed to a set of countries or security types.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>adjustment::GetRangeParams</c> (<c>adjustment.rs:56-88</c>). Named for its
/// endpoint rather than as a bare <c>GetRangeParams</c>: upstream can reuse that name in three
/// modules of one crate, C# cannot reuse it in one namespace, and
/// <see cref="DatabentoDotNet.Historical.GetRangeParams"/> has already claimed the short spelling
/// for <c>timeseries.get_range</c>.
/// </para>
/// <para>
/// <b><c>compression</c> is not a property, because it is not a choice.</b> Upstream hard-codes
/// <c>compression=zstd</c> on this endpoint (<c>adjustment.rs:36</c>) and so does
/// <see cref="ToFormParameters"/>. The response handler requires the frame — an uncompressed body
/// would not parse — so a caller who could set it could only break the request.
/// <see cref="DatabentoDotNet.Historical.GetRangeParams"/> makes the same call for the same reason.
/// </para>
/// <para>
/// <b><see cref="Countries"/> and <see cref="SecurityTypes"/> are omitted when empty rather than
/// sent empty.</b> Upstream's <c>AddToForm</c> impls push nothing for an empty
/// <c>Vec</c> (<c>reference.rs:266-296</c>), and <c>countries=</c> is a different request from no
/// <c>countries</c> at all — the same distinction <see cref="ReferenceDateTimeRange"/> draws for an
/// absent <c>end</c>.
/// </para>
/// </remarks>
public sealed record AdjustmentFactorsGetRangeParams
{
    /// <summary>The symbols to filter for.</summary>
    public required Symbols Symbols { get; init; }

    /// <summary>The request range: inclusive start, optional exclusive end.</summary>
    /// <remarks>
    /// Filters on <c>index</c>, per upstream (<c>adjustment.rs:60</c>, <c>:63</c>). Omit the end and
    /// the response runs to the end of the data — see <see cref="ReferenceDateTimeRange"/>, which
    /// also records that the exclusive end is documented rather than probed.
    /// </remarks>
    public required ReferenceDateTimeRange DateTimeRange { get; init; }

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
    /// <b>A billing consequence hiding in a default.</b> Left at <see langword="true"/>, a request
    /// for symbols the plan has not seen before can create new allocations against an ISIN-limited
    /// entitlement; set it <see langword="false"/> and the API drops the rows that would have done
    /// so rather than returning them. The default is upstream's and is kept, because a client that
    /// silently returned fewer rows than upstream for the same parameters would be the worse
    /// surprise — but it is the reason this property is documented at length rather than listed.
    /// </para>
    /// <para>
    /// Unprobed here, like every other billing behaviour of these three endpoints; #57 owns the
    /// gated request that can measure it.
    /// </para>
    /// </remarks>
    public bool AllocateIsins { get; init; } = true;

    /// <summary>
    /// Renders this parameter set as the form body <c>adjustment_factors.get_range</c> posts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is upstream's push order (<c>adjustment.rs:32-41</c>), which is not the order the
    /// properties are declared in: <c>stype_in</c> precedes <c>symbols</c>, <c>allocate_isins</c>
    /// and <c>compression</c> sit before the range, and the two optional filters come last. It
    /// makes no difference to the API and it makes the rendered body byte-comparable with
    /// upstream's, which is the cheapest way to tell this rendering apart from a plausible one —
    /// the same argument <see cref="DatabentoDotNet.Historical.GetRangeParams.ToFormParameters"/>
    /// makes for its own order.
    /// </para>
    /// <para>
    /// For a request with no country or security-type filter and an open range, the key set is
    /// exactly <c>{stype_in, symbols, allocate_isins, compression, start}</c> — five fields, with
    /// nothing sent empty.
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
    public IReadOnlyList<KeyValuePair<string, string>> ToFormParameters()
    {
        var parameters = new List<KeyValuePair<string, string>>(8)
        {
            new("stype_in", StypeIn.ToWireString()),
            new("symbols", Symbols.ToApiString()),

            // Not bool.ToString(), which is `True` and `False`. Upstream's `bool::to_string` is
            // lower case, and the difference is invisible in C# and load-bearing on the wire —
            // the same trap SubmitJobParams.Boolean documents.
            new("allocate_isins", AllocateIsins ? "true" : "false"),
            new("compression", AdjustmentFactorsClient.RequestCompression),
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
