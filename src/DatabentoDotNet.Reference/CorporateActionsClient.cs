using DatabentoDotNet.Historical;
using DatabentoDotNet.Reference.Internal;

namespace DatabentoDotNet.Reference;

/// <summary>
/// The <c>corporate_actions.*</c> endpoints: what happened to a security, and the documentation the
/// server keeps about its own events and enumerations.
/// </summary>
/// <remarks>
/// <para>
/// Reached through <see cref="ReferenceClient.CorporateActions"/> rather than constructed. Port of
/// upstream's <c>CorporateActionsClient</c> (<c>corporate.rs:27-96</c>), which holds a mutable
/// borrow of the outer client; this holds a reference, there being no borrow checker to satisfy.
/// </para>
/// <para>
/// <b>Three endpoints in two shapes, which is why they shipped in two issues.</b>
/// <see cref="GetRangeAsync"/> is a <c>POST</c> with a form body that answers with zstd-framed JSON
/// lines, read a row at a time (#55). <see cref="ListEventsAsync"/> and
/// <see cref="ListEnumsAsync"/> are bare <c>GET</c>s — no body, no query string — that answer with
/// one plain JSON object, read whole through <see cref="HistoricalClient.ReadJsonAsync{T}"/> (#56).
/// Different method, different encoding, different reader, and only one type family in common.
/// </para>
/// <para>
/// <b>Only one of the three costs money.</b> <see cref="GetRangeAsync"/> returns data and bills for
/// it; the other two return documentation and are, near-certainly, the only free endpoints in this
/// namespace — this repository has already called both against the live API and vendored the
/// responses (#58). "Near-certainly" is a prior rather than a measurement; #57 owns pricing them
/// properly.
/// </para>
/// <para>
/// <b>The two documentation endpoints return the whole document, and neither streams.</b> A JSON
/// object is not a sequence of rows: there is no point at which half of one is usable, so an
/// <see cref="IAsyncEnumerable{T}"/> would buy nothing and cost the caller a
/// <c>ToDictionaryAsync</c>. Upstream buffers all three (<c>corporate.rs:57-63</c>,
/// <c>:75-79</c>, <c>:91-95</c>); for these two that is not a borrow-checker artefact, and for
/// <see cref="GetRangeAsync"/> it is — see #52.
/// </para>
/// </remarks>
public sealed class CorporateActionsClient
{
    /// <summary>
    /// The compression <see cref="GetRangeAsync"/> asks for. Not caller-settable.
    /// </summary>
    /// <remarks>
    /// Upstream hard-codes this (<c>corporate.rs:42</c>) because the response handler requires the
    /// frame; <see cref="CorporateActionsGetRangeParams.ToFormParameters"/> renders it and documents
    /// why it is a constant rather than a property. Public and named so a test can assert the value
    /// on the wire against the value the library believes it sends, rather than against a string
    /// typed twice — the same reason <see cref="SecurityMasterClient.RequestCompression"/> and
    /// <see cref="AdjustmentFactorsClient.RequestCompression"/> are.
    /// </remarks>
    public const string RequestCompression = "zstd";

    private const string GetRangeSlug = "corporate_actions.get_range";
    private const string ListEventsSlug = "corporate_actions.list_events";
    private const string ListEnumsSlug = "corporate_actions.list_enums";

    private readonly ReferenceClient _client;

    internal CorporateActionsClient(ReferenceClient client) => _client = client;

