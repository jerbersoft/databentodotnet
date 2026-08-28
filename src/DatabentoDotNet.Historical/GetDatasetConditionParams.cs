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
    /// The UTC date range to report on, with an inclusive start date and an inclusive end date, or
    /// <see langword="null"/> to report on every available date.
    /// </summary>
    public DateRange? DateRange { get; init; }

    /// <summary>
    /// Renders this parameter set as the query string the <c>get_dataset_condition</c> endpoint's
    /// GET request carries.
    /// </summary>
    /// <remarks>
    /// The order matches upstream: <c>dataset</c> is always sent first
    /// (<c>metadata.rs:125-127</c>), and <c>start_date</c>/<c>end_date</c> follow, from
    /// <see cref="DateRange.StartIsoDate"/>/<see cref="DateRange.EndIsoDate"/>, only when
    /// <see cref="DateRange"/> is set (<c>metadata.rs:128-130</c>).
    /// </remarks>
    /// <returns>The query parameters, in upstream's push order.</returns>
    public IReadOnlyList<KeyValuePair<string, string>> ToQueryParameters()
    {
        var parameters = new List<KeyValuePair<string, string>>(3)
        {
            new("dataset", Dataset),
        };

        if (DateRange is { } dateRange)
        {
            parameters.Add(new("start_date", dateRange.StartIsoDate));
            parameters.Add(new("end_date", dateRange.EndIsoDate));
        }

        return parameters;
    }
}
