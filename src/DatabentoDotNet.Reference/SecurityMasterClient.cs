using DatabentoDotNet.Reference.Internal;

namespace DatabentoDotNet.Reference;

/// <summary>
/// The <c>security_master.*</c> endpoints: what a listing is, where it trades, and every identifier
/// it is known by.
/// </summary>
/// <remarks>
/// <para>
/// Reached through <see cref="ReferenceClient.SecurityMaster"/> rather than constructed. Port of
/// upstream's <c>SecurityMasterClient</c> (<c>security.rs:20-82</c>), which holds a mutable borrow
/// of the outer client; this holds a reference, there being no borrow checker to satisfy.
/// </para>
/// <para>
/// <b>Two methods, one row type, and the difference between them is which rows.</b>
/// <see cref="GetRangeAsync"/> asks for every version of a record over a window of one of its two
/// timestamps; <see cref="GetLastAsync"/> asks for the latest version and takes no window at all.
/// Both return <see cref="SecurityMaster"/>.
/// </para>
/// <para>
/// <b>Both methods bill.</b> Reference data is a separate Databento product, so a <c>403</c>
/// here is a legitimate outcome on an account entitled for historical data rather than a
/// mysterious failure. Both also default to allocating ISINs — see
/// <see cref="SecurityMasterGetRangeParams.AllocateIsins"/>, which is the one property in this
/// group whose default can spend an entitlement rather than only money.
/// </para>
/// </remarks>
public sealed class SecurityMasterClient
{
    /// <summary>
    /// The compression both endpoints ask for. Not caller-settable.
    /// </summary>
    /// <remarks>
    /// Upstream hard-codes this (<c>security.rs:40</c>, <c>:70</c>) because the response handler
    /// requires the frame; the two parameter types render it and document why it is a constant
    /// rather than a property. Public and named so a test can assert the value on the wire against
    /// the value the library believes it sends, rather than against a string typed twice — the same
    /// reason <see cref="AdjustmentFactorsClient.RequestCompression"/> is.
    /// </remarks>
    public const string RequestCompression = "zstd";

    private const string GetRangeSlug = "security_master.get_range";
    private const string GetLastSlug = "security_master.get_last";

    private readonly ReferenceClient _client;

    internal SecurityMasterClient(ReferenceClient client) => _client = client;

    /// <summary>
    /// Streams every security master record matching <paramref name="parameters"/> over the
    /// requested range, a row at a time as they decompress.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of upstream's <c>get_range</c> (<c>security.rs:31-53</c>) — with one deliberate
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
    /// buffers the whole response into a <c>Vec</c> and then sorts it by whichever timestamp
    /// <see cref="SecurityMasterGetRangeParams.Index"/> names (<c>security.rs:50-53</c>) — it can,
    /// because it has already paid for the buffer. A stream has not: sorting is what buffering
    /// <em>is</em>, so an <see cref="IAsyncEnumerable{T}"/> that sorted would be a list wearing a
    /// stream's type. <b>The index is still sent</b>, because it is also what the server filters
    /// on — dropping the sort does not drop the parameter. A caller who needs upstream's order can
    /// have it in one line over the materialised sequence, and pays for the buffer where they can
    /// see it. See ROADMAP.md §6, and
    /// <see cref="DatabentoDotNet.Historical.HistoricalClient.ReadZstdJsonLinesStreamAsync"/> where
    /// the argument is made in full.
    /// </para>
    /// <para>
    /// <b>Nothing is sent until the enumeration starts.</b> Calling this method builds a query; the
    /// request goes out on the first <c>MoveNextAsync</c>. A caller who never enumerates never
    /// bills. The argument checks below run at the call rather than at that first step, so a
    /// mistake in them faults where it was made.
    /// </para>
    /// </remarks>
    /// <param name="parameters">Which symbols, over what range of which timestamp.</param>
    /// <param name="cancellationToken">Cancels the request and the enumeration.</param>
    /// <returns>One row per security master record, in the order the server sent them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="parameters"/> leaves <see cref="SecurityMasterGetRangeParams.Symbols"/> or
    /// <see cref="SecurityMasterGetRangeParams.DateTimeRange"/> at its type's default value.
    /// </exception>
    /// <exception cref="DatabentoDotNet.Historical.DatabentoApiException">The API answered with a non-success status.</exception>
    public IAsyncEnumerable<SecurityMaster> GetRangeAsync(
        SecurityMasterGetRangeParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        // Rendered here rather than inside the iterator, and that placement is the whole reason
        // this method is not itself an iterator: ToFormParameters throws for a default Symbols or
        // range, and inside an iterator that throw would be deferred to the first MoveNextAsync —
        // or swallowed entirely for a caller who never enumerates.
        var form = parameters.ToFormParameters();

        return _client.Transport.SendZstdJsonLinesStreamAsync(
            HttpMethod.Post,
            GetRangeSlug,
            form,
            SecurityMasterJson.Default.SecurityMaster,
            cancellationToken);
    }

    /// <summary>
    /// Streams the latest security master record for each security matching
    /// <paramref name="parameters"/>, a row at a time as they decompress.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of upstream's <c>get_last</c> (<c>security.rs:62-79</c>).
    /// </para>
    /// <para>
    /// <b>No range and no index.</b> The endpoint's answer is "the current record", so there is
    /// nothing for a window to select — <see cref="SecurityMasterGetLastParams"/> has no property
    /// for either, and the form this posts carries neither key.
    /// </para>
    /// <para>
    /// <b>Upstream documents this one as sorted by <c>ts_effective</c> and it is not sorted here,
    /// which is #52's decision restated rather than a second one.</b> That sort has no request
    /// counterpart at all — no <c>index</c> is sent, so it is purely a rearrangement of a buffer
    /// upstream had already paid for (<c>security.rs:77</c>). A stream has no buffer to rearrange.
    /// A caller who wants it writes <c>rows.OrderBy(row =&gt; row.TsEffective)</c> over the
    /// materialised sequence.
    /// </para>
    /// <para>
    /// <b>This costs money and defaults to allocating ISINs</b>, exactly as
    /// <see cref="GetRangeAsync"/> does, and sends nothing until the enumeration starts for the
    /// same reason.
    /// </para>
    /// </remarks>
    /// <param name="parameters">Which symbols, narrowed how.</param>
    /// <param name="cancellationToken">Cancels the request and the enumeration.</param>
    /// <returns>The latest row per security, in the order the server sent them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="parameters"/> leaves <see cref="SecurityMasterGetLastParams.Symbols"/> at
    /// its type's default value.
    /// </exception>
    /// <exception cref="DatabentoDotNet.Historical.DatabentoApiException">The API answered with a non-success status.</exception>
    public IAsyncEnumerable<SecurityMaster> GetLastAsync(
        SecurityMasterGetLastParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var form = parameters.ToFormParameters();

        return _client.Transport.SendZstdJsonLinesStreamAsync(
            HttpMethod.Post,
            GetLastSlug,
            form,
            SecurityMasterJson.Default.SecurityMaster,
            cancellationToken);
    }
}
