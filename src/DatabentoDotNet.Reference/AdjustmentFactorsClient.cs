using DatabentoDotNet.Reference.Internal;

namespace DatabentoDotNet.Reference;

/// <summary>
/// The <c>adjustment_factors.*</c> endpoints: the multipliers that make a price series comparable
/// across splits, dividends and other capital events.
/// </summary>
/// <remarks>
/// <para>
/// Reached through <see cref="ReferenceClient.AdjustmentFactors"/> rather than constructed. Port of
/// upstream's <c>AdjustmentFactorsClient</c> (<c>adjustment.rs:15-54</c>), which holds a mutable
/// borrow of the outer client; this holds a reference, there being no borrow checker to satisfy.
/// </para>
/// <para>
/// <b>Its one method bills.</b> Reference data is a separate Databento product, so a
/// <c>403</c> here is a legitimate outcome on an account entitled for historical data rather than a
/// mysterious failure — see <see cref="GetRangeAsync"/>.
/// </para>
/// </remarks>
public sealed class AdjustmentFactorsClient
{
    /// <summary>
    /// The compression every <c>get_range</c> request asks for. Not caller-settable.
    /// </summary>
    /// <remarks>
    /// Upstream hard-codes this (<c>adjustment.rs:36</c>) because the response handler requires the
    /// frame; <see cref="AdjustmentFactorsGetRangeParams.ToFormParameters"/> renders it and
    /// documents why it is a constant rather than a property. Public and named so a test can assert
    /// the value on the wire against the value the library believes it sends, rather than against a
    /// string typed twice — the same reason
    /// <see cref="DatabentoDotNet.Historical.TimeseriesClient.RequestCompression"/> is.
    /// </remarks>
    public const string RequestCompression = "zstd";

    private const string GetRangeSlug = "adjustment_factors.get_range";

    private readonly ReferenceClient _client;

    internal AdjustmentFactorsClient(ReferenceClient client) => _client = client;

    /// <summary>
    /// Streams the adjustment factors matching <paramref name="parameters"/>, a row at a time as
    /// they decompress.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of upstream's <c>get_range</c> (<c>adjustment.rs:28-53</c>) — with one deliberate
    /// behavioural difference, below.
    /// </para>
    /// <para>
    /// <b>This costs money, and it is billed by what it returns.</b> Reference data is a separate
    /// Databento product from historical market data: an API key entitled for one is not
    /// necessarily entitled for the other, and a <c>403</c> from this endpoint on an otherwise
    /// working key means exactly that rather than a broken credential. Unlike a batch job, a stream
    /// can be stopped part-way — <c>break</c> out of the <c>await foreach</c> and the response is
    /// disposed on the way out, which is half of why this streams.
    /// </para>
    /// <para>
    /// <b>Rows arrive in the server's order, and this method does not sort them.</b> Upstream
    /// buffers the whole response into a <c>Vec</c> and then sorts it by <c>ex_date</c>
    /// (<c>adjustment.rs:49-51</c>) — it can, because it has already paid for the buffer. A stream
    /// has not: sorting is what buffering <em>is</em>, so an <see cref="IAsyncEnumerable{T}"/> that
    /// sorted would be a list wearing a stream's type. A caller who needs upstream's order can have
    /// it in one line —
    /// <c>rows.OrderBy(r =&gt; r.ExDate)</c> over the materialised sequence — and pays for the
    /// buffer where they can see it. Whether the server's own order is already
    /// <c>ex_date</c> order is unmeasured; #57 owns the probe. See ROADMAP.md §6 and
    /// <see cref="DatabentoDotNet.Historical.HistoricalClient.ReadZstdJsonLinesStreamAsync"/>,
    /// where the argument is made in full.
    /// </para>
    /// <para>
    /// <b>Nothing is sent until the enumeration starts.</b> Calling this method builds a query; the
    /// request goes out on the first <c>MoveNextAsync</c>. A caller who never enumerates never
    /// bills. The argument checks below run at the call rather than at that first step, so a
    /// mistake in them faults where it was made.
    /// </para>
    /// </remarks>
    /// <param name="parameters">Which symbols, over what range, narrowed how.</param>
    /// <param name="cancellationToken">Cancels the request and the enumeration.</param>
    /// <returns>One row per adjustment factor, in the order the server sent them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="parameters"/> leaves <see cref="AdjustmentFactorsGetRangeParams.Symbols"/>
    /// or <see cref="AdjustmentFactorsGetRangeParams.DateTimeRange"/> at its type's default value.
    /// </exception>
    /// <exception cref="DatabentoDotNet.Historical.DatabentoApiException">The API answered with a non-success status.</exception>
    public IAsyncEnumerable<AdjustmentFactor> GetRangeAsync(
        AdjustmentFactorsGetRangeParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        // Rendered here rather than inside the iterator, and that placement is the whole reason
        // this method is not itself an iterator: ToFormParameters throws for a default Symbols or
        // range, and inside an iterator that throw would be deferred to the first MoveNextAsync —
        // or swallowed entirely for a caller who never enumerates. The transport's own streaming
        // send makes the same split for the same reason.
        var form = parameters.ToFormParameters();

        return _client.Transport.SendZstdJsonLinesStreamAsync(
            HttpMethod.Post,
            GetRangeSlug,
            form,
            AdjustmentFactorsJson.Default.AdjustmentFactor,
            cancellationToken);
    }
}
