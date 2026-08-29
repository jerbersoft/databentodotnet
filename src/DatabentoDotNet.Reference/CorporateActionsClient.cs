using DatabentoDotNet.Historical;
using DatabentoDotNet.Reference.Internal;

namespace DatabentoDotNet.Reference;

/// <summary>
/// The <c>corporate_actions.*</c> endpoints. Two of them ship here: the documentation the server
/// keeps about its own events and enumerations.
/// </summary>
/// <remarks>
/// <para>
/// Reached through <see cref="ReferenceClient.CorporateActions"/> rather than constructed. Port of
/// upstream's <c>CorporateActionsClient</c> (<c>corporate.rs:27-96</c>), which holds a mutable
/// borrow of the outer client; this holds a reference, there being no borrow checker to satisfy.
/// </para>
/// <para>
/// <b>These two are shaped unlike everything else in this namespace.</b> The three
/// <c>get_range</c> endpoints are <c>POST</c>s with a form body that answer with zstd-framed JSON
/// lines and are read a row at a time. These are bare <c>GET</c>s — no body, no query string — that
/// answer with one plain JSON object, read whole through
/// <see cref="HistoricalClient.ReadJsonAsync{T}"/>. That is why #56 ships them separately from
/// <c>get_range</c> (#55) despite the three sharing this class: they have a different method, a
/// different encoding, a different reader and a different type family in common with it.
/// </para>
/// <para>
/// <b>They are also, near-certainly, the only free endpoints in this namespace</b> — they return
/// documentation rather than data, and this repository has already called both against the live API
/// and vendored the responses (#58). "Near-certainly" is a prior rather than a measurement; #57
/// owns pricing them properly, and is where they move if it turns out otherwise.
/// </para>
/// <para>
/// <b>Both return the whole document, and neither streams.</b> A JSON object is not a sequence of
/// rows: there is no point at which half of one is usable, so an
/// <see cref="IAsyncEnumerable{T}"/> would buy nothing and cost the caller a
/// <c>ToDictionaryAsync</c>. Upstream buffers these two as well (<c>corporate.rs:75-79</c>,
/// <c>:91-95</c>), and for once that is not a borrow-checker artefact — see #52 for the endpoints
/// where it is.
/// </para>
/// </remarks>
public sealed class CorporateActionsClient
{
    private const string ListEventsSlug = "corporate_actions.list_events";
    private const string ListEnumsSlug = "corporate_actions.list_enums";

    private readonly ReferenceClient _client;

    internal CorporateActionsClient(ReferenceClient client) => _client = client;

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
