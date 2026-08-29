using System.Globalization;
using System.Net.Http.Headers;
using System.Text;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// A hand-written client that speaks the client half of the Databento historical API, used to drive
/// <see cref="MockHistoricalGateway"/> in the harness's own tests.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately <em>not</em> the library's historical client — that arrives in
/// <see href="https://github.com/jerbersoft/databentodotnet/issues/35">#35</see>, and a harness
/// verified against the thing it exists to verify proves nothing. It is written from the API's
/// documented HTTP behaviour: <c>{base}/v0/{slug}</c>, HTTP Basic with the API key as the username
/// and an empty password, <c>Accept: application/json</c> on everything except
/// <c>timeseries.get_range</c>, which takes <c>application/octet-stream</c> and posts its
/// parameters as a form.
/// </para>
/// <para>
/// <b>Every request is built by hand rather than from <c>DefaultRequestHeaders</c>.</b> Half of
/// what this stub exists to do is send a credential the gateway must refuse — no
/// <c>Authorization</c> at all, a non-empty password, the key in the query string — and a default
/// header is exactly the thing that cannot be removed for one request. The correct credential is
/// built by the same <see cref="BasicHeader"/> the wrong ones are, so the difference between a test
/// that should pass and one that should not is visible at the call site.
/// </para>
/// </remarks>
public sealed class StubHistoricalClient : IDisposable
{
    /// <summary>
    /// The user agent this stub reports. It starts with
    /// <see cref="MockHistoricalGateway.UserAgentPrefix"/>, which is all the gateway checks and all
    /// the real client will be able to promise.
    /// </summary>
    public const string UserAgent = "DatabentoDotNet/0.0.0-stub .NET test-stub";

    /// <summary>The <c>Accept</c> the API's JSON endpoints take.</summary>
    public const string JsonAccept = "application/json";

    /// <summary>The <c>Accept</c> <c>timeseries.get_range</c> and a batch file download take.</summary>
    public const string BinaryAccept = "application/octet-stream";

    private readonly HttpClient _http;
    private readonly Uri _baseUrl;
    private readonly string _apiKey;

    /// <summary>Creates a client pointed at <paramref name="baseUrl"/>.</summary>
    /// <param name="baseUrl">
    /// The gateway's base URL — <see cref="MockHistoricalGateway.BaseUrl"/>. The client reaches the
    /// harness through a base-URL override, the way upstream's tests do.
    /// </param>
    /// <param name="apiKey">The API key to authenticate with.</param>
    public StubHistoricalClient(Uri baseUrl, string apiKey = MockHistoricalGateway.TestApiKey)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentException.ThrowIfNullOrEmpty(apiKey);

        _baseUrl = baseUrl;
        _apiKey = apiKey;

