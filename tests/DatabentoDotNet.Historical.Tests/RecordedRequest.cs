namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// One request as <see cref="MockHistoricalGateway"/> saw it: method, path, query string, form
/// fields, headers and raw body.
/// </summary>
/// <remarks>
/// <para>
/// The historical API splits its requests between the two encodings this type flattens.
/// <c>metadata.*</c> and <c>symbology.resolve</c> are <c>GET</c>s carrying their parameters in the
/// query string; <c>timeseries.get_range</c> and <c>batch.submit_job</c> are <c>POST</c>s carrying
/// theirs as <c>application/x-www-form-urlencoded</c>. A test asserts against
/// <see cref="Query"/> or <see cref="Form"/> accordingly, and both are empty rather than absent
/// when the request carried neither.
/// </para>
/// <para>
/// <b><see cref="Headers"/> does not contain <c>Authorization</c>, on purpose.</b> The API key is
/// what that header carries, and the harness's own guard is the only thing that has any business
/// reading it — see <see cref="MockHistoricalGateway"/>, whose one non-negotiable rule is that the
/// key never leaves that header. A test that wants to know whether the credential was right asks
/// by making the request: a wrong one is refused.
/// </para>
/// <para>
/// A repeated query or form parameter is joined with a comma, which is also how the API's own
/// multi-value parameters — <c>symbols</c> above all — arrive in the first place.
/// </para>
/// </remarks>
public sealed class RecordedRequest
{
    /// <summary>The HTTP method, upper-case: <c>GET</c> or <c>POST</c>.</summary>
    public required string Method { get; init; }

    /// <summary>
    /// The request path, including the API version segment — <c>/v0/metadata.list_datasets</c>.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>The query string parameters, keyed ordinally. Empty when there were none.</summary>
    public required IReadOnlyDictionary<string, string> Query { get; init; }

    /// <summary>
    /// The <c>application/x-www-form-urlencoded</c> body fields, keyed ordinally. Empty when the
    /// request carried no form body.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Form { get; init; }

    /// <summary>
    /// The request headers, keyed case-insensitively as HTTP header names are, and without
    /// <c>Authorization</c>.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    /// <summary>
    /// The raw request body, before any form decoding. Empty for a <c>GET</c>.
    /// </summary>
    public required ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>The route key this request matched against: <c>"GET /v0/metadata.list_datasets"</c>.</summary>
    public string RouteKey => $"{Method} {Path}";
}