    /// <summary>
    /// Streams every corporate action matching <paramref name="parameters"/> over the requested
    /// range, a row at a time as they decompress.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of upstream's <c>get_range</c> (<c>corporate.rs:33-65</c>) — with one deliberate
    /// behavioural difference, below.
    /// </para>
    /// <para>
    /// <b>This costs money, and it is the only endpoint in this class that does.</b> Reference data
    /// is a separate Databento product from historical market data: an API key entitled for one is
    /// not necessarily entitled for the other, and a <c>403</c> here on an otherwise working key
    /// means exactly that rather than a broken credential. Unlike a batch job, a stream can be
    /// stopped part-way — <c>break</c> out of the <c>await foreach</c> and the response is disposed
    /// on the way out, which is half of why this streams. It also defaults to allocating ISINs; see
    /// <see cref="CorporateActionsGetRangeParams.AllocateIsins"/>.
    /// </para>
    /// <para>
    /// <b>Rows arrive in the server's order, and this method does not sort them.</b> Upstream
    /// buffers the whole response into a <c>Vec</c> and then sorts it by whichever date
    /// <see cref="CorporateActionsGetRangeParams.Index"/> names (<c>corporate.rs:59-63</c>) — it
    /// can, because it has already paid for the buffer. A stream has not: sorting is what buffering
    /// <em>is</em>, so an <see cref="IAsyncEnumerable{T}"/> that sorted would be a list wearing a
    /// stream's type. <b>The index is still sent</b>, because it is also what the server filters
    /// on — dropping the sort does not drop the parameter. A caller who needs upstream's order can
    /// have it in one line over the materialised sequence, and pays for the buffer where they can
    /// see it. See ROADMAP.md §6, #52, and
    /// <see cref="HistoricalClient.ReadZstdJsonLinesStreamAsync"/> where the argument is made in
    /// full.
    /// </para>
    /// <para>
    /// <b>Whether that is observable is still unmeasured, and #57 is where it stops being.</b> If
    /// the server already returns rows in the index's order, dropping the sort changes nothing a
    /// caller can see; if it does not, a caller who needs that order must sort for themselves and
    /// this paragraph has to say so. <c>RealReferenceRequestTests.</c>
    /// <c>CorporateActionsGetRange_ArrivesInTheOrderTheIndexNames</c> asks the server under both
    /// <see cref="CorporateActionIndex.EventDate"/> and <see cref="CorporateActionIndex.TsRecord"/>
    /// — two indexes, because "the server sorts" and "storage order happens to match one index" are
    /// different claims and only the first survives changing it. The mock cannot answer this: it
    /// returns the lines it was given. On 2026-08-29 the account that experiment ran under was
    /// answered <c>403 license_reference_dataset_no_subscription</c>, so it is written, gated and
    /// waiting on an entitled key rather than on anyone's attention.
    /// </para>
    /// <para>
    /// <b>Nothing is sent until the enumeration starts.</b> Calling this method builds a query; the
    /// request goes out on the first <c>MoveNextAsync</c>. A caller who never enumerates never
    /// bills. The argument checks below run at the call rather than at that first step, so a
    /// mistake in them faults where it was made.
    /// </para>
    /// </remarks>
    /// <param name="parameters">Which symbols, over what range of which date, narrowed how.</param>
    /// <param name="cancellationToken">Cancels the request and the enumeration.</param>
    /// <returns>One row per corporate action, in the order the server sent them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="parameters"/> leaves <see cref="CorporateActionsGetRangeParams.Symbols"/> or
    /// <see cref="CorporateActionsGetRangeParams.DateTimeRange"/> at its type's default value.
    /// </exception>
    /// <exception cref="DatabentoApiException">The API answered with a non-success status.</exception>
    public IAsyncEnumerable<CorporateAction> GetRangeAsync(
        CorporateActionsGetRangeParams parameters,
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
            CorporateActionsJson.Default.CorporateAction,
            cancellationToken);
    }

    /// <summary>
    /// Reads the server's documentation for every corporate action event it supports, keyed by
    /// event code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of upstream's <c>list_events</c> (<c>corporate.rs:67-80</c>).
    /// </para>
    /// <para>
    /// <b>Keyed by the string the server filed each document under, never by a parsed
    /// <see cref="Event"/>.</b> That is upstream's choice (<c>HashMap&lt;String, EventDoc&gt;</c>)
    /// and it is the right one: an event code this library has never seen still arrives under its
    /// own key, where a caller can find it. Parsing the key would either lose such an entry or
    /// collapse several onto one <see langword="default"/>. The key is ordinal and
    /// case-sensitive, as upstream's is — <c>AGM</c> is not <c>agm</c>.
    /// </para>
    /// <para>
    /// <b>What it is for beyond being an endpoint:</b> each document's
    /// <see cref="EventDoc.Fields"/> says which of <c>CorporateAction</c>'s three open maps every
    /// field lands in, so this is the authority for what may legally appear in them (#55). It is
    /// also the only authority for <see cref="EventCategory"/>, <see cref="EventLevel"/> and
    /// <see cref="FieldGroup"/>, none of which <c>list_enums</c> reports a group for.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Cancels the request and the read.</param>
    /// <returns>One document per event, keyed by the event code the server filed it under.</returns>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    /// <exception cref="DatabentoApiException">The API answered with a non-success status.</exception>
    /// <exception cref="System.Text.Json.JsonException">The body was not a readable document.</exception>
    public async Task<IReadOnlyDictionary<string, EventDoc>> ListEventsAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await _client.Transport
            .SendAsync(
                HttpMethod.Get,
                ListEventsSlug,
                parameters: null,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return await HistoricalClient
            .ReadJsonAsync(
                response, CorporateActionsJson.Default.DictionaryStringEventDoc, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads every enumeration the corporate actions data uses, keyed by enum group name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of upstream's <c>list_enums</c> (<c>corporate.rs:82-96</c>).
    /// </para>
    /// <para>
    /// <b>This is the dictionary the ten open code types are transcribed from</b>, and the reason
    /// they are open at all: probing it found upstream's own tables behind the server on
    /// <see cref="SecurityType"/> and <see cref="Frequency"/>, and stale in both directions on
    /// <see cref="Event"/>. A group name maps to a type here — <c>SECTYPE</c> to
    /// <see cref="SecurityType"/>, <c>MANDVOLU</c> to <see cref="MandVolu"/> — but that mapping
    /// lives in the tables rather than in this method, which returns the server's own key. See
    /// ROADMAP.md §6 and <c>tests/DatabentoDotNet.Reference.Tests/Data/README.md</c>.
    /// </para>
    /// <para>
    /// <b>A group may list a blank code, and 148 of the 235 the server returned do.</b> Those
    /// arrive as a <see langword="null"/> <see cref="EventEnumVariant.Code"/>, which is the
    /// evidence behind every code carrier reading a blank as "no value" rather than rejecting it.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Cancels the request and the read.</param>
    /// <returns>The variants of each enumeration, keyed by the group name the server uses.</returns>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    /// <exception cref="DatabentoApiException">The API answered with a non-success status.</exception>
    /// <exception cref="System.Text.Json.JsonException">The body was not a readable document.</exception>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<EventEnumVariant>>> ListEnumsAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await _client.Transport
            .SendAsync(
                HttpMethod.Get,
                ListEnumsSlug,
                parameters: null,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return await HistoricalClient
            .ReadJsonAsync(
                response,
                CorporateActionsJson.Default.DictionaryStringIReadOnlyListEventEnumVariant,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
