using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Dbn.Internal;
using DatabentoDotNet.Historical.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// System.Text.Encoding and DatabentoDotNet.Dbn.Encoding collide by simple name, and this file
// imports both namespaces — the codec's for VersionUpgradePolicy and the zstd seam. Everything
// here means the BCL's; the alias says so once rather than at each use. LiveClient carries the
// same pair of aliases for the same collision.
using Encoding = System.Text.Encoding;

namespace DatabentoDotNet.Historical;

/// <summary>
/// A client for Databento's historical HTTPS API: metadata, symbology, timeseries queries older
/// than 24 hours, and batch jobs.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>historical::Client</c> (<c>historical/client.rs</c>) — its
/// <c>request</c>, <c>check_warnings</c>, <c>check_http_error</c>, <c>handle_response</c> and
/// <c>handle_zstd_jsonl_response</c>, which together are the whole HTTP transport every endpoint
/// sits on. This type is that transport and nothing else: the endpoints themselves arrive with
/// the subclient facades that group them —
/// <see href="https://github.com/jerbersoft/databentodotnet/issues/36">#36</see>–<see href="https://github.com/jerbersoft/databentodotnet/issues/39">#39</see>.
/// A facade with no endpoints on it would be a public empty class, so none is declared here.
/// </para>
/// <para>
/// <b>Thread-safe for concurrent requests once configured, and deliberately so — the opposite
/// call from <c>LiveClient</c>.</b> Everything below the public surface is one
/// <see cref="HttpClient"/>, which is documented as safe for concurrent use, plus properties that
/// are <see langword="init"/>-only and therefore frozen before the first request. Several
/// requests may be in flight on one instance, and the intended use is one long-lived client for
/// the life of a process rather than one per call. <c>LiveClient</c> is not thread-safe because
/// one live connection is one conversation with a gateway and its record loop is a single reader
/// by construction; nothing of the kind is true of independent HTTP requests, so nothing here
/// pretends otherwise.
/// </para>
/// <para>
/// <b>No builder.</b> Upstream's <c>ClientBuilder&lt;AK&gt;</c> is generic type-state whose only
/// purpose is to make "no API key" unrepresentable — <c>build()</c> exists only on
/// <c>ClientBuilder&lt;ApiKey&gt;</c>. C# 11 <c>required</c> init properties do exactly that
/// natively, checked by the compiler at every construction site. See PORTING.md §2, and
/// <c>LiveClient</c> for the precedent in this repo.
/// </para>
/// <code>
/// await using var client = new HistoricalClient { ApiKey = new ApiKey(key) };
///
/// var datasets = await client.SendJsonAsync(
///     HttpMethod.Get, "metadata.list_datasets", parameters: null, MyJson.Default.ListString, ct);
/// </code>
/// <para>
/// <b>The transport is public, and that is a decision rather than an omission.</b>
/// <see cref="SendAsync"/> and the two readers are what the endpoint facades are built from, and
/// they are also the escape hatch for an endpoint this library has not wrapped yet: the API has
/// twenty-three, and a caller who needs the twenty-fourth the week it ships should not have to
/// wait for a release. Upstream's equivalents are <c>pub(crate)</c>; this repo declares no
/// <c>InternalsVisibleTo</c> anywhere, so "internal but tested" is not a shape available here.
/// </para>
/// </remarks>
public sealed class HistoricalClient : IAsyncDisposable
{
    /// <summary>The API version every request path is prefixed with.</summary>
    public const int ApiVersion = 0;

    /// <summary>The <c>Accept</c> every request carries unless one is given for it.</summary>
    public const string JsonMediaType = "application/json";

    private const string WarningHeader = "X-Warning";
    private const string RequestIdHeader = "request-id";
    private const string UserAgentHeader = "User-Agent";
    private const string BasicScheme = "Basic";
    private const string DetailProperty = "detail";

    private readonly Lazy<HttpClient> _http;
    private readonly Lazy<ILogger> _logger;
    private readonly Uri? _baseUrl;

    private volatile bool _disposed;