        // No automatic decompression: the zstd-framed bodies the API returns announce nothing in
        // Content-Encoding, so there is nothing for HttpClient to act on and a client that expected
        // it to would read a frame it never unwrapped.
        _http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.None });
    }

    /// <summary>
    /// The value of an HTTP Basic <c>Authorization</c> header for a username and password.
    /// </summary>
    /// <param name="username">The username. The API takes the API key here.</param>
    /// <param name="password">The password. The API takes an empty one.</param>
    /// <returns>The header value, scheme included.</returns>
    public static string BasicHeader(string username, string password)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);

        return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));
    }

    /// <summary>Sends a correctly authenticated <c>GET</c> of <paramref name="slug"/>.</summary>
    /// <param name="slug">The API slug, without the version prefix.</param>
    /// <param name="query">The query string parameters, or <see langword="null"/> for none.</param>
    /// <param name="accept">The <c>Accept</c> header. Defaults to <see cref="JsonAccept"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The response, headers read but body not yet buffered.</returns>
    public Task<HttpResponseMessage> GetAsync(
        string slug,
        IEnumerable<KeyValuePair<string, string>>? query = null,
        string accept = JsonAccept,
        CancellationToken cancellationToken = default)
    {
        var request = Request(HttpMethod.Get, slug, query, accept);
        request.Headers.TryAddWithoutValidation("Authorization", BasicHeader(_apiKey, string.Empty));
        return SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Sends a correctly authenticated <c>POST</c> of <paramref name="slug"/> with an
    /// <c>application/x-www-form-urlencoded</c> body, the way <c>timeseries.get_range</c> and
    /// <c>batch.submit_job</c> are sent.
    /// </summary>
    /// <param name="slug">The API slug, without the version prefix.</param>
    /// <param name="form">The form fields.</param>
    /// <param name="accept">The <c>Accept</c> header. Defaults to <see cref="JsonAccept"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The response, headers read but body not yet buffered.</returns>
    public Task<HttpResponseMessage> PostFormAsync(
        string slug,
        IEnumerable<KeyValuePair<string, string>> form,
        string accept = JsonAccept,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);

        var request = Request(HttpMethod.Post, slug, query: null, accept);
        request.Headers.TryAddWithoutValidation("Authorization", BasicHeader(_apiKey, string.Empty));
        request.Content = new FormUrlEncodedContent(form);
        return SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Sends a correctly authenticated <c>GET</c> asking for the tail of a body from
    /// <paramref name="firstByte"/>, which is how a batch download resumes.
    /// </summary>
    /// <param name="slug">The API slug, without the version prefix.</param>
    /// <param name="firstByte">The first byte wanted, producing <c>Range: bytes=N-</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The response, headers read but body not yet buffered.</returns>
    public Task<HttpResponseMessage> GetRangeAsync(
        string slug,
        long firstByte,
        CancellationToken cancellationToken = default)
    {
        var request = Request(HttpMethod.Get, slug, query: null, BinaryAccept);
        request.Headers.TryAddWithoutValidation("Authorization", BasicHeader(_apiKey, string.Empty));
        request.Headers.Range = new RangeHeaderValue(firstByte, null);
        return SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Sends a correctly authenticated <c>GET</c> carrying <paramref name="range"/> as its
    /// <c>Range</c> header verbatim.
    /// </summary>
    /// <remarks>
    /// The seam for the ranges <see cref="GetRangeAsync"/> cannot express, because
    /// <c>RangeHeaderValue</c> will not build them: a unit that is not <c>bytes</c>, a count that is
    /// not a number, and — the one that matters —
    /// <c>bytes={{body length}}-</c>, which is what a resumed download asks for when the local file
    /// is already complete.
    /// </remarks>
    /// <param name="slug">The API slug, without the version prefix.</param>
    /// <param name="range">The <c>Range</c> header value, sent without validation.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The response, headers read but body not yet buffered.</returns>
    public Task<HttpResponseMessage> GetWithRawRangeAsync(
        string slug,
        string range,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(range);

        var request = Request(HttpMethod.Get, slug, query: null, BinaryAccept);
        request.Headers.TryAddWithoutValidation("Authorization", BasicHeader(_apiKey, string.Empty));
        request.Headers.TryAddWithoutValidation("Range", range);
        return SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Sends a <c>GET</c> carrying <paramref name="authorization"/> verbatim, or no
    /// <c>Authorization</c> header at all when it is <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// The seam the gateway's credential guard is tested through. Everything else about the request
    /// is correct, so a refusal can only be about the credential.
    /// </remarks>
    /// <param name="slug">The API slug, without the version prefix.</param>
    /// <param name="authorization">The header value, or <see langword="null"/> to omit the header.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The response, headers read but body not yet buffered.</returns>
    public Task<HttpResponseMessage> GetWithAuthorizationAsync(
        string slug,
        string? authorization,
        CancellationToken cancellationToken = default)
    {
        var request = Request(HttpMethod.Get, slug, query: null, JsonAccept);
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        return SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Sends a correctly authenticated <c>GET</c> that <em>also</em> puts the API key in the query
    /// string, which is the one place it must never be.
    /// </summary>
    /// <remarks>
    /// The header is correct on purpose: a refusal then says the key was somewhere it does not
    /// belong, rather than that the request failed to authenticate.
    /// </remarks>
    /// <param name="slug">The API slug, without the version prefix.</param>
    /// <param name="parameterName">The query parameter to smuggle it in. Defaults to <c>key</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The response, headers read but body not yet buffered.</returns>
    public Task<HttpResponseMessage> GetWithApiKeyInQueryAsync(
        string slug,
        string parameterName = "key",
        CancellationToken cancellationToken = default)
    {
        var query = new[] { new KeyValuePair<string, string>(parameterName, _apiKey) };
        var request = Request(HttpMethod.Get, slug, query, JsonAccept);
        request.Headers.TryAddWithoutValidation("Authorization", BasicHeader(_apiKey, string.Empty));
        return SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Sends a correctly authenticated <c>GET</c> reporting <paramref name="userAgent"/>.
    /// </summary>
    /// <param name="slug">The API slug, without the version prefix.</param>
    /// <param name="userAgent">The <c>User-Agent</c> to report.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The response, headers read but body not yet buffered.</returns>
    public Task<HttpResponseMessage> GetWithUserAgentAsync(
        string slug,
        string userAgent,
        CancellationToken cancellationToken = default)
    {
        var request = Request(HttpMethod.Get, slug, query: null, JsonAccept, userAgent);
        request.Headers.TryAddWithoutValidation("Authorization", BasicHeader(_apiKey, string.Empty));
        return SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Reads a zstd-framed JSONL body and returns its lines.
    /// </summary>
    /// <remarks>
    /// The frame is unwrapped here rather than by <c>HttpClient</c> because the API puts it in the
    /// body and says nothing in <c>Content-Encoding</c>.
    /// </remarks>
    /// <param name="response">The response.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One string per line, without the newline.</returns>
    public static async Task<IReadOnlyList<string>> ReadZstdJsonLinesAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var decompressor = new ZstdSharp.DecompressionStream(body);
        using var reader = new StreamReader(decompressor, Encoding.UTF8);

        var lines = new List<string>();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    /// <summary>
    /// Reads a zstd-framed JSONL body that may stop part-way, returning both the lines that
    /// decompressed and the failure that ended the read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ReadZstdJsonLinesAsync"/> for a frame that is not whole. A zstd frame carries an
    /// epilogue, so a body cut short of it decompresses perfectly well up to the cut and then
    /// reports <see cref="EndOfStreamException"/> — "premature end of stream" — and that pair is
    /// the whole answer rather than an error to be swallowed. Both halves are worth asserting:
    /// the lines say the prefix decoded to exactly what was written before the cut, and the
    /// failure says it really was a prefix and not accidentally a complete frame.
    /// </para>
    /// <para>
    /// The same shape as <see cref="ReadUntilEndAsync"/> and for the same reason. Only transfer and
    /// truncation failures are caught: a <c>ZstdSharp</c> exception about corrupt data is not a
    /// short read, it is a frame nobody should have served, and it propagates.
    /// </para>
    /// <para>
    /// <b>A separate method rather than a shared core with <see cref="ReadZstdJsonLinesAsync"/>,
    /// and the reason is not structural.</b> An earlier draft of this remark claimed it was — that
    /// the other method had a consumer outside this assembly that a refactor would disturb — and
    /// that is simply false: <see cref="ReadZstdJsonLinesAsync"/> here is
    /// <see cref="StubHistoricalClient"/>'s own, its only callers are in
    /// <c>MockHistoricalGatewayTests</c> in this same assembly, and nothing outside the harness can
    /// see it at all. Anyone is free to fold the two together tomorrow.
    /// </para>
    /// <para>
    /// The reason not to is that folding them means <em>inverting</em> them. The lines and the
    /// failure can only be reported together by the method that does the reading, so the shared
    /// core would have to be this one, and <see cref="ReadZstdJsonLinesAsync"/> would become a
    /// wrapper that rethrows what this one caught — which costs an
    /// <see cref="System.Runtime.ExceptionServices.ExceptionDispatchInfo"/> to keep the original
    /// stack trace, and gets a worse one without. That is more machinery than the five lines of
    /// decode it removes. And these are the harness's oracle: five straight lines a reader can
    /// check against the API's documented behaviour at a glance are worth more here than one fewer
    /// duplicate.
    /// </para>
    /// </remarks>
    /// <param name="response">The response.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The lines that decompressed, and what stopped the read, or <see langword="null"/>.</returns>
    public static async Task<(IReadOnlyList<string> Lines, Exception? Failure)> ReadZstdJsonLinesUntilEndAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var decompressor = new ZstdSharp.DecompressionStream(body);
        using var reader = new StreamReader(decompressor, Encoding.UTF8);

        var lines = new List<string>();

        try
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                lines.Add(line);
            }
        }
        catch (Exception failure) when (failure is IOException or HttpRequestException)
        {
            return (lines, failure);
        }

        return (lines, null);
    }

    /// <summary>
    /// Reads a body until it ends or the transfer fails, returning both what arrived and why it
    /// stopped.
    /// </summary>
    /// <remarks>
    /// Both halves matter for the dropped-connection case: that the read failed says the connection
    /// went, and the bytes that arrived first say it went <em>mid-body</em> rather than before the
    /// body started.
    /// </remarks>
    /// <param name="body">The response body stream, possibly already partly read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bytes received, and the failure that ended the read, or <see langword="null"/>.</returns>
    public static async Task<(byte[] Received, Exception? Failure)> ReadUntilEndAsync(
        Stream body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        using var received = new MemoryStream();
        var buffer = new byte[256];

        try
        {
            int read;
            while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                received.Write(buffer, 0, read);
            }
        }
        catch (Exception failure) when (failure is IOException or HttpRequestException)
        {
            return (received.ToArray(), failure);
        }

        return (received.ToArray(), null);
    }

    /// <summary>Releases the underlying <see cref="HttpClient"/>.</summary>
    public void Dispose() => _http.Dispose();

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    private HttpRequestMessage Request(
        HttpMethod method,
        string slug,
        IEnumerable<KeyValuePair<string, string>>? query,
        string accept,
        string userAgent = UserAgent)
    {
        ArgumentException.ThrowIfNullOrEmpty(slug);

        var path = new StringBuilder()
            .Append('v')
            .Append(MockHistoricalGateway.ApiVersion.ToString(CultureInfo.InvariantCulture))
            .Append('/')
            .Append(slug);

        if (query is not null)
        {
            var first = true;
            foreach (var parameter in query)
            {
                path.Append(first ? '?' : '&')
                    .Append(Uri.EscapeDataString(parameter.Key))
                    .Append('=')
                    .Append(Uri.EscapeDataString(parameter.Value));
                first = false;
            }
        }

        var request = new HttpRequestMessage(method, new Uri(_baseUrl, path.ToString()));
        request.Headers.TryAddWithoutValidation("Accept", accept);
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        return request;
    }
}
