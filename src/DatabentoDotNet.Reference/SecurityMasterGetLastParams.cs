using DatabentoDotNet.Dbn;

namespace DatabentoDotNet.Reference;

/// <summary>
/// The parameter set <c>security_master.get_last</c> takes: which symbols, optionally narrowed to a
/// set of countries or security types. There is no range, and no index to apply one to.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>security::GetLastParams</c> (<c>security.rs:132-157</c>).
/// </para>
/// <para>
/// <b>This is <see cref="SecurityMasterGetRangeParams"/> minus <c>index</c>, <c>start</c> and
/// <c>end</c>, and the two types share no inheritance.</b> The endpoint returns the latest record
/// per security, so there is nothing for a range to select and nothing for an index to filter on —
/// a request carrying one would be a request the API does not have. If this type derived from the
/// other, a caller could hand a fully specified range to
/// <see cref="SecurityMasterClient.GetLastAsync"/> and have it silently discarded; separate types
/// make that a compile error instead. The cost is four properties written twice, which the tests
/// treat as a claim to check rather than as an invariant to trust.
/// </para>
/// <para>
/// <b><c>compression</c> is not a property here either</b>, for the reason
/// <see cref="SecurityMasterGetRangeParams"/> gives.
/// </para>
/// </remarks>
public sealed record SecurityMasterGetLastParams
{
    /// <summary>The symbols to filter for.</summary>
    public required Symbols Symbols { get; init; }

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
    /// The same billing consequence, and the same rule about tests that reach the real API, as
    /// <see cref="SecurityMasterGetRangeParams.AllocateIsins"/> — which carries both in full.
    /// </remarks>
    public bool AllocateIsins { get; init; } = true;

    /// <summary>
    /// Renders this parameter set as the form body <c>security_master.get_last</c> posts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is upstream's push order (<c>security.rs:66-73</c>): <c>stype_in</c> precedes
    /// <c>symbols</c>, <c>allocate_isins</c> and <c>compression</c> follow, and the two optional
    /// filters come last.
    /// </para>
    /// <para>
    /// For a request with no country or security-type filter, the key set is exactly
    /// <c>{stype_in, symbols, allocate_isins, compression}</c> — four fields, with nothing sent
    /// empty and no range of any spelling.
    /// </para>
    /// </remarks>
    /// <returns>The form fields, in upstream's push order.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Symbols"/> is left at its type's default value. <see langword="required"/> forces
    /// a caller to assign it but does not stop them assigning <see langword="default"/>, and
    /// <see cref="Symbols.ToApiString"/> refuses to render one.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A <see langword="default"/> <see cref="Country"/> or <see cref="SecurityType"/> appears in a
    /// filter list.
    /// </exception>
    public IReadOnlyList<KeyValuePair<string, string>> ToFormParameters()
    {
        var parameters = new List<KeyValuePair<string, string>>(6)
        {
            new("stype_in", StypeIn.ToWireString()),
            new("symbols", Symbols.ToApiString()),

            // Lower case, not bool.ToString(). See SecurityMasterGetRangeParams.ToFormParameters.
            new("allocate_isins", AllocateIsins ? "true" : "false"),
            new("compression", SecurityMasterClient.RequestCompression),
        };

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
