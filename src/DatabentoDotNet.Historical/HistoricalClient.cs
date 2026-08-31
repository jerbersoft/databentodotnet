using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
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
///
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
/// twenty (ROADMAP.md §5 lists them), and a caller who needs the twenty-first the week it ships
/// should not have to wait for a release. Upstream's equivalents are <c>pub(crate)</c>; this repo
/// declares no <c>InternalsVisibleTo</c> anywhere, so "internal but tested" is not a shape
/// available here.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// using DatabentoDotNet;
/// using DatabentoDotNet.Dbn;
/// using DatabentoDotNet.Historical;
/// using NodaTime;
///
/// // One long-lived client for the life of the process, not one per call: this type is safe for
/// // concurrent requests once configured.
/// await using var client = new HistoricalClient
/// {
///     ApiKey = new ApiKey(Environment.GetEnvironmentVariable("DATABENTO_API_KEY")!),
/// };
///
/// var request = new GetRangeParams
/// {
///     Dataset = "GLBX.MDP3",
///     Symbols = Symbols.From("ESH4"),
///     Schema = Schema.Trades,
///     DateTimeRange = DateRange.OnDay(new LocalDate(2024, 1, 2)).ToDateTimeRange(),
///     Limit = 10,
/// };
///
/// // Free, and it prices the request below rather than one assembled a second time by hand — which
/// // is the whole reason ToQuery() exists.
/// decimal cost = await client.Metadata.GetCostAsync(request.ToQuery());
/// if (cost &gt; 0.01m)
/// {
///     Console.WriteLine($"${cost} is more than this program will spend.");
///     return;
/// }
///
/// // This one bills.
/// await using var reader = await client.Timeseries.GetRangeAsync(request);
/// await foreach (OwnedRecord record in reader.ReadRecordsAsync())
/// {
///     if (record.TryGet(out TradeMsg trade))
///     {
///         Console.WriteLine($"{DbnTime.ToInstant(trade.IndexTs)} {trade.Price} x {trade.Size}");
///     }
/// }
/// </code>
/// </example>
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
    private readonly Lazy<MetadataClient> _metadata;
    private readonly Lazy<SymbologyClient> _symbology;
    private readonly Lazy<TimeseriesClient> _timeseries;
    private readonly Lazy<BatchClient> _batch;
    private readonly Uri? _baseUrl;

    private volatile bool _disposed;

    /// <summary>Creates a client. Configure it through the init properties.</summary>
    /// <remarks>
    /// <para>
    /// <b>All three fields are lazy, and not for cost.</b> An <see langword="init"/> accessor runs
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
        _metadata = new Lazy<MetadataClient>(() => new MetadataClient(this), LazyThreadSafetyMode.ExecutionAndPublication);
        _symbology = new Lazy<SymbologyClient>(() => new SymbologyClient(this), LazyThreadSafetyMode.ExecutionAndPublication);
        _timeseries = new Lazy<TimeseriesClient>(() => new TimeseriesClient(this), LazyThreadSafetyMode.ExecutionAndPublication);
        _batch = new Lazy<BatchClient>(() => new BatchClient(this), LazyThreadSafetyMode.ExecutionAndPublication);
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
    /// The <see cref="HttpMessageHandler"/> to send through, or <see langword="null"/> to let
    /// <see cref="HttpClient"/> build its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists for <c>IHttpClientFactory</c>, and the defect it fixes is not socket
    /// exhaustion.</b> <see cref="HttpClient"/>'s own handler is a
    /// <see cref="System.Net.Http.SocketsHttpHandler"/> whose <c>PooledConnectionLifetime</c>
    /// defaults to infinite, so a client held as a singleton in a host that stays up for weeks
    /// keeps talking to whatever address <c>hist.databento.com</c> resolved to on its first
    /// request. A handler supplied here can bound that; nothing else on this type can.
    /// </para>
    /// <para>
    /// <b>A handler, not an <see cref="HttpClient"/>, and that is the whole design.</b> Everything
    /// this client puts on a request it builds — HTTP Basic from the <see cref="ApiKey"/>, the
    /// validated <c>User-Agent</c>, the <c>Accept</c> header, the base address — is still built
    /// here and still built once. Handing over the whole client would mean either mutating an
    /// object this type does not own to attach the <c>Authorization</c> header, or letting the
    /// caller attach it — and then the key has two paths to the wire, which is exactly what
    /// <see cref="ApiKey"/>'s redacted <see cref="object.ToString"/> and the single-header rule
    /// exist to prevent.
    /// </para>
    /// </remarks>
    public HttpMessageHandler? Handler { get; init; }

    /// <summary>
    /// Whether <see cref="DisposeAsync"/> disposes <see cref="Handler"/>. Defaults to
    /// <see langword="true"/>, as <see cref="HttpClient"/>'s own parameter does.
    /// </summary>
    /// <remarks>
    /// Set it to <see langword="false"/> when the handler's lifetime belongs to somebody else —
    /// which is the <c>IHttpMessageHandlerFactory</c> case, where the factory pools handlers
    /// across clients and rotates them on its own schedule. Disposing one out from under it would
    /// break every other client sharing it. Ignored when <see cref="Handler"/> is
    /// <see langword="null"/>: a handler this client built is a handler this client disposes.
    /// </remarks>
    public bool DisposesHandler { get; init; } = true;

    /// <summary>
    /// Where to send this client's log messages, or <see langword="null"/> for none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is how the API's <c>X-Warning</c> header surfaces, and it is the only route it has:
    /// the alternative — a warnings property on every response — means every one of the API's
    /// twenty endpoints (ROADMAP.md §5 lists them) returns a wrapper type instead of its payload,
    /// and every caller unwrapping, to carry a header that is almost always absent. That was
    /// rejected on cost, not on taste. See <c>Internal/HistoricalLog.cs</c> for the messages and
    /// their event ids.
    /// </para>
    /// <para>
    /// Left <see langword="null"/>, this resolves to <see cref="NullLogger.Instance"/> — no
    /// logging configured means no logging done, and nothing is formatted or allocated for a
    /// caller who never asked.
    /// </para>
    /// </remarks>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>The <c>metadata.*</c> endpoints — discovery, and what a request would cost.</summary>
    /// <remarks>
    /// The first of the four endpoint-group facades this client exposes; #38–#39 add the rest.
    /// Built once and cached, because this client is documented thread-safe for concurrent
    /// requests and a bare null-coalescing assignment would let two threads each build one.
    /// </remarks>
    public MetadataClient Metadata => _metadata.Value;

    /// <summary>The <c>symbology.*</c> endpoints — what a symbol's instrument id was, and when.</summary>
    /// <remarks>
    /// The second facade (#37), cached the same way and for the same reason. One endpoint, and it
    /// costs nothing to call.
    /// </remarks>
    public SymbologyClient Symbology => _symbology.Value;

    /// <summary>The <c>timeseries.*</c> endpoints — the market data itself.</summary>
    /// <remarks>
    /// The third facade (#38), cached the same way and for the same reason. <b>The only one whose
    /// endpoints cost money</b>: everything on <see cref="Metadata"/> and <see cref="Symbology"/> is
    /// discovery or a billing enquiry. Price a download with <see cref="MetadataClient.GetCostAsync"/>
    /// before making it.
    /// </remarks>
    public TimeseriesClient Timeseries => _timeseries.Value;

    /// <summary>The <c>batch.*</c> endpoints — jobs that produce files rather than a stream.</summary>
    /// <remarks>
    /// The fourth and last facade (#39), cached the same way and for the same reason. <b>One of its
    /// eight methods costs money and the rest are free:</b>
    /// <see cref="BatchClient.SubmitJobAsync"/> bills for the whole range at once, and listing,
    /// inspecting and <em>downloading</em> a job cost nothing — a job's files stay fetchable until
    /// they expire, so a download can be retried or resumed without a second charge. Price a job
    /// with <see cref="MetadataClient.GetCostAsync"/> before submitting it;
    /// <see cref="SubmitJobParams.ToQuery"/> narrows the parameters for exactly that.
    /// </remarks>
    public BatchClient Batch => _batch.Value;

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
    /// <para>
    /// <b><paramref name="slug"/> is interpolated, not escaped, and that is the caller's
    /// constraint to honour.</b> Upstream's <c>base_url.join(&amp;format!("v{API_VERSION}/{slug}"))</c>
    /// does not escape either, so this is faithful — but faithful is not the same as safe, and a
    /// slug is a path here rather than a value. A <c>?</c> in one starts a query string and a
    /// <c>#</c> starts a fragment, either of which silently truncates the path instead of
    /// producing a rejected request. Endpoint slugs are literals in this library's own source and
    /// cannot contain one; a batch file's path
    /// (<see href="https://github.com/jerbersoft/databentodotnet/issues/39">#39</see>) is
    /// server-supplied and is the one place a caller passes something it did not write, so
    /// percent-encode there rather than widening this.
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
    /// (<c>client.rs:144-154</c>) together with the <c>check_warnings</c> then
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
    /// <b>The <see cref="HttpRequestMessage"/> is disposed when this method returns, while the
    /// response body may still be arriving.</b> That is safe — the request has been written in
    /// full by the time <see cref="HttpClient"/> hands back headers, and nothing about reading
    /// the response consults it — but it has one visible consequence worth knowing before
    /// investigating it: <c>response.RequestMessage.Content</c> is a disposed
    /// <see cref="HttpContent"/> on a response held across a long read, so a caller retrying a
    /// download must rebuild the request rather than resend that one.
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

        return await SendCoreAsync(http, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one <c>GET</c> to an arbitrary path on the configured host, carrying whatever request
    /// headers are given, and returns the response with its headers read and its body still on the
    /// socket.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of upstream's <c>get_with_path</c> (<c>client.rs:128-137</c>), which exists for exactly
    /// one caller in either library: a batch file's download URL is <em>given</em> by the API
    /// rather than composed from a slug, so it cannot go through <see cref="SendAsync"/>.
    /// </para>
    /// <para>
    /// <b>Only the path is used, and the host the API named is discarded — deliberately, and
    /// upstream does the same.</b> <c>batch.list_files</c> returns URLs on
    /// <c>api.databento.com</c> while the API itself is reached at <c>hist.databento.com</c>;
    /// upstream's <c>base_url.join(path)</c> keeps the configured scheme and authority and replaces
    /// only the path, and #39 measured both hosts serving byte-identical responses for the same
    /// path. Two things follow, and both are the reason to keep it rather than to "fix" it.
    /// </para>
    /// <para>
    /// First, <b>the API key never reaches a host the caller did not configure.</b> The credential
    /// travels on this request as it does on every other one, so following a server-supplied
    /// absolute URL would be handing it to whatever host that URL named. Second, a test harness
    /// pointed at by <see cref="BaseUrl"/> keeps working: the download goes to the same loopback
    /// server as everything else, which is what lets this library's resumable-download tests run
    /// against <c>MockHistoricalGateway</c> at all.
    /// </para>
    /// <para>
    /// <b><paramref name="headers"/> is what <see cref="SendAsync"/> has no equivalent of</b>, and
    /// it exists for <c>Range</c>. Values go through
    /// <see cref="HttpHeaders.TryAddWithoutValidation(string, string?)"/> — the non-validating
    /// overload — because a <c>Range</c> is a request header whose validated form
    /// <see cref="HttpRequestMessage.Headers"/> models as a typed collection rather than as a
    /// string, and round-tripping through that type to send back the value already in hand would
    /// buy nothing.
    /// </para>
    /// </remarks>
    /// <param name="path">
    /// The path to fetch, resolved against the configured base URL. An absolute path — one
    /// beginning with <c>/</c>, which is what <see cref="Uri.AbsolutePath"/> gives — replaces the
    /// base URL's path entirely, which is what makes this a faithful port of <c>Url::join</c>.
    /// </param>
    /// <param name="headers">Request headers to add, or <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The response, headers read and body not yet buffered. The caller disposes it.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
    /// <exception cref="DatabentoApiException">The API answered with a non-success status.</exception>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    public async Task<HttpResponseMessage> GetPathAsync(
        string path,
        IEnumerable<KeyValuePair<string, string>>? headers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var http = Http;

        // BaseAddress is set by CreateHttpClient and never cleared, so it is not null here.
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(http.BaseAddress!, path));

        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return await SendCoreAsync(http, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a built request, logs whatever warnings the response carried, and throws if the API
    /// rejected it.
    /// </summary>
    /// <remarks>
    /// The half of <see cref="SendAsync"/> that <see cref="GetPathAsync"/> shares with it: upstream's
    /// <c>check_warnings</c> then <c>check_http_error</c> pair (<c>client.rs:205-206</c>), which
    /// every response handler in the crate opens with. Factored out rather than duplicated so a
    /// download and an endpoint call cannot drift on what counts as a failure.
    /// </remarks>
    private async Task<HttpResponseMessage> SendCoreAsync(
        HttpClient http,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
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
    /// <para>
    /// <b>A decode failure here is thrown and not logged, deliberately.</b> Upstream logs one
    /// (<c>deserialize_json</c>, <c>client.rs:231-236</c>) and this port does not; the rule that
    /// decides which of upstream's <c>tracing</c> sites are ported is on
    /// <c>Internal/HistoricalLog.cs</c>'s type remarks, and this is the case it rules out. The
    /// short version: the <see cref="JsonException"/> reaches the caller carrying
    /// <see cref="JsonException.Path"/>, <see cref="JsonException.LineNumber"/> and
    /// <see cref="JsonException.BytePositionInLine"/>, so a log line would duplicate what they
    /// already hold.
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
    /// automatic decompression — there is no <c>Content-Encoding</c> for that to act on. A line
    /// that is empty <em>or entirely whitespace</em> is skipped, which is wider than the trailing
    /// newline a line-oriented writer leaves behind and is deliberately so: a lone <c>\r</c>
    /// surviving a CRLF split, or a line of spaces, is no more a JSON document than an empty one
    /// and would otherwise fail the whole read.
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
    /// <para>
    /// <b>This is the buffering half of a pair.</b>
    /// <see cref="ReadZstdJsonLinesStreamAsync"/> is the streaming half: it yields each row as it
    /// decompresses instead of collecting them, and it is the one to reach for when the response
    /// is larger than the working set or the caller wants the first row before the last one has
    /// arrived. This one is what a caller who wants the whole list — to sort it, to count it, to
    /// index into it — should keep using.
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
    /// Reads <paramref name="response"/>'s body as a zstd frame containing one JSON document per
    /// line, yielding each row as it decompresses rather than collecting them all first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The streaming half of a pair.</b> <see cref="ReadZstdJsonLinesAsync"/> is the buffering
    /// half: it returns an <see cref="IReadOnlyList{T}"/> once the whole body has been read, and
    /// it stays the non-streaming path for a caller who wants the complete list — to sort it, to
    /// count it, to index into it. This one holds one row at a time, so a response larger than the
    /// working set costs the same as a small one and the first row is available before the last has
    /// left the server.
    /// </para>
    /// <para>
    /// <b>Rows come out in the order they arrived, and this method claims nothing more than
    /// that.</b> Upstream's <c>handle_zstd_jsonl_response</c> (<c>client.rs:212-229</c>) returns a
    /// <c>Vec&lt;R&gt;</c> precisely so that its callers can sort it, and all four of them do:
    /// <c>reference/security.rs:50-53</c> by <c>index</c> and <c>:77</c> by <c>ts_effective</c>,
    /// <c>corporate.rs:59-63</c> by <c>index</c>, <c>adjustment.rs:51</c> by <c>ex_date</c>. A
    /// stream cannot be sorted — sorting is what buffering <em>is</em> — so this does not sort, and
    /// the documented order is the server's own. Whether that differs from upstream's order
    /// observably depends on whether the server already returns rows sorted, which is a question
    /// about the live API that no mock can answer (a double returns the lines it was handed, so it
    /// agrees with whatever we assumed) and which
    /// <see href="https://github.com/jerbersoft/databentodotnet/issues/57">#57</see> owns. There is
    /// deliberately no sorting overload here: each reference endpoint decides for itself whether it
    /// sorts, over the buffering reader.
    /// </para>
    /// <para>
    /// <b>Blank-line tolerance and <c>null</c>-literal rejection are the buffering reader's, not a
    /// second reading of them.</b> A line that is empty or entirely whitespace is skipped; a line
    /// that is the JSON literal <c>null</c> throws <see cref="JsonException"/> with the same
    /// message. A difference between the two paths would be a bug in one of them, so the tests pin
    /// them to each other rather than asserting each in isolation.
    /// </para>
    /// <para>
    /// <b>The argument checks run at the call, not at the first <c>MoveNextAsync</c>, and that
    /// costs a split.</b> A C# iterator method runs no part of its body until it is enumerated, so
    /// an <c>await foreach</c>-less caller who passed <see langword="null"/> would get no exception
    /// at all — the bug would be silent rather than late. Everything above returns
    /// <see cref="IAsyncEnumerable{T}"/> from an ordinary method that validates and then hands back
    /// a private iterator, which is what <see cref="JsonSerializer"/>'s own
    /// <c>DeserializeAsyncEnumerable</c> does and what makes this behave like
    /// <see cref="ReadZstdJsonLinesAsync"/> at the call site. The consequence is that
    /// <c>[EnumeratorCancellation]</c> sits on the private iterator instead; a caller's
    /// <c>WithCancellation</c> still reaches it, because this method returns that iterator's
    /// enumerable unchanged.
    /// </para>
    /// <para>
    /// <b>Disposing the enumerator disposes the decompression stream and the body stream</b> —
    /// which is what an <c>await foreach</c> does on its way out, an early <c>break</c> included.
    /// <paramref name="response"/> itself is the caller's to dispose, as it is on the buffering
    /// reader; <see cref="SendZstdJsonLinesStreamAsync"/> is the composed form that owns it.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The type each line deserializes into.</typeparam>
    /// <param name="response">The response to read.</param>
    /// <param name="typeInfo">The source-generated metadata for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>One element per non-blank line, in the order they arrived.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">A line is not valid JSON, or is the literal <c>null</c>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static IAsyncEnumerable<T> ReadZstdJsonLinesStreamAsync<T>(
        HttpResponseMessage response,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(typeInfo);

        return ReadZstdJsonLinesStreamCoreAsync(response, typeInfo, cancellationToken);
    }

    /// <summary>
    /// The iterator behind <see cref="ReadZstdJsonLinesStreamAsync"/>, split off it so the argument
    /// checks run eagerly. See that method's remarks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="StreamReader.ReadLineAsync(CancellationToken)"/> over the decompressed frame,
    /// which is what the buffering reader does and what keeps the two paths' line handling one
    /// reading rather than two. The per-row cost is therefore a <see langword="string"/> and the
    /// object it deserializes into — a constant, not a share of the response, which is the property
    /// <c>ZstdJsonLinesAllocationTests</c> measures. It is deliberately not zero: every row is a
    /// JSON object and a class, so an allocation per row is correct here in a way it never is on
    /// the DBN record path.
    /// </para>
    /// <para>
    /// <b>Not <see cref="JsonSerializer"/>'s <c>DeserializeAsyncEnumerable</c>.</b> That API reads
    /// a JSON <em>array</em>; this body is a sequence of separate JSON documents separated by
    /// newlines, which is not one. And not <c>System.IO.Pipelines</c>, for the reason PORTING.md §3
    /// gives — it would add a second buffering layer over <see cref="StreamReader"/>'s own and buy
    /// nothing, since nothing here reinterprets bytes in place.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The type each line deserializes into.</typeparam>
    /// <param name="response">The response to read.</param>
    /// <param name="typeInfo">The source-generated metadata for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>One element per non-blank line, in the order they arrived.</returns>
    private static async IAsyncEnumerable<T> ReadZstdJsonLinesStreamCoreAsync<T>(
        HttpResponseMessage response,
        JsonTypeInfo<T> typeInfo,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var frame = ZstdDecompressor.Decompress(stream, leaveOpen: true);
            await using (frame.ConfigureAwait(false))
            {
                using var reader = new StreamReader(
                    frame, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var value = JsonSerializer.Deserialize(line, typeInfo);
                    if (value is null)
                    {
                        // Word for word the buffering reader's message. The tests compare the two,
                        // so a change to either one that is not made to both fails the build's
                        // tests rather than drifting.
                        throw new JsonException(
                            $"A line of the response was the JSON literal 'null', which is not a {typeof(T).Name}.");
                    }

                    yield return value;
                }
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
    /// <para>
    /// The shape the reference-data endpoints want; see <see cref="ReadZstdJsonLinesAsync"/> for
    /// why nothing in M3 calls it.
    /// </para>
    /// <para>
    /// The buffering half of a pair — <see cref="SendZstdJsonLinesStreamAsync"/> is the streaming
    /// half, which yields rows as they decompress instead of returning a list.
    /// </para>
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

    /// <summary>
    /// Sends a request and streams its body's rows as they decompress — <see cref="SendAsync"/> and
    /// <see cref="ReadZstdJsonLinesStreamAsync"/> composed, with the response disposed when the
    /// enumeration ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The streaming half of a pair; <see cref="SendZstdJsonLinesAsync"/> is the buffering half.
    /// Rows come out <b>in the order they arrived</b> and this method sorts nothing — see
    /// <see cref="ReadZstdJsonLinesStreamAsync"/>, which is where that decision is argued in full.
    /// </para>
    /// <para>
    /// <b>Nothing is sent until the enumeration starts, and the response lives exactly as long as
    /// the enumerator.</b> The request is issued from inside the iterator, so the
    /// <see langword="using"/> that owns the <see cref="HttpResponseMessage"/> is unwound by
    /// <c>IAsyncEnumerator.DisposeAsync</c> — which is what <c>await foreach</c> does on its way
    /// out, whether the loop ran to the end, hit an exception, or <c>break</c>'d after one row.
    /// Hoisting the send out of the iterator, into the validating method above it, would compile
    /// and pass a happy-path test and would leak the socket for every caller who stopped early, so
    /// the tests prove the close from the gateway's side rather than from ours.
    /// </para>
    /// <para>
    /// Split into a validating method and a private iterator for the reason
    /// <see cref="ReadZstdJsonLinesStreamAsync"/> gives: a bad argument should fault at the call
    /// rather than at the first <c>MoveNextAsync</c> — or, for a caller who never enumerates, not
    /// at all. The checks duplicate <see cref="SendAsync"/>'s own deliberately, because inside an
    /// iterator its checks no longer run when the caller makes the call.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The type each line deserializes into.</typeparam>
    /// <param name="method">The HTTP method.</param>
    /// <param name="slug">The API slug, without the version prefix.</param>
    /// <param name="parameters">The request parameters, or <see langword="null"/> for none.</param>
    /// <param name="typeInfo">The source-generated metadata for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">Cancels the request and the enumeration.</param>
    /// <returns>One element per non-blank line, in the order they arrived.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="slug"/> is null or empty.</exception>
    /// <exception cref="DatabentoApiException">The API answered with a non-success status.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public IAsyncEnumerable<T> SendZstdJsonLinesStreamAsync<T>(
        HttpMethod method,
        string slug,
        IEnumerable<KeyValuePair<string, string>>? parameters,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrEmpty(slug);
        ArgumentNullException.ThrowIfNull(typeInfo);

        return SendZstdJsonLinesStreamCoreAsync(method, slug, parameters, typeInfo, cancellationToken);
    }

    /// <summary>
    /// The iterator behind <see cref="SendZstdJsonLinesStreamAsync"/>. See that method's remarks
    /// for why the <see langword="using"/> below has to be here and not one level up.
    /// </summary>
    /// <typeparam name="T">The type each line deserializes into.</typeparam>
    /// <param name="method">The HTTP method.</param>
    /// <param name="slug">The API slug, without the version prefix.</param>
    /// <param name="parameters">The request parameters, or <see langword="null"/> for none.</param>
    /// <param name="typeInfo">The source-generated metadata for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">Cancels the request and the enumeration.</param>
    /// <returns>One element per non-blank line, in the order they arrived.</returns>
    private async IAsyncEnumerable<T> SendZstdJsonLinesStreamCoreAsync<T>(
        HttpMethod method,
        string slug,
        IEnumerable<KeyValuePair<string, string>>? parameters,
        JsonTypeInfo<T> typeInfo,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var response = await SendAsync(method, slug, parameters, accept: null, cancellationToken).ConfigureAwait(false);

        // The private iterator rather than the validating method in front of it: the arguments have
        // already been checked, and running ThrowIfNull twice over the same reference is noise.
        var rows = ReadZstdJsonLinesStreamCoreAsync(response, typeInfo, cancellationToken);
        await foreach (var row in rows.ConfigureAwait(false))
        {
            yield return row;
        }
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

    /// <summary>
    /// Where this client's own log messages go — <see cref="NullLogger.Instance"/> when no
    /// <see cref="LoggerFactory"/> was configured.
    /// </summary>
    /// <remarks>
    /// <see langword="internal"/>, and reached only by the endpoint facades in this assembly.
    /// <see cref="BatchClient"/> is the one that needs it: a download logs three things the caller
    /// cannot otherwise see (see <c>Internal/HistoricalLog.cs</c> for the rule that admits them),
    /// and it does so from a method that returns file paths rather than a response. No
    /// <c>InternalsVisibleTo</c> is involved — this is one assembly.
    /// </remarks>
    internal ILogger Logger => _logger.Value;

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
        // The request id comes off the response before the body is read, matching upstream's
        // order: request_id at client.rs:163-166, status_code at :167, then
        // response.text().await at :168.
        //
        // **Upstream has to read them first. We do not, and the difference is worth being exact
        // about.** reqwest's `text(self)` takes the Response by value and consumes it, so in Rust
        // the ordering is genuinely load-bearing — read the body first and there is no longer a
        // response to take the status or the header from. In .NET nothing is consumed:
        // HttpResponseMessage.StatusCode and .Headers stay readable after a failed content read,
        // including from inside the catch below.
        //
        // So the ordering here is faithfulness to upstream, and **the catch is what actually
        // preserves the status and the request id**. The two are not substitutes, and reading
        // this comment as though they were is the one mistake it exists to prevent: keeping the
        // order while dropping the guard reintroduces precisely the defect — an error that
        // reports neither the status nor the id support asks for first.
        var requestId = response.Headers.TryGetValues(RequestIdHeader, out var values)
            ? values.FirstOrDefault()
            : null;

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        {
            // The connection failed partway through the error body — the API said 500 and then
            // the transfer went. Upstream's `unwrap_or_default()` reports the status with an
            // empty message; this reports it with the read failure's own message in the body's
            // place, which is strictly more than upstream keeps and is the only record of why
            // there is no body to quote.
            //
            // **The type filter is the load-bearing part of this catch.** A bare `catch` would
            // also swallow OperationCanceledException, turning a caller's cancelled request —
            // or a linked timeout budget elapsing — into a DatabentoApiException claiming the
            // API rejected something. It did not; the caller called it off.
            //
            // Both types are needed, and which one arrives is not obvious: a connection reset
            // mid-body surfaces here as HttpRequestException("Error while copying content to a
            // stream") *wrapping* the IOException, so a filter naming only IOException would
            // miss the very case this exists for. Measured, not assumed.
            //
            // Nothing is logged here, per the rule on HistoricalLog: the exception's message
            // reaches the caller inside the exception they already receive, so a log line would
            // duplicate what they hold rather than record something they cannot otherwise see.
            return new DatabentoApiException(
                response.StatusCode,
                requestId,
                errorCase: null,
                $"The error response body could not be read: {exception.Message}",
                docsUrl: null,
                payload: null);
        }

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
        // No handler of our own unless the caller supplied one. HttpClient's automatic
        // decompression is off by default and would be irrelevant if it were not: the zstd frame
        // the API returns is in the body and nothing announces it in Content-Encoding, so
        // ReadZstdJsonLinesAsync unwraps it itself.
        //
        // DisposeAsync needs no branch for this. HttpClient's own disposeHandler parameter already
        // decides whether Dispose reaches the handler, so the one call at the end of DisposeAsync
        // does the right thing in both cases without knowing either property exists.
        var http = Handler is null
            ? new HttpClient()
            : new HttpClient(Handler, DisposesHandler);

        http.BaseAddress = EffectiveBaseUrl();

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
    /// <para>
    /// The normalisation belongs here rather than on <see cref="HistoricalGateway"/>: a gateway
    /// URL is a bare authority, which <see cref="Uri"/> already normalises to a root path, and it
    /// is a consumer-supplied <see cref="BaseUrl"/> carrying a path that would otherwise lose its
    /// last segment when <c>v0/{slug}</c> is resolved against it.
    /// </para>
    /// <para>
    /// <b>The test is on <see cref="Uri.AbsolutePath"/>, not on the whole URI.</b> A base URL may
    /// carry a query — <c>http://proxy/api?token=x</c> — and appending the slash to the full
    /// string would put it after the query, producing <c>…?token=x/</c>: a base whose path is
    /// still <c>/api</c> and whose token now has a slash glued to it. Rebuilding through
    /// <see cref="UriBuilder"/> puts the slash where it belongs and leaves scheme, port, query
    /// and fragment alone.
    /// </para>
    /// <para>
    /// A query on the base URL is <em>inert</em> regardless, and this is the place to say so
    /// rather than let someone discover it: resolving a relative reference that has a path
    /// replaces the base's query outright (RFC 3986 §5.2.2, "Transform References" — the
    /// <c>T.query = R.query</c> arm taken when the reference's path is non-empty), so
    /// <c>?token=x</c> reaches no request
    /// and is not a way to attach a credential. The one credential this client sends is the
    /// <c>Authorization</c> header.
    /// </para>
    /// </remarks>
    private Uri EffectiveBaseUrl()
    {
        var url = BaseUrl ?? Gateway.ToUri();

        if (url.AbsolutePath.EndsWith('/'))
        {
            return url;
        }

        return new UriBuilder(url) { Path = url.AbsolutePath + "/" }.Uri;
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
