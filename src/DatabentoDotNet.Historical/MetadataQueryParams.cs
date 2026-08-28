using System.Globalization;
using DatabentoDotNet.Dbn;

namespace DatabentoDotNet.Historical;

/// <summary>
/// The parameter set the three <c>metadata.*</c> billing endpoints take — and the same set
/// <c>timeseries.get_range</c> takes.
/// </summary>
/// <remarks>
/// <para>
/// <b>One type, deliberately, and not named for billing.</b> Upstream declares this once as
/// <c>GetQueryParams</c> and aliases it three times (<c>metadata.rs:348-359</c>). Sharing it
/// matters more than the name does: a caller who prices a request with
/// <see cref="MetadataClient"/>.<c>GetCostAsync</c> and then sends a <em>different</em> request
/// has been badly served by the API surface, and a shared type is what makes sending the same one
/// the path of least resistance. <c>GetCostAsync</c> itself stays plain markup rather than a full
/// <c>&lt;see cref&gt;</c> here — <see cref="MetadataClient"/> now exists, but the method does not
/// yet; it is the three billing endpoints' own task, not this type's. #38 uses this type for
/// <c>timeseries.get_range</c>.
/// </para>
/// <para>
/// <b><see cref="Limit"/> is <c>ulong?</c> where upstream is <c>Option&lt;NonZeroU64&gt;</c>.</b>
/// C# has no non-zero integer type, so the constraint moves into the initializer, which throws
/// rather than silently sending <c>limit=0</c> — a value the API would read as a limit rather than
/// as its absence.
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
