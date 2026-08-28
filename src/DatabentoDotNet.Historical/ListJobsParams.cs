using System.Globalization;
using NodaTime;

namespace DatabentoDotNet.Historical;

/// <summary>The filters <c>batch.list_jobs</c> takes. Both are optional.</summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>ListJobsParams</c> (<c>batch.rs:596-603</c>). A default instance is a
/// valid request that filters nothing, which is why nothing here is
/// <see langword="required"/> and why <see cref="ToQueryParameters"/> can return an empty list.
/// </para>
/// <para>
/// <b>Leaving <see cref="States"/> unset is not the same as naming every state.</b> Upstream
/// documents the API's own default as "all except <c>Expired</c>" (<c>batch.rs:597</c>). #39 could
/// not measure that — every job the probing account holds is <see cref="JobState.Done"/> — so the
/// claim is repeated here as upstream's rather than as this library's, and a caller who wants
/// expired jobs should name the states.
/// </para>
/// </remarks>
public sealed record ListJobsParams
{
    /// <summary>
    /// The job states to include, or <see langword="null"/> for the API's own default.
    /// </summary>
    /// <remarks>
    /// Rendered as one comma-separated <c>states</c> parameter, which is upstream's spelling
    /// (<c>batch.rs:141-152</c>) and which #39 confirmed the API reads:
    /// <c>states=done,queued</c> answers with both. An unknown state is a <c>400</c> whose body
    /// enumerates the seven the API knows — which is how <see cref="JobState"/> came to have three
    /// members upstream lacks.
    /// </remarks>
    public IReadOnlyList<JobState>? States { get; init; }

    /// <summary>
    /// The earliest submission time to include, or <see langword="null"/> for no lower bound.
    /// </summary>
    /// <remarks>
    /// Compared against each job's <see cref="BatchJobSummary.ReceivedTimestamp"/> and sent as Unix
    /// nanoseconds, as upstream does (<c>batch.rs:153-155</c>). #39 measured the filter working at
    /// day granularity: a <c>since</c> set past two of the four jobs' receipt times returned only
    /// the other two.
    /// </remarks>
    public Instant? Since { get; init; }

    /// <summary>Renders these filters as the query string <c>batch.list_jobs</c> takes.</summary>
    /// <remarks>
    /// <para>
    /// <b><c>short</c> is not rendered here</b>, though it travels on the same request. It is not a
    /// filter — it selects which of two response shapes comes back — so
    /// <see cref="BatchClient.ListJobsAsync"/> and <see cref="BatchClient.ListJobsFullAsync"/> each
    /// add their own, which is also what stops a caller pairing this parameter set with the wrong
    /// return type.
    /// </para>
    /// <para>
    /// An unset filter is omitted rather than sent empty, and a <see cref="States"/> list that is
    /// present but empty is treated as unset: an empty <c>states=</c> is not a request for no
    /// states, it is a malformed one.
    /// </para>
    /// </remarks>
    /// <returns>The query parameters, empty when nothing is filtered.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="States"/> holds a value outside the defined <see cref="JobState"/> set.
    /// </exception>
    /// <exception cref="OverflowException">
    /// <see cref="Since"/> is too far from the Unix epoch (roughly beyond the year 2262) for its
    /// nanosecond count to fit in a <see cref="long"/>. See CLAUDE.md, "Dates and times".
    /// </exception>
    public IReadOnlyList<KeyValuePair<string, string>> ToQueryParameters()
    {
        var parameters = new List<KeyValuePair<string, string>>(2);

        if (States is { Count: > 0 } states)
        {
            parameters.Add(new("states", string.Join(',', states.Select(BatchWireStrings.ToWireString))));
        }

        if (Since is { } since)
        {
            var nanoseconds = (since - NodaConstants.UnixEpoch).ToInt64Nanoseconds();
            parameters.Add(new("since", nanoseconds.ToString(CultureInfo.InvariantCulture)));
        }

        return parameters;
    }
}
