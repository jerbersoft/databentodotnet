namespace DatabentoDotNet.Historical;

/// <summary>
/// The parameters for <c>metadata.get_dataset_condition</c>, which reports data availability and
/// quality for a dataset.
/// </summary>
/// <remarks>
/// Port of upstream's <c>GetDatasetConditionParams</c> (<c>metadata.rs:280-289</c>).
/// </remarks>
public sealed record GetDatasetConditionParams
{
    /// <summary>The dataset code, for example <c>XNAS.ITCH</c>.</summary>
    public required string Dataset { get; init; }

    /// <summary>
    /// The UTC date range to report on, or <see langword="null"/> to report on every available
    /// date. This is the library's own <see cref="DatabentoDotNet.Historical.DateRange"/> and its
    /// usual half-open contract holds: an inclusive
    /// <see cref="DatabentoDotNet.Historical.DateRange.Start"/>, an exclusive
    /// <see cref="DatabentoDotNet.Historical.DateRange.End"/>, and n days requested is n details
    /// returned. <c>DateRange.OnDay(d)</c> reports on <c>d</c> alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The endpoint itself disagrees, and the difference is absorbed in
    /// <see cref="ToQueryParameters"/>.</b> <c>get_dataset_condition</c> reads <c>end_date</c> as
    /// <em>inclusive</em> — verified against the real API by
    /// <see href="https://github.com/jerbersoft/databentodotnet/issues/44">#44</see>, which is the
    /// question this property's doc comment used to defer. So the renderer sends the day before
    /// <see cref="DatabentoDotNet.Historical.DateRange.End"/>, via
    /// <see cref="DatabentoDotNet.Historical.DateRange.ToInclusiveEndDateParameters"/>, and the
    /// caller's half-open range means here what it means everywhere else
    /// (<see href="https://github.com/jerbersoft/databentodotnet/issues/45">#45</see>).
    /// </para>
    /// <para>
    /// <b>This diverges from every other Databento client, deliberately.</b> Upstream's
    /// <c>DateRange</c> is half-open too — <c>From&lt;RangeInclusive&gt;</c> normalizes with
    /// <c>next_day()</c> (<c>historical.rs:72-79</c>) — and its one
    /// <c>AddToQuery&lt;DateRange&gt;</c> sends <c>end</c> verbatim to every endpoint, so
    /// <c>databento-rs</c> carries the identical off-by-one and documents the consequence at this
    /// field instead of correcting it (<c>metadata.rs:285</c>). <c>databento-cpp</c> offers no
    /// opinion at all: its <c>DateRange</c> is a pair of raw strings the caller writes out.
    /// Correcting it here is the same call this library already made about empty ranges, about
    /// <c>decimal</c> over <c>f64</c>, and about a sub-day <c>Spanning</c> — and it loses nothing,
    /// because a caller who genuinely wants <c>d</c> and <c>d + 1</c> writes
    /// <c>DateRange.Including(d, d.PlusDays(1))</c>.
    /// </para>
    /// <para>
    /// <b>The rejected alternative was a second, closed-range type for this one endpoint</b>, so
    /// the difference would sit in the caller's source rather than in a renderer. It was rejected
    /// on price: a public type every caller must learn to choose between, permanently, to describe
    /// one server's reading of one parameter on one endpoint. What settled it was probing
    /// <c>metadata.list_datasets</c>, the only other endpoint taking this type today: upstream
    /// documents nothing about its end (<c>metadata.rs:41-50</c>) and against the real API it is
    /// genuinely half-open. So the difference belongs to <em>this endpoint</em>, not to the
    /// library's model of a date range, and it is rendered where it belongs.
    /// </para>
    /// </remarks>
    public DateRange? DateRange { get; init; }

    /// <summary>
    /// Renders this parameter set as the query string the <c>get_dataset_condition</c> endpoint's
    /// GET request carries.
    /// </summary>
    /// <remarks>
    /// The order matches upstream: <c>dataset</c> is always sent first
    /// (<c>metadata.rs:125-127</c>), and <c>start_date</c>/<c>end_date</c> follow only when
    /// <see cref="DateRange"/> is set (<c>metadata.rs:128-130</c>).
    /// <para>
    /// The renderer is <see cref="DateRange.ToInclusiveEndDateParameters"/>, <em>not</em> the
    /// <see cref="DateRange.ToExclusiveEndDateParameters"/> that
    /// <see cref="MetadataClient.ListDatasetsAsync"/> uses — this endpoint reads <c>end_date</c>
    /// as inclusive and that one does not. Both live side by side on
    /// <see cref="DatabentoDotNet.Historical.DateRange"/> so the difference is one line of one
    /// file rather than an arithmetic adjustment buried in a parameter list here; see the
    /// <see cref="DateRange"/> property above for why the conversion happens at all.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <see cref="DateRange"/> is set to a default
    /// <see cref="DatabentoDotNet.Historical.DateRange"/> value — reachable even though every
    /// factory on that type rejects one, because <c>new DateRange()</c> uses the struct's implicit
    /// parameterless constructor, which C# cannot suppress.
    /// <see cref="DatabentoDotNet.Historical.DateRange.ToInclusiveEndDateParameters"/> guards
    /// against one explicitly rather than leaning on
    /// <see cref="DatabentoDotNet.Historical.DateRange.StartIsoDate"/> being evaluated first, since
    /// it formats <see cref="DatabentoDotNet.Historical.DateRange.End"/> minus a day rather than
    /// reading <see cref="DatabentoDotNet.Historical.DateRange.EndIsoDate"/>.
    /// </exception>
    /// <returns>The query parameters, in upstream's push order.</returns>
    public IReadOnlyList<KeyValuePair<string, string>> ToQueryParameters()
    {
        var parameters = new List<KeyValuePair<string, string>>(3)
        {
            new("dataset", Dataset),
        };

        if (DateRange is { } dateRange)
        {
            parameters.AddRange(DatabentoDotNet.Historical.DateRange.ToInclusiveEndDateParameters(dateRange));
        }

        return parameters;
    }
}
