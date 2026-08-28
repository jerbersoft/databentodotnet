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
    /// date. This is the library's own <see cref="DatabentoDotNet.Historical.DateRange"/>, so its
    /// usual half-open contract applies here too: an inclusive
    /// <see cref="DatabentoDotNet.Historical.DateRange.Start"/> and an exclusive
    /// <see cref="DatabentoDotNet.Historical.DateRange.End"/>. Whether the
    /// <c>get_dataset_condition</c> endpoint itself treats <c>end_date</c> as inclusive or
    /// exclusive on the wire was deferred here, and
    /// <see href="https://github.com/jerbersoft/databentodotnet/issues/44">#44</see> answered it
    /// against the real API: <b>inclusive</b>. So a half-open range asking for n days is answered
    /// for n + 1, and <c>DateRange.OnDay(d)</c> returns both <c>d</c> and <c>d + 1</c>. Upstream
    /// annotates the inclusive end on this one field (<c>metadata.rs:285</c>) and half-open
    /// everywhere else; this port carried the shared type in without absorbing the difference.
    /// Tracked as <see href="https://github.com/jerbersoft/databentodotnet/issues/45">#45</see>,
    /// which owns the fix and the choice of shape.
    /// </summary>
    public DateRange? DateRange { get; init; }

    /// <summary>
    /// Renders this parameter set as the query string the <c>get_dataset_condition</c> endpoint's
    /// GET request carries.
    /// </summary>
    /// <remarks>
    /// The order matches upstream: <c>dataset</c> is always sent first
    /// (<c>metadata.rs:125-127</c>), and <c>start_date</c>/<c>end_date</c> follow, rendered by
    /// <see cref="DateRange.ToStartEndDateParameters"/>, only when <see cref="DateRange"/> is set
    /// (<c>metadata.rs:128-130</c>). That helper is shared with
    /// <see cref="MetadataClient.ListDatasetsAsync"/> so the two call sites cannot drift apart.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <see cref="DateRange"/> is set to a default
    /// <see cref="DatabentoDotNet.Historical.DateRange"/> value — reachable even though every
    /// factory on that type rejects one, because <c>new DateRange()</c> uses the struct's implicit
    /// parameterless constructor, which C# cannot suppress.
    /// <see cref="DatabentoDotNet.Historical.DateRange.StartIsoDate"/> and
    /// <see cref="DatabentoDotNet.Historical.DateRange.EndIsoDate"/> each refuse to render one.
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
            parameters.AddRange(DatabentoDotNet.Historical.DateRange.ToStartEndDateParameters(dateRange));
        }

        return parameters;
    }
}
