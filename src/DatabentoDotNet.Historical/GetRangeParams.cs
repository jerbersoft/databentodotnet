using System.Globalization;
using DatabentoDotNet.Dbn;

namespace DatabentoDotNet.Historical;

/// <summary>
/// The parameter set <c>timeseries.get_range</c> takes: what to download, over what range, and
/// named in which symbology.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>GetRangeParams</c> (<c>timeseries.rs:166-199</c>). It is
/// <see cref="MetadataQueryParams"/> plus a <see cref="StypeOut"/> — upstream keeps the two types
/// distinct for that one field, and so does this port. #37 found the discrepancy the hard way,
/// while porting <c>symbology.resolve</c>'s <c>From&lt;GetRangeParams&gt;</c> conversion: the
/// billing type's own doc comment used to promise it was the set this endpoint takes, and it never
/// was.
/// </para>
/// <para>
/// <b><see cref="ToQuery"/> is an addition over upstream, not a port of one.</b> There is no
/// <c>From&lt;GetRangeParams&gt; for GetQueryParams</c> anywhere in <c>databento-rs</c>; an
/// upstream caller who wants to price a download builds the billing object by hand. That is the
/// part not worth porting — two hand-built objects that must agree is where a drifted field
/// becomes a wrong quote, and <em>pricing the request you actually send</em> is the whole property
/// <see cref="MetadataQueryParams"/> exists for. The conversion keeps it, at the cost of one
/// method. See PORTING.md §4.
/// </para>
/// <para>
/// <b><c>encoding</c> and <c>compression</c> are not properties, because they are not choices.</b>
/// Upstream hard-codes <c>encoding=dbn</c> and <c>compression=zstd</c> on every request
/// (<c>timeseries.rs:131-134</c>) and so does <see cref="ToFormParameters"/>. This client returns a
/// decoder, so DBN is the only encoding it could ask for; zstd is what makes a multi-gigabyte range
/// a reasonable thing to request at all. A caller who wants CSV wants a different library.
/// </para>
/// <para>
/// <b>Upstream's deprecated per-request <c>upgrade_policy</c> is not ported.</b> It was deprecated
/// in 0.28.0 in favour of the client-level setting, which this library carries as
/// <see cref="HistoricalClient.UpgradePolicy"/>. Porting a field upstream tells its own callers not
/// to use would be fidelity to the wrong thing.
/// </para>
/// </remarks>
public sealed record GetRangeParams
{
    private readonly ulong? _limit;

    /// <summary>The dataset code, for example <c>GLBX.MDP3</c>.</summary>
    public required string Dataset { get; init; }

    /// <summary>The symbols to download.</summary>
    public required Symbols Symbols { get; init; }

    /// <summary>The record schema to download.</summary>
    public required Schema Schema { get; init; }

    /// <summary>
    /// The request range: inclusive start, exclusive end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Probed against the live API rather than inherited.</b> Upstream documents the exclusive
    /// end at <c>timeseries.rs:175</c> and it is correct — but #45 was a documented prior that
    /// turned out false, so #38 asked the server before repeating this one. An <c>ohlcv-1d</c> bar
    /// is stamped at exactly UTC midnight; a one-nanosecond window starting on that instant returns
    /// the bar, and a one-nanosecond window <em>ending</em> on it returns nothing. The endpoint also
    /// refuses <c>start == end</c> with <c>422 data_time_range_start_on_or_after_end</c>, exactly as
    /// the three billing endpoints do — which is the answer an endpoint reading <c>end</c> as
    /// inclusive could not give, since that is how such an endpoint would spell a single instant.
    /// </para>
    /// <para>
    /// Upstream adds that the filter is on <c>ts_recv</c> where the schema has one and on
    /// <c>ts_event</c> otherwise. That half is <em>not</em> probed here: <c>ohlcv</c> schemas carry
    /// no <c>ts_recv</c>, so the measurement above pins <c>ts_event</c> only.
    /// </para>
    /// </remarks>
    public required DateTimeRange DateTimeRange { get; init; }

    /// <summary>
    /// The symbology <see cref="Symbols"/> is expressed in. Defaults to
    /// <see cref="SType.RawSymbol"/>, as upstream's builder does.
    /// </summary>
    public SType StypeIn { get; init; } = SType.RawSymbol;

