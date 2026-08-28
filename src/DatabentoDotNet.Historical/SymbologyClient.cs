using DatabentoDotNet.Dbn;
using DatabentoDotNet.Historical.Internal;

namespace DatabentoDotNet.Historical;

/// <summary>
/// The <c>symbology.*</c> endpoints: what instrument ids a set of symbols had, over a range of
/// days.
/// </summary>
/// <remarks>
/// <para>
/// Reached through <see cref="HistoricalClient.Symbology"/> rather than constructed. Port of
/// upstream's <c>SymbologyClient</c> (<c>symbology.rs:14-18</c>), which holds a mutable borrow of
/// the outer client; this holds a reference, there being no borrow checker to satisfy.
/// </para>
/// <para>
/// One endpoint, and it is free — <c>symbology.resolve</c> moves no market data, so nothing here
/// is gated behind the opt-in that <c>timeseries.get_range</c> and <c>batch.submit_job</c> carry.
/// </para>
/// </remarks>
public sealed class SymbologyClient
{
    private readonly HistoricalClient _client;

    internal SymbologyClient(HistoricalClient client) => _client = client;

    /// <summary>
    /// Resolves symbols from one symbology to another over a range of UTC days — for example a raw
    /// symbol to an instrument id, <c>ESM2</c> → <c>3403</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of upstream's <c>resolve</c> (<c>symbology.rs:29-50</c>). <c>POST</c>, with every
    /// parameter in the form body — see <see cref="ResolveParams.ToFormParameters"/> for the field
    /// order and for why <c>end_date</c> goes on the wire unchanged.
    /// </para>
    /// <para>
    /// <b>A symbol that does not resolve is not an error.</b> The API answers HTTP 200 whether or
    /// not anything resolved, so no <see cref="DatabentoApiException"/> is thrown for it and the
    /// returned <see cref="Resolution"/>'s <see cref="Resolution.NotFound"/> and
    /// <see cref="Resolution.Partial"/> are the only signal. See <see cref="Resolution"/> for what
    /// each bucket means and for why a symbol appears in
    /// <see cref="Resolution.Mappings"/> regardless of which one it landed in.
    /// </para>
    /// </remarks>
    /// <param name="parameters">What to resolve, from which symbology to which, over which days.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The resolution, with the request's two symbology types attached.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="DatabentoApiException">The API answered with a non-success status.</exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// The response body was not a resolution: one of <c>result</c>, <c>partial</c> or
    /// <c>not_found</c> was absent, or a mapping interval was missing one of its own three keys.
    /// A body that omitted <c>result</c> would otherwise read as a valid answer in which nothing
    /// resolved and nothing was reported missing.
    /// </exception>
    public async Task<Resolution> ResolveAsync(
        ResolveParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var response = await _client.SendJsonAsync(
            HttpMethod.Post,
            Slug("resolve"),
            parameters.ToFormParameters(),
            SymbologyJson.Default.ResolutionResponse,
            cancellationToken).ConfigureAwait(false);

        var mappings = new Dictionary<string, IReadOnlyList<MappingInterval>>(
            response.Result.Count, StringComparer.Ordinal);

        foreach (var (symbol, intervals) in response.Result)
        {
            mappings[symbol] = intervals;
        }

        return new Resolution
        {
            Mappings = mappings,
            Partial = response.Partial,
            NotFound = response.NotFound,
            StypeIn = parameters.StypeIn,
            StypeOut = parameters.StypeOut,
        };
    }

    /// <summary>
    /// The endpoint group's slug prefix, built the way <c>MetadataClient.Slug</c> builds its own
    /// (<c>symbology.rs:52-54</c>), before the transport prepends the API version.
    /// </summary>
    private static string Slug(string endpoint) => $"symbology.{endpoint}";
}
