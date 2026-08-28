using System.Globalization;
using DatabentoDotNet.Dbn;

namespace DatabentoDotNet.Historical;

/// <summary>
/// The parameter set the three <c>metadata.*</c> billing endpoints take.
/// </summary>
/// <remarks>
/// <para>
/// <b>One type, deliberately, and not named for billing.</b> Upstream declares this once as
/// <c>GetQueryParams</c> and aliases it three times (<c>metadata.rs:348-359</c>). Sharing it
/// matters more than the name does: a caller who prices a request with
/// <see cref="MetadataClient.GetCostAsync"/> and then sends a <em>different</em> request has been
/// badly served by the API surface, and a shared type is what makes sending the same one the path
/// of least resistance.
/// </para>
/// <para>
/// <b>This is <em>not</em> the full parameter set <c>timeseries.get_range</c> takes, and the
/// sentence above used to claim it was.</b> #37 found the discrepancy while porting
/// <c>symbology.resolve</c>'s <c>From&lt;GetRangeParams&gt;</c> conversion: upstream keeps two
/// distinct types, and <c>GetRangeParams</c> (<c>timeseries.rs:166-199</c>) carries a
/// <c>stype_out</c> this one has no field for, posting it together with <c>encoding=dbn</c> and
/// <c>compression=zstd</c> (<c>timeseries.rs:128-138</c>) — three fields the billing endpoints
/// neither take nor send (<c>metadata.rs:462-471</c>). None of the three affects a price, a record
/// count or a billable size, so their absence here is correct rather than an omission; what was
/// wrong was the promise that #38 could send this type as-is.
/// </para>
/// <para>
/// <b>#38 closed it with a second type.</b> <see cref="GetRangeParams"/> carries the
/// <c>stype_out</c>, as upstream's own <c>GetRangeParams</c> does, and
/// <see cref="GetRangeParams.ToQuery"/> narrows one back to this type — so a caller still prices
/// exactly the request they are about to send, which is the property this type was built for. The
/// rejected alternative was widening this type with a <c>StypeOut</c> the billing renderer would
/// drop: an inert public field on the type whose whole job is to describe what bills. That
/// conversion is an addition over upstream, which has no equivalent and leaves its callers to
/// build the second object by hand.
/// </para>
/// <para>
/// Meanwhile <see cref="ResolveParams.FromQuery(MetadataQueryParams, DatabentoDotNet.Dbn.SType)"/>
/// takes the missing <c>stype_out</c> as an explicit argument rather than defaulting it, because
/// there is no default for it that is right — see that method for what the wrong one would
/// silently do. Callers holding a <see cref="GetRangeParams"/> should prefer
/// <see cref="ResolveParams.FromQuery(GetRangeParams)"/>, which reads the value instead of asking
/// for it.
/// </para>
/// <para>
/// <b><see cref="Limit"/> is <c>ulong?</c> where upstream is <c>Option&lt;NonZeroU64&gt;</c>.</b>
/// C# has no non-zero integer type, so the constraint moves into the initializer, which throws
/// rather than sending <c>limit=0</c>.
/// </para>
/// <para>
/// <b>That sentence used to guess at what the API does with a zero, and the guess was wrong.</b>
/// It read "a value the API would read as a limit rather than as its absence". #38 asked instead:
/// these three endpoints <em>reject</em> it, with <c>422</c> and a validation body saying
/// <c>Input should be greater than 0</c>. So the initializer is not preventing a silently
/// misread request; it is turning a round trip into a compile-site error.
/// </para>
/// <para>
/// <b>And <c>timeseries.get_range</c> does not agree with them about it</b>, which is the part
/// worth knowing. The same <c>limit=0</c> that fails validation here is accepted there, returns a
/// body byte-identical to the one with no limit at all, and carries a <c>X-Warning</c> claiming no
/// data was found. See <see cref="GetRangeParams.Limit"/>. Refusing zero in both types is what
/// keeps a request that was priced here and sent there from behaving differently at each end.
/// </para>
/// <para>
/// <b>"Query" in this type's name means a data query, not a URL query string.</b> Its siblings'
/// <c>ToQueryParameters()</c> render onto the URL query string, but this type's
/// <see cref="ToFormParameters"/> renders onto a form body instead — upstream's own
/// <c>GetQueryParams</c> already carries this same double meaning, and the name is kept for
/// consistency with it rather than renamed away from it.
/// </para>
/// </remarks>
public sealed record MetadataQueryParams
{
    private readonly ulong? _limit;

    /// <summary>The dataset code, for example <c>XNAS.ITCH</c>.</summary>
    public required string Dataset { get; init; }

    /// <summary>The symbols to query.</summary>
    public required Symbols Symbols { get; init; }

    /// <summary>The record schema to query.</summary>
    public required Schema Schema { get; init; }

    /// <summary>The request range: inclusive start, exclusive end.</summary>
    public required DateTimeRange DateTimeRange { get; init; }

    /// <summary>
    /// The symbology type of <see cref="Symbols"/>. Defaults to <see cref="SType.RawSymbol"/>, and
    /// is sent on every request even when left at the default — upstream pushes it unconditionally
    /// (<c>metadata.rs:466</c>).
    /// </summary>
    public SType StypeIn { get; init; } = SType.RawSymbol;

    /// <summary>
    /// The maximum number of records. Defaults to no limit, in which case the field is omitted
    /// from the request rather than sent empty.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero.</exception>
    public ulong? Limit
    {
        get => _limit;
        init
        {
            if (value == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), value, "A limit of zero is not a limit; leave it unset instead.");
            }

            _limit = value;
        }
    }

    /// <summary>
    /// Renders this parameter set as the form body the three billing endpoints post.
    /// </summary>
    /// <remarks>
    /// The order is upstream's push order (<c>metadata.rs:462-471</c>), which is not the order the
    /// properties are declared in: <c>stype_in</c> precedes <c>symbols</c>. It makes no difference
    /// to the API and it makes the rendered body byte-comparable with upstream's, which is the
    /// cheapest way to tell this rendering apart from a plausible one.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Symbols"/> or <see cref="DateTimeRange"/> is left at its type's default value.
    /// <see langword="required"/> forces a caller to assign each property but does not stop them
    /// assigning <see langword="default"/>, and
    /// <see cref="DatabentoDotNet.Symbols.ToApiString"/> and
    /// <see cref="DatabentoDotNet.Historical.DateTimeRange.StartUnixNanoseconds"/>/
    /// <see cref="DatabentoDotNet.Historical.DateTimeRange.EndUnixNanoseconds"/> each refuse to
    /// render one, the same way their own accessors document.
    /// </exception>
    /// <returns>The form fields, in upstream's push order.</returns>
    public IReadOnlyList<KeyValuePair<string, string>> ToFormParameters()
    {
        var parameters = new List<KeyValuePair<string, string>>(7)
        {
            new("dataset", Dataset),
            new("schema", Schema.ToWireString()),
            new("stype_in", StypeIn.ToWireString()),
            new("symbols", Symbols.ToApiString()),
            new("start", DateTimeRange.StartUnixNanoseconds.ToString(CultureInfo.InvariantCulture)),
            new("end", DateTimeRange.EndUnixNanoseconds.ToString(CultureInfo.InvariantCulture)),
        };

        if (Limit is { } limit)
        {
            parameters.Add(new("limit", limit.ToString(CultureInfo.InvariantCulture)));
        }

        return parameters;
    }
}