    /// <summary>
    /// The symbology the downloaded records name instruments in. Defaults to
    /// <see cref="SType.InstrumentId"/>, as upstream's builder does.
    /// </summary>
    /// <remarks>
    /// The field <see cref="MetadataQueryParams"/> has no equivalent of, and the reason these are
    /// two types. It cannot affect a price, a record count or a billable size — it names output,
    /// not volume — which is why <see cref="ToQuery"/> drops it rather than carrying it into a
    /// request that would ignore it.
    /// </remarks>
    public SType StypeOut { get; init; } = SType.InstrumentId;

    /// <summary>
    /// The maximum number of records. Defaults to no limit, in which case the field is omitted from
    /// the request rather than sent empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Zero is refused here because the API's answer to it is self-contradictory.</b> Upstream's
    /// type is <c>Option&lt;NonZeroU64&gt;</c> and C# has no non-zero integer, so the constraint has
    /// to live in an initializer either way. What #38 probed is <em>why</em> it is worth enforcing,
    /// and it is not the reason that was assumed: sending <c>limit=0</c> does not request nothing.
    /// The response body is <b>byte-identical</b> to the one the same request returns with no
    /// <c>limit</c> at all — same records, same metadata — but it additionally carries
    /// <c>X-Warning: No data found for the request you submitted.</c>
    /// </para>
    /// <para>
    /// So the server reads <c>limit=0</c> as "no limit" on the data path and as "zero records" on
    /// the warning path, and the response's header contradicts its own body.
    /// <see cref="HistoricalClient"/> logs <c>X-Warning</c> faithfully, which means a caller who
    /// passed zero would see "No data found" logged beside a stream that has data. Refusing the
    /// value at construction is the only place that contradiction can be stopped.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero.</exception>
    public ulong? Limit
    {
        get => _limit;
        init
        {
            if (value == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    "A limit of zero is not a limit; leave it unset instead. The API accepts it, "
                    + "returns the data anyway, and warns that it found none — see this property's "
                    + "documentation.");
            }

            _limit = value;
        }
    }

    /// <summary>
    /// Narrows this to the parameters the three <c>metadata.*</c> billing endpoints take, so a
    /// caller can price exactly the request they are about to send.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Drops <see cref="StypeOut"/> and nothing else. That is not a lossy conversion in any sense
    /// that matters to a price: the billing endpoints neither take the field nor could use it, and
    /// upstream's own billing type has no room for it (<c>metadata.rs:328-346</c>).
    /// </para>
    /// <para>
    /// The conversion runs in this direction only. Widening a billing query back into a download
    /// would have to invent a <see cref="StypeOut"/>, and <see cref="ResolveParams.FromQuery(MetadataQueryParams, DatabentoDotNet.Dbn.SType)"/>
    /// documents what inventing that value silently does.
    /// </para>
    /// </remarks>
    /// <returns>The same request, priced rather than downloaded.</returns>
    public MetadataQueryParams ToQuery() =>
        new()
        {
            Dataset = Dataset,
            Symbols = Symbols,
            Schema = Schema,
            DateTimeRange = DateTimeRange,
            StypeIn = StypeIn,
            Limit = Limit,
        };

    /// <summary>
    /// Renders this parameter set as the form body <c>timeseries.get_range</c> posts.
    /// </summary>
    /// <remarks>
    /// The order is upstream's push order (<c>timeseries.rs:128-138</c>), which is not the order
    /// the properties are declared in: <c>encoding</c> and <c>compression</c> sit between
    /// <c>schema</c> and the stypes, and <c>stype_in</c> precedes <c>symbols</c>. It makes no
    /// difference to the API and it makes the rendered body byte-comparable with upstream's, which
    /// is the cheapest way to tell this rendering apart from a plausible one — the same argument
    /// <see cref="MetadataQueryParams.ToFormParameters"/> makes for its own order.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Symbols"/> or <see cref="DateTimeRange"/> is left at its type's default value.
    /// <see langword="required"/> forces a caller to assign each property but does not stop them
    /// assigning <see langword="default"/>, and the accessors this reads refuse to render one.
    /// </exception>
    /// <returns>The form fields, in upstream's push order.</returns>
    public IReadOnlyList<KeyValuePair<string, string>> ToFormParameters()
    {
        var parameters = new List<KeyValuePair<string, string>>(10)
        {
            new("dataset", Dataset),
            new("schema", Schema.ToWireString()),
            new("encoding", TimeseriesClient.RequestEncoding),
            new("compression", TimeseriesClient.RequestCompression),
            new("stype_in", StypeIn.ToWireString()),
            new("stype_out", StypeOut.ToWireString()),
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