    /// <summary>Creates a client. Configure it through the init properties.</summary>
    /// <remarks>
    /// <para>
    /// <b>Both fields are lazy, and not for cost.</b> An <see langword="init"/> accessor runs
    /// <em>after</em> the constructor body, so <see cref="ApiKey"/> does not exist yet at the
    /// point where an eager constructor would want to build the <see cref="HttpClient"/> and its
    /// <c>Authorization</c> header from it. Deferring to first use is what makes
    /// <see langword="required"/> init properties and a fully configured <see cref="HttpClient"/>
    /// compatible at all. <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> because this
    /// type is documented as safe for concurrent requests: two threads racing into the first
    /// request must get one client, not two, and the loser must not see a half-built one.
    /// </para>
    /// </remarks>
    public HistoricalClient()
    {
        _http = new Lazy<HttpClient>(CreateHttpClient, LazyThreadSafetyMode.ExecutionAndPublication);
        _logger = new Lazy<ILogger>(CreateLogger, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The API key to authenticate with. Validated when it is constructed.</summary>
    /// <remarks>
    /// The type, never a <see langword="string"/>. That is what keeps the key out of a log line
    /// or an exception message structurally rather than carefully:
    /// <see cref="DatabentoDotNet.ApiKey.ToString"/> is redacted, so formatting the object that
    /// holds it cannot leak it. The key reaches the wire in exactly one place — the
    /// <c>Authorization</c> header built in this file — and nowhere else, not as a query
    /// parameter and not as a form field.
    /// </remarks>
    public required ApiKey ApiKey { get; init; }

    /// <summary>The gateway to send requests to. Defaults to <see cref="HistoricalGateway.Bo1"/>.</summary>
    /// <remarks>Ignored when <see cref="BaseUrl"/> is set.</remarks>
    public HistoricalGateway Gateway { get; init; } = HistoricalGateway.Bo1;

    /// <summary>
    /// How to handle DBN data from an older version than this library decodes natively. Defaults
    /// to <see cref="VersionUpgradePolicy.UpgradeToV3"/>, as upstream's builder does.
    /// </summary>
    /// <remarks>
    /// Carried here because it is a property of the client rather than of a call — upstream keeps
    /// it on <c>Client</c> and reads it from <c>timeseries.get_range</c> and the batch
    /// endpoints — but nothing in this transport consults it. It is the DBN <em>decoder's</em>
    /// input, and the first request whose body is DBN rather than JSON arrives with
    /// <see href="https://github.com/jerbersoft/databentodotnet/issues/38">#38</see>.
    /// </remarks>
    public VersionUpgradePolicy UpgradePolicy { get; init; } = VersionUpgradePolicy.UpgradeToV3;

    /// <summary>
    /// A base URL to send requests to instead of <see cref="Gateway"/>'s, or
    /// <see langword="null"/> to use the gateway.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The advanced knob, as upstream documents <c>with_url</c>: it exists for a test harness or
    /// a proxy, and a caller pointing a production client somewhere other than Databento's own
    /// gateway has almost certainly made a mistake. It is how this library's own tests reach
    /// <c>MockHistoricalGateway</c>.
    /// </para>
    /// <para>
    /// <b>A path on this URL is preserved, which takes explicit work.</b>
    /// <c>new Uri(new Uri("http://host/api"), "v0/x")</c> is <c>http://host/v0/x</c> — combining
    /// a relative URI with a base whose path has no trailing slash <em>replaces</em> that last
    /// segment rather than appending to it, so a proxy mounted at <c>/api</c> would silently
    /// lose its mount point. The effective base address is therefore normalised to end in
    /// <c>/</c> before anything is resolved against it.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The URL is not absolute.</exception>
    public Uri? BaseUrl
    {
        get => _baseUrl;
        init
        {
            if (value is not null && !value.IsAbsoluteUri)
            {
                throw new ArgumentException(
                    "The base URL must be absolute — scheme and host included.", nameof(value));
            }

            _baseUrl = value;
        }
    }

    /// <summary>
    /// Text to append to this library's <c>User-Agent</c>, identifying the application built on
    /// it, or <see langword="null"/> to send the library's own user agent alone.
    /// </summary>
    /// <remarks>
    /// Port of upstream's <c>user_agent_ext</c> (<c>client.rs:384-387</c>), which composes it the
    /// same way: the library's user agent, a space, then this. The composed header goes through
    /// <see cref="HttpHeaders.Add(string, string?)"/> — the <em>validating</em> overload — so an
    /// extension that is not a well-formed sequence of user-agent products and comments is
    /// rejected when the first request is sent rather than silently reaching Databento's logs
    /// malformed.
    /// </remarks>
    public string? UserAgentExtension { get; init; }

    /// <summary>
    /// Where to send this client's log messages, or <see langword="null"/> for none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is how the API's <c>X-Warning</c> header surfaces, and it is the only route it has:
    /// the alternative — a warnings property on every response — means every one of the API's
    /// twenty-three endpoints returns a wrapper type instead of its payload, and every caller
    /// unwrapping, to carry a header that is almost always absent. That was rejected on cost, not
    /// on taste. See <c>Internal/HistoricalLog.cs</c> for the messages and their event ids.
    /// </para>
    /// <para>
    /// Left <see langword="null"/>, this resolves to <see cref="NullLogger.Instance"/> — no
    /// logging configured means no logging done, and nothing is formatted or allocated for a
    /// caller who never asked.
    /// </para>
    /// </remarks>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>
    /// The path a slug is served at, relative to the base URL: <c>v0/{slug}</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Relative, and with no leading slash</b>, because that is the only form that composes:
    /// <c>new Uri(baseAddress, PathFor(slug))</c> appends to the base address' path, where a
    /// leading slash would resolve against the authority and discard it.
    /// </para>
    /// <para>
    /// <c>MockHistoricalGateway.PathFor</c> in this repo's test project returns the
    /// <em>absolute</em> path, with its leading slash, because its job is to match a recorded
    /// <c>RecordedRequest.Path</c>. Two different jobs that happen to share a name and a version
    /// segment; neither calls the other, deliberately. The harness is written from the API's
    /// documented behaviour rather than from this library, and a harness that computed the path
    /// the same way the client does could not catch the client computing it wrongly.
    /// </para>
    /// </remarks>
    /// <param name="slug">
    /// The API slug — <c>metadata.list_datasets</c>, <c>timeseries.get_range</c>. Slashes are
    /// allowed, so a batch file's path — <c>batch/download/{user}/{job}/{file}</c> — is a slug
    /// like any other.
    /// </param>
    /// <returns>The relative path.</returns>
    /// <exception cref="ArgumentException"><paramref name="slug"/> is null or empty.</exception>
    public static string PathFor(string slug)
    {
        ArgumentException.ThrowIfNullOrEmpty(slug);

        return $"v{ApiVersion.ToString(CultureInfo.InvariantCulture)}/{slug}";
    }

    /// <summary>
    /// Sends one request to <c>v0/{slug}</c> and returns the response, having already logged any
    /// server warnings it carried and thrown if the API rejected it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one primitive every endpoint is built from. Port of upstream's <c>request</c>
    /// (<c>client.rs:139-151</c>) together with the <c>check_warnings</c> then
    /// <c>check_http_error</c> pair that every one of its response handlers opens with
    /// (<c>client.rs:205-206</c>).
    /// </para>
    /// <para>
    /// <b><paramref name="parameters"/> travel by HTTP method, not by endpoint.</b> Upstream has
    /// two families — <c>add_to_query</c> and <c>add_to_form</c> — and which one an endpoint uses
    /// is decided entirely by its method: every <c>GET</c> in the crate queries and every
    /// <c>POST</c> forms. So a <c>POST</c> sends an
    /// <c>application/x-www-form-urlencoded</c> body and anything else sends a query string. One
    /// rule, and no per-endpoint table for a future endpoint to be missing from — and no method
    /// for which the parameters would silently go nowhere.
    /// </para>
    /// <para>
    /// A <see langword="null"/> or empty <paramref name="parameters"/> on a <c>POST</c> is an
    /// <em>empty form</em>, not an absent body. The distinction is on the wire: an absent body
    /// carries no <c>Content-Type</c>, and a server that branches on
    /// <c>application/x-www-form-urlencoded</c> — Databento's does, and so does this repo's
    /// harness — sees a different request.
    /// </para>
    /// <para>
    /// <b>Values are percent-encoded with <see cref="Uri.EscapeDataString(string)"/></b>, which escapes
    /// every reserved character rather than only the ones a URI parser would choke on. That
    /// matters for one parameter above all: a <c>Symbols</c> list renders as <c>AAPL,MSFT</c>,
    /// and the comma has to arrive as <c>%2C</c> — a comma is a sub-delimiter, and a server
    /// splitting on raw ones would see a differently shaped request rather than a rejected one.
    /// </para>
    /// <para>
    /// <b>The response is returned with its headers read and its body still on the socket</b>
    /// (<see cref="HttpCompletionOption.ResponseHeadersRead"/>), and it is the caller's to
    /// dispose. That is not an optimisation:
    /// <see href="https://github.com/jerbersoft/databentodotnet/issues/38">#38</see> streams
    /// bodies larger than memory, and buffering the whole response before returning it is exactly
    /// what that endpoint cannot afford. Establishing it here means no endpoint has to remember
    /// to.
    /// </para>
    /// <para>
    /// Nothing leaks on the throwing path: the error body is read, the response disposed, and
    /// only then is the exception raised.
    /// </para>
    /// <para>
    /// <b><paramref name="cancellationToken"/> is last, after <paramref name="accept"/>.</b> Both
    /// are optional, and the reverse order — the one this method was specified with — does not
    /// build here: CA1068 requires a <see cref="CancellationToken"/> to be the last parameter, and
    /// this repo treats warnings as errors. There is no suppression of it, and the ordering is
    /// not a matter of taste worth one: a caller who passes a token positionally into an
    /// <c>accept</c> slot gets a compile error rather than a request that quietly cannot be
    /// cancelled.
    /// </para>
    /// </remarks>
    /// <param name="method">The HTTP method. <c>POST</c> forms its parameters; anything else queries them.</param>
    /// <param name="slug">The API slug, without the version prefix.</param>
    /// <param name="parameters">The request parameters, or <see langword="null"/> for none.</param>
    /// <param name="accept">
    /// An <c>Accept</c> for this request alone, overriding the client's
    /// <see cref="JsonMediaType"/> default, or <see langword="null"/> to send the default. It has
    /// exactly one caller in this library: <c>timeseries.get_range</c> asks for
    /// <c>application/octet-stream</c> and is the only request in the whole historical API whose
    /// response is not JSON — upstream says as much in its own comment at
    /// <c>historical/timeseries.rs:141</c>. It is set on the request rather than on the client's
    /// default headers, which every request this client will ever send shares.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The response, headers read and body not yet buffered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="method"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="slug"/> is null or empty.</exception>
    /// <exception cref="FormatException"><paramref name="accept"/> is not a media type.</exception>
    /// <exception cref="DatabentoApiException">The API answered with a non-success status.</exception>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string slug,
        IEnumerable<KeyValuePair<string, string>>? parameters,
        string? accept = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrEmpty(slug);

        var http = Http;
        var isPost = method.Equals(HttpMethod.Post);

        // BaseAddress is set by CreateHttpClient and never cleared, so it is not null here.
        var url = new Uri(http.BaseAddress!, isPost ? PathFor(slug) : PathAndQueryFor(slug, parameters));

        using var request = new HttpRequestMessage(method, url);

        if (isPost)
        {
            request.Content = new FormUrlEncodedContent(parameters ?? []);
        }

        if (accept is not null)
        {
            // Setting Accept on the request suppresses the client's default for this request
            // rather than adding to it: HttpClient copies a default header across only when the
            // request does not already carry that header.
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        }

        var response = await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        var returning = false;
        try
        {
            // Warnings first, then the error check — upstream's order, and the right one: a
            // failing response can still carry an X-Warning, and the warning is often why it
            // failed.
            LogWarnings(response, _logger.Value);

            if (response.IsSuccessStatusCode)
            {
                returning = true;
                return response;
            }

            throw await CreateApiExceptionAsync(response, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!returning)
            {
                response.Dispose();
            }
        }
    }

    /// <summary>
    /// Reads <paramref name="response"/>'s body as one JSON document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of the tail of upstream's <c>handle_response</c> (<c>client.rs:207-209</c>). It does
    /// not dispose <paramref name="response"/>; <see cref="SendJsonAsync"/> is the composed form
    /// that does.
    /// </para>
    /// <para>
    /// <b>A <see cref="JsonTypeInfo{T}"/> rather than a plain <typeparamref name="T"/>, and that
    /// signature is not negotiable here.</b> This assembly is trim- and AOT-analysed with
    /// warnings as errors, so the reflection-based <see cref="JsonSerializer"/> overloads do not
    /// merely allocate a metadata cache at run time — they fail the build (IL2026/IL3050). Each
    /// endpoint therefore supplies its own <c>[JsonSerializable]</c> context and passes the
    /// generated type info in, which is also what lets a consumer publish this library
    /// AOT-compiled at all.
    /// </para>
    /// <para>
    /// <b><see langword="static"/>, because it needs nothing from the client</b> — the response is
    /// the caller's and the decode is pure. CA1822 is what raised the question and this repo
    /// treats warnings as errors, but the answer would be the same without it: the alternative
    /// was to give the method a use for instance state it does not have, and inventing a
    /// disposed-client guard for a read that touches no client state would make
    /// <c>await using</c>-scoping a client and then finishing a response you already hold throw
    /// for no reason.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The type to deserialize into.</typeparam>
    /// <param name="response">The response to read.</param>
    /// <param name="typeInfo">The source-generated metadata for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The deserialized body.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The body is not valid JSON, or is the literal <c>null</c>.</exception>
    public static async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(typeInfo);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var value = await JsonSerializer
                .DeserializeAsync(stream, typeInfo, cancellationToken)
                .ConfigureAwait(false);

            if (value is null)
            {
                // The body was the JSON literal `null`. Upstream's serde rejects that for any
                // type that is not an Option, and so does this: a caller asked for a payload and
                // the API said there is none, which is a decode failure rather than a value.
                throw new JsonException(
                    $"The response body was the JSON literal 'null', which is not a {typeof(T).Name}.");
            }

            return value;
        }
    }

    /// <summary>
    /// Reads <paramref name="response"/>'s body as a zstd frame containing one JSON document per
    /// line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of upstream's <c>handle_zstd_jsonl_response</c> (<c>client.rs:212-229</c>). The frame
    /// is in the body rather than announced in <c>Content-Encoding</c>, which is why this
    /// decompresses the stream itself instead of leaving it to <see cref="HttpClient"/>'s
    /// automatic decompression — there is no <c>Content-Encoding</c> for that to act on. Blank
    /// lines are skipped, so the trailing newline a line-oriented writer leaves behind is not a
    /// record.
    /// </para>
    /// <para>
    /// <b>No historical endpoint calls this, and that is not an oversight.</b> Upstream's
    /// <c>handle_zstd_jsonl_response</c> has zero call sites in <c>src/historical/</c>; all four
    /// are in <c>src/reference/</c> — <c>security.rs:49</c> and <c>:76</c>,
    /// <c>corporate.rs:58</c>, <c>adjustment.rs:50</c> — which is M4, the reference-data
    /// milestone. It lives here because upstream defines it here, so a reader comparing the two
    /// files finds it where they expect, and because the harness already serves exactly this
    /// shape, so it could be tested against an oracle written before it existed.
    /// </para>
    /// <para>
    /// Takes a <see cref="JsonTypeInfo{T}"/> for the reason given on
    /// <see cref="ReadJsonAsync"/>: the reflection-based overloads fail this assembly's build.
    /// It is <see langword="static"/> for the reason given there too.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The type each line deserializes into.</typeparam>
    /// <param name="response">The response to read.</param>
    /// <param name="typeInfo">The source-generated metadata for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One element per non-blank line, in the order they arrived.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">A line is not valid JSON, or is the literal <c>null</c>.</exception>
    public static async Task<IReadOnlyList<T>> ReadZstdJsonLinesAsync<T>(
        HttpResponseMessage response,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(typeInfo);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var frame = ZstdDecompressor.Decompress(stream, leaveOpen: true);
            await using (frame.ConfigureAwait(false))
            {
                using var reader = new StreamReader(
                    frame, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

                var values = new List<T>();
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var value = JsonSerializer.Deserialize(line, typeInfo);
                    if (value is null)
                    {
                        throw new JsonException(
                            $"A line of the response was the JSON literal 'null', which is not a {typeof(T).Name}.");
                    }

                    values.Add(value);
                }

                return values;
            }
        }
    }

    /// <summary>
    /// Sends a request and reads its body as one JSON document — <see cref="SendAsync"/> and
    /// <see cref="ReadJsonAsync"/> composed, with the response disposed.
    /// </summary>
    /// <remarks>
    /// The shape almost every endpoint wants, and upstream's <c>handle_response</c> end to end.
    /// </remarks>
    /// <typeparam name="T">The type to deserialize into.</typeparam>
    /// <param name="method">The HTTP method.</param>
    /// <param name="slug">The API slug, without the version prefix.</param>
    /// <param name="parameters">The request parameters, or <see langword="null"/> for none.</param>
    /// <param name="typeInfo">The source-generated metadata for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The deserialized body.</returns>
    /// <exception cref="DatabentoApiException">The API answered with a non-success status.</exception>
    public async Task<T> SendJsonAsync<T>(
        HttpMethod method,
        string slug,
        IEnumerable<KeyValuePair<string, string>>? parameters,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(method, slug, parameters, accept: null, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(response, typeInfo, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a request and reads its body as zstd-framed JSON lines — <see cref="SendAsync"/> and
    /// <see cref="ReadZstdJsonLinesAsync"/> composed, with the response disposed.
    /// </summary>
    /// <remarks>
    /// The shape the reference-data endpoints want; see <see cref="ReadZstdJsonLinesAsync"/> for
    /// why nothing in M3 calls it.
    /// </remarks>
    /// <typeparam name="T">The type each line deserializes into.</typeparam>
    /// <param name="method">The HTTP method.</param>
    /// <param name="slug">The API slug, without the version prefix.</param>
    /// <param name="parameters">The request parameters, or <see langword="null"/> for none.</param>
    /// <param name="typeInfo">The source-generated metadata for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>One element per non-blank line, in the order they arrived.</returns>
    /// <exception cref="DatabentoApiException">The API answered with a non-success status.</exception>
    public async Task<IReadOnlyList<T>> SendZstdJsonLinesAsync<T>(
        HttpMethod method,
        string slug,
        IEnumerable<KeyValuePair<string, string>>? parameters,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(method, slug, parameters, accept: null, cancellationToken).ConfigureAwait(false);
        return await ReadZstdJsonLinesAsync(response, typeInfo, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Releases the underlying <see cref="HttpClient"/>.</summary>
    /// <remarks>
    /// Idempotent, and safe on a client that never sent a request: the
    /// <see cref="HttpClient"/> is built on first use, so there is nothing to release until one
    /// has been. Using the client after this throws
    /// <see cref="ObjectDisposedException"/> rather than quietly building a second one.
    /// </remarks>
    /// <returns>A completed task; there is no asynchronous work to do.</returns>
    public ValueTask DisposeAsync()
    {
        _disposed = true;

        if (_http.IsValueCreated)
        {
            _http.Value.Dispose();
        }

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private HttpClient Http
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _http.Value;
        }
    }

    private static string PathAndQueryFor(string slug, IEnumerable<KeyValuePair<string, string>>? parameters)
    {
        var path = PathFor(slug);
        if (parameters is null)
        {
            return path;
        }

        var builder = new StringBuilder(path);
        var separator = '?';
        foreach (var (name, value) in parameters)
        {
            builder
                .Append(separator)
                .Append(Uri.EscapeDataString(name))
                .Append('=')
                .Append(Uri.EscapeDataString(value));

            separator = '&';
        }

        return builder.ToString();
    }

    /// <summary>
    /// Logs every warning the response's <c>X-Warning</c> headers carry. Port of upstream's
    /// <c>check_warnings</c> (<c>client.rs:238-251</c>), and of nothing more.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A malformed header is logged and the request continues, because a warning that breaks the
    /// call it was attached to is worse than no warning at all.
    /// </para>
    /// <para>
    /// One header's warnings are collected before any of them is logged, which is what upstream's
    /// <c>from_slice::&lt;Vec&lt;String&gt;&gt;</c> does implicitly: an array whose third element
    /// is a number is a malformed header, not two good warnings followed by a complaint.
    /// </para>
    /// </remarks>
    private static void LogWarnings(HttpResponseMessage response, ILogger logger)
    {
        // A response may carry the header more than once, and each occurrence is its own JSON
        // array. HttpResponseMessage.Headers hands back one string per occurrence.
        if (!response.Headers.TryGetValues(WarningHeader, out var headers))
        {
            return;
        }

        foreach (var header in headers)
        {
            List<string> warnings;
            try
            {
                warnings = ParseWarnings(header);
            }
            catch (JsonException exception)
            {
                HistoricalLog.MalformedWarningHeader(logger, exception);
                continue;
            }

            foreach (var warning in warnings)
            {
                HistoricalLog.ServerWarning(logger, warning);
            }
        }
    }

    private static List<string> ParseWarnings(string header)
    {
        // JsonDocument rather than a serializer: no reflection, no JsonTypeInfo to declare for a
        // shape that is one line to walk, and the header is a string we already hold.
        using var document = JsonDocument.Parse(header);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"The {WarningHeader} header is not a JSON array.");
        }

        var warnings = new List<string>(document.RootElement.GetArrayLength());
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                throw new JsonException($"The {WarningHeader} header is not a JSON array of strings.");
            }

            warnings.Add(element.GetString()!);
        }

        return warnings;
    }

    /// <summary>
    /// Builds the exception for a non-success response. Port of upstream's
    /// <c>check_http_error</c> (<c>client.rs:157-200</c>).
    /// </summary>
    private async Task<DatabentoApiException> CreateApiExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var requestId = response.Headers.TryGetValues(RequestIdHeader, out var values)
            ? values.FirstOrDefault()
            : null;

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return ParseError(response.StatusCode, requestId, body);
        }
        catch (JsonException exception)
        {
            // The third shape, and the one that is not a shape: whatever the server sent becomes
            // the message verbatim rather than being swallowed. An HTML error page from a proxy
            // in front of the API arrives here, and it is usually the only thing that says so.
            //
            // Upstream reaches this arm through serde failing to match either variant of an
            // untagged enum; ParseError reaches it by throwing a JsonException describing what
            // did not match. The exception is not control flow for its own sake — the log
            // message this catch writes is *about* a deserialization failure and has nowhere
            // else to get one from.
            HistoricalLog.UnparseableErrorBody(_logger.Value, exception);

            return new DatabentoApiException(
                response.StatusCode, requestId, errorCase: null, body, docsUrl: null, payload: null);
        }
    }

    /// <summary>
    /// Parses one of the two documented error bodies, or throws <see cref="JsonException"/> if
    /// this is neither.
    /// </summary>
    /// <remarks>
    /// Upstream tells the two apart with serde's untagged union, which is one <c>detail</c> field
    /// that is a string in the simple shape and an object in the business one. Here that is a
    /// single <see cref="JsonElement.ValueKind"/> check. <see cref="JsonDocument"/> and
    /// <see cref="JsonElement"/> only: the reflection-based serializer overloads fail this
    /// assembly's trim and AOT analysis, and this shape does not warrant a source-generated
    /// context of its own when walking it is six lines.
    /// </remarks>
    private static DatabentoApiException ParseError(HttpStatusCode statusCode, string? requestId, string body)
    {
        using var document = JsonDocument.Parse(body);

        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty(DetailProperty, out var detail))
        {
            throw new JsonException($"The error body has no '{DetailProperty}' property.");
        }

        if (detail.ValueKind == JsonValueKind.String)
        {
            return new DatabentoApiException(
                statusCode,
                requestId,
                errorCase: null,
                detail.GetString()!,
                docsUrl: null,
                payload: null);
        }

        if (detail.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"'{DetailProperty}' is neither a string nor an object.");
        }

        // `case` and `payload` are optional, `message` and `docs` are not — upstream's
        // BusinessErrorDetails types them Option<String> / Option<HashMap<..>> and String, so a
        // detail object missing either of the last two matches neither variant and falls through
        // to the verbatim-body arm.
        return new DatabentoApiException(
            statusCode,
            requestId,
            OptionalString(detail, "case"),
            RequiredString(detail, "message"),
            RequiredString(detail, "docs"),
            OptionalPayload(detail));
    }

    private static string? OptionalString(JsonElement detail, string name)
    {
        if (!detail.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : throw new JsonException($"'{DetailProperty}.{name}' is not a string.");
    }

    private static string RequiredString(JsonElement detail, string name) =>
        OptionalString(detail, name)
        ?? throw new JsonException($"'{DetailProperty}.{name}' is missing.");

    // Dictionary rather than IReadOnlyDictionary: this is a private helper whose one caller hands
    // the result straight to a constructor that takes the interface, so the abstraction buys
    // nothing here and CA1859 is right to say so.
    private static Dictionary<string, JsonElement>? OptionalPayload(JsonElement detail)
    {
        const string Name = "payload";

        if (!detail.TryGetProperty(Name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"'{DetailProperty}.{Name}' is not an object.");
        }

        // Not cloned here: DatabentoApiException clones every element it is handed, precisely
        // because the JsonDocument backing them is disposed as this method's caller returns.
        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            payload[property.Name] = property.Value;
        }

        return payload;
    }

    private HttpClient CreateHttpClient()
    {
        // No handler of our own. HttpClient's automatic decompression is off by default and would
        // be irrelevant if it were not: the zstd frame the API returns is in the body and nothing
        // announces it in Content-Encoding, so ReadZstdJsonLinesAsync unwraps it itself.
        var http = new HttpClient { BaseAddress = EffectiveBaseUrl() };

        // HTTP Basic with the API key as the username and an *empty* password — a credential that
        // ends in a colon with nothing after it. Not a bearer token, and never a query parameter:
        // this line is the only place in this library where the key reaches the wire.
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            BasicScheme,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(ApiKey.Value + ":")));

        // The validating overload, deliberately. UserAgent.Value's third token —
        // `.NET osx-arm64` — has no '/', which HttpClient parses as a bare product rather than
        // rejecting; verified against both the bare form and one with an extension appended.
        // A malformed UserAgentExtension supplied by a consumer is worth throwing over here,
        // where the caller can see it, rather than sending Databento a broken header.
        http.DefaultRequestHeaders.Add(
            UserAgentHeader,
            UserAgentExtension is null ? UserAgent.Value : UserAgent.Value + " " + UserAgentExtension);

        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(JsonMediaType));

        // Infinite, and this is load-bearing. HttpClient.Timeout defaults to 100 seconds and
        // covers the *whole* operation including reading the body, so a default-configured client
        // aborts every timeseries.get_range download whose body outlasts that budget — mid-stream,
        // as a TaskCanceledException that looks like a cancellation rather than like a timeout.
        // Per-call budgets belong on a linked CancellationTokenSource, which is both the modern
        // .NET recommendation and the only form that does not name a banned BCL type.
        //
        // Nothing asserts this: comparing two TimeSpans calls TimeSpan.op_Equality, which
        // BannedSymbols.txt forbids, so there is no reachable member for a test to read. The
        // assignment itself is clean — RS0030 flags members and operators of System.TimeSpan, not
        // a value that merely has that type.
        http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;

        return http;
    }

    /// <summary>
    /// The base URL requests resolve against: <see cref="BaseUrl"/> or <see cref="Gateway"/>'s,
    /// normalised to end in <c>/</c>.
    /// </summary>
    /// <remarks>
    /// The normalisation belongs here rather than on <see cref="HistoricalGateway"/>: a gateway
    /// URL is a bare authority, which <see cref="Uri"/> already normalises to a root path, and it
    /// is a consumer-supplied <see cref="BaseUrl"/> carrying a path that would otherwise lose its
    /// last segment when <c>v0/{slug}</c> is resolved against it.
    /// </remarks>
    private Uri EffectiveBaseUrl()
    {
        var url = BaseUrl ?? Gateway.ToUri();

        return url.AbsoluteUri.EndsWith('/')
            ? url
            : new Uri(url.AbsoluteUri + "/", UriKind.Absolute);
    }

    private ILogger CreateLogger()
    {
        // Typed as ILogger before the null-coalesce on purpose: ILogger<HistoricalClient> and
        // NullLogger share no common type the operator can infer, so `?? NullLogger.Instance`
        // against the generic result does not compile.
        ILogger? logger = LoggerFactory?.CreateLogger<HistoricalClient>();

        return logger ?? NullLogger.Instance;
    }
}
