using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// A stand-in for the Databento historical API: it serves <c>v0/{slug}</c> over real HTTP on a
/// loopback port, asserts that the client authenticates the way the API requires, and answers with
/// whatever <see cref="MockHistoricalResponse"/> the test registered — including the several ways
/// the API misbehaves.
/// </summary>
/// <remarks>
/// <para>
/// It goes in before any historical client code exists, for the reason
/// <c>MockLiveGateway</c> (<see href="https://github.com/jerbersoft/databentodotnet/issues/18">#18</see>)
/// went in before the live client: nothing below it is testable without one, and a harness grown
/// ad hoc inside whichever issue needs it next is a harness shaped by one caller.
/// </para>
/// <para>
/// <b>It is written from the API's documented HTTP behaviour, not from this library.</b> The test
/// project references no <c>src/</c> project at all. The facts it encodes are the published ones:
/// paths are <c>v{0}/{slug}</c>; authentication is HTTP Basic with the API key as the username and
/// an <em>empty</em> password; <c>request-id</c> identifies a response to support; <c>X-Warning</c>
/// carries a JSON array of warnings. That is deliberate — a double sharing types with the client it
/// exists to check would let a misreading of the API sit in both, and the two would then agree with
/// each other. It is also why <see cref="ExpectedUserAgentPrefix"/> is a literal string rather than
/// a call into the library's user-agent type.
/// </para>
/// <para>
/// <b>Kestrel, not an <c>HttpMessageHandler</c> stub.</b> A fake handler never opens a socket, so
/// it cannot exercise chunked transfer and back-pressure, a <c>Range: bytes=N-</c> answered
/// <c>206 Partial Content</c>, a connection dropped mid-body, or <c>HttpClient</c> itself — and
/// <c>HttpClient</c> is the component under test in half of M3. See the csproj for the rejected
/// alternatives.
/// </para>
/// <para>
/// <b>Departures from upstream's harness.</b> Upstream (<c>databento-rs/src/historical.rs</c>,
/// <c>test_infra</c>) is a <c>wiremock</c> <c>MockServer</c> plus a per-test stack of
/// <c>Mock::given(…)</c> matchers, and each test restates the credential matcher it wants. This is
/// one class with route registration, matching <c>MockLiveGateway</c> rather than matching
/// <c>wiremock</c>, and the credential check is unconditional: it is the harness's own invariant,
/// not something a test opts into, so no test downstream of this one can go green against a client
/// that authenticated wrongly.
/// </para>
/// <para>
/// <b>The <c>Authorization</c> header never reaches a recorded request.</b>
/// <see cref="RecordedRequest.Headers"/> omits it outright, so a key sent the correct way — as the
/// Basic username — is never recorded. A key a broken client puts in the query string or the form
/// instead is a different story: <see cref="RecordedRequest.Query"/>, <see cref="RecordedRequest.RawQuery"/>,
/// <see cref="RecordedRequest.Form"/> and <see cref="RecordedRequest.Body"/> all record it verbatim,
/// key-looking ones included, and it stays readable through <see cref="Requests"/> even though the
/// gateway goes on to refuse the request. The credential guard itself is held to a stronger and
/// entirely structural rule:
/// <b>no message it produces interpolates anything the request carried</b>, the only two values
/// reaching one being <see cref="ExpectedUserAgentPrefix"/> and a name out of
/// <see cref="KeyParameterNames"/>, both of which this harness owns. A message with no request
/// data in it cannot leak a key, whatever a broken client sends, so the property holds without
/// anyone having to remember it.
/// </para>
/// <para>
/// <b>Staying readable rather than redacting is a deliberate choice.</b> Three reasons. First,
/// <see cref="RecordedRequest"/> exists to be a faithful record of what arrived — a test that wants
/// to assert the key is <em>not</em> in the query or form has to be able to look, and against a
/// redacted record it could only assert the absence of a redaction marker, a weaker claim that
/// passes for the wrong reason if the marker logic itself breaks. Second, the guard refuses the
/// request regardless, so a readable record is only ever a record of a request that already failed;
/// redaction would be a second mechanism guarding a state the first mechanism already makes
/// unreachable, and the second one is the one nothing tests. Third, the key here is a fixed test
/// constant in a test-only assembly, and it is the guard's own no-interpolation rule — not
/// redaction — that actually keeps it out of anything a human reads.
/// </para>
/// <para>
/// <b>One message is outside that rule, deliberately.</b> An unregistered route is reported as
/// <c>No route is registered for 'GET /v0/…'</c>, which echoes the request line — method plus the
/// path the client asked for. That is the whole value of the message: a slug misspelled at
/// registration is diagnosable only if the message says which one arrived. It is safe for a
/// different reason than the guard's, and the difference is worth naming rather than blurring: the
/// key travels in a header, so a path can only carry one if a client put it there, and the guard
/// above would not have caught that either. Should the rule ever need to cover the path too, the
/// fix is to widen the guard, not to weaken this message.
/// </para>
/// <para>
/// <b>A refused request is answered on the wire and re-raised on the test's thread.</b> Kestrel
/// runs the handler, so an exception thrown there would reach the client as a <c>500</c> and the
/// test as nothing. Instead the gateway answers <c>401</c> for a credential the API would not
/// accept and <c>501</c> for a route nobody registered, records why, and
/// <see cref="ThrowIfRejected"/> raises it where the test can see it. <c>501</c> rather than
/// <c>404</c> for the unregistered route on purpose: the API returns <c>404</c>, so a test with a
/// typo in its slug would otherwise pass an assertion about error handling for entirely the wrong
/// reason.
/// </para>
/// </remarks>
public sealed class MockHistoricalGateway : IAsyncDisposable
{
    /// <summary>
    /// The 32-character test API key, shared verbatim with upstream's <c>test_infra::API_KEY</c> so
    /// a failure here and a failure there can be compared directly.
    /// </summary>
    public const string TestApiKey = "test-API________________________";

    /// <summary>The historical API version every path is prefixed with.</summary>
    public const int ApiVersion = 0;

    /// <summary>
    /// The literal prefix a request's <c>User-Agent</c> must start with.
    /// </summary>
    /// <remarks>
    /// A literal rather than a call into the library's user-agent type, because this project
    /// references no <c>src/</c> project — see the class remarks. The real client's user agent is
    /// <c>DatabentoDotNet/{version} …</c>, so the prefix is the part of it that is a promise rather
    /// than a build detail.
    /// </remarks>
    public const string UserAgentPrefix = "DatabentoDotNet/";

    /// <summary>The response header carrying a JSON array of server warnings.</summary>
    public const string WarningHeader = "X-Warning";

    /// <summary>The response header identifying a response to Databento support.</summary>
    public const string RequestIdHeader = "request-id";

    /// <summary>The status the gateway answers a request it refuses to authenticate.</summary>
    public const int RefusedStatusCode = StatusCodes.Status401Unauthorized;

    /// <summary>The status the gateway answers a route no test registered.</summary>
    public const int UnroutedStatusCode = StatusCodes.Status501NotImplemented;

    /// <summary>
    /// The query parameter or form field names that mean "the API key travelled outside the
    /// <c>Authorization</c> header" whatever their value. A fixed list the harness owns, so naming
    /// the offender in a refusal message can never echo a request back.
    /// </summary>
    private static readonly string[] KeyParameterNames = ["key", "api_key", "apikey"];

    /// <summary>
    /// How much of a body goes out per write, with a flush between. Small on purpose: a body of a
    /// few hundred bytes then really crosses the wire in several chunks, which is what makes
    /// back-pressure and a mid-body drop observable. <c>MockLiveGateway</c> splits every record
    /// into two writes for the same reason.
    /// </summary>
    private const int ChunkSize = 64;

    private const string BasicScheme = "Basic ";
    private const string AuthorizationHeader = "Authorization";
    private const string UserAgentHeader = "User-Agent";

    private readonly Lock _gate = new();
    private readonly List<RecordedRequest> _requests = [];
    private readonly List<string> _rejections = [];
    private readonly Dictionary<string, MockHistoricalResponse> _routes = new(StringComparer.Ordinal);
    private readonly WebApplication _app;

    private Uri? _baseUrl;

    private MockHistoricalGateway(string apiKey, string userAgentPrefix)
    {
        ExpectedApiKey = apiKey;
        ExpectedUserAgentPrefix = userAgentPrefix;

        var builder = WebApplication.CreateSlimBuilder();

        // Silence, not tolerance. Kestrel's console provider would interleave its own lines with
        // xUnit's output on every request, and a connection reset — which this harness causes on
        // purpose — logs at error level. A harness whose normal operation prints stack traces
        // trains everyone reading the run to ignore them.
        builder.Logging.ClearProviders();

        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));

        _app = builder.Build();

        // One terminal handler rather than the routing middleware: routes here are registered
        // after the server is already running, and dispatching from a dictionary keeps that
        // possible without rebuilding a pipeline.
        _app.Run(HandleAsync);
    }

    /// <summary>
    /// The base URL a client should be pointed at, with a trailing slash so
    /// <c>new Uri(BaseUrl, "v0/…")</c> resolves the way <c>Url::join</c> does.
    /// </summary>
    /// <remarks>
    /// The client reaches this harness through a base-URL override, the way upstream's tests do.
    /// That is also why nothing here needs TLS: the rule that certificate verification is never
    /// disabled to make a call work stays intact because no test ever makes a TLS call to a fake
    /// host.
    /// </remarks>
    public Uri BaseUrl => _baseUrl
        ?? throw new InvalidOperationException("The gateway is not running. Use StartAsync.");

    /// <summary>The API key a request's Basic credential must carry as its username.</summary>
    public string ExpectedApiKey { get; }

    /// <summary>The prefix a request's <c>User-Agent</c> must start with.</summary>
    public string ExpectedUserAgentPrefix { get; }

    /// <summary>
    /// How long a response registered with <c>MockHistoricalResponse.Dropped(…, dropWhen)</c> waits
    /// for its signal before resetting the connection anyway. Defaults to ten seconds.
    /// </summary>
    /// <remarks>
    /// The only wait in this harness that Kestrel does not already bound, and therefore the only
    /// place a test could hang the run. <c>MockLiveGateway.Timeout</c> exists for the same reason:
    /// a double that blocks forever is the worst failure a suite can have, because the run stops
    /// and the reason is invisible.
    /// </remarks>
    public Duration Timeout { get; set; } = Duration.FromSeconds(10);

    /// <summary>Every request the gateway saw, in arrival order, refused ones included.</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>
    /// Why the gateway refused each request it refused, in arrival order. Empty when every request
    /// authenticated correctly and matched a registered route.
    /// </summary>
    public IReadOnlyList<string> Rejections
    {
        get
        {
            lock (_gate)
            {
                return [.. _rejections];
            }
        }
    }

    /// <summary>
    /// Starts a gateway on an ephemeral loopback port, expecting <see cref="TestApiKey"/> and
    /// <see cref="UserAgentPrefix"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The running gateway.</returns>
    public static Task<MockHistoricalGateway> StartAsync(CancellationToken cancellationToken = default) =>
        StartAsync(TestApiKey, UserAgentPrefix, cancellationToken);

    /// <summary>
    /// Starts a gateway on an ephemeral loopback port, expecting a specific credential and user
    /// agent.
    /// </summary>
    /// <param name="expectedApiKey">The API key a request's Basic username must carry.</param>
    /// <param name="expectedUserAgentPrefix">The prefix a request's <c>User-Agent</c> must start with.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The running gateway.</returns>
    public static Task<MockHistoricalGateway> StartAsync(
        string expectedApiKey,
        string expectedUserAgentPrefix,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedApiKey);
        ArgumentException.ThrowIfNullOrEmpty(expectedUserAgentPrefix);

        return new MockHistoricalGateway(expectedApiKey, expectedUserAgentPrefix)
            .StartCoreAsync(cancellationToken);
    }

    private async Task<MockHistoricalGateway> StartCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _app.StartAsync(cancellationToken).ConfigureAwait(false);

            var address = _app.Urls.FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "Kestrel started without reporting a bound address.");

            _baseUrl = new Uri(address.EndsWith('/') ? address : address + "/", UriKind.Absolute);
            return this;
        }
        catch
        {
            // A half-started host still holds a port and a thread pool. Nothing else will dispose
            // it, because the caller never received a reference to dispose.
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The path a slug is served at: <c>/v0/{slug}</c>.</summary>
    /// <param name="slug">The API slug — <c>metadata.list_datasets</c>, <c>timeseries.get_range</c>.</param>
    /// <returns>The absolute path, with its leading slash.</returns>
    public static string PathFor(string slug)
    {
        ArgumentException.ThrowIfNullOrEmpty(slug);

        return $"/v{ApiVersion.ToString(CultureInfo.InvariantCulture)}/{slug}";
    }

    /// <summary>Registers the answer to a <c>GET</c> of <paramref name="slug"/>.</summary>
    /// <param name="slug">
    /// The API slug, without the version prefix. Slashes are allowed, so a batch file's path —
    /// <c>batch/download/{user}/{job}/{file}</c> — registers the same way an endpoint does.
    /// </param>
    /// <param name="response">The answer.</param>
    /// <returns>This gateway, so registrations chain.</returns>
    public MockHistoricalGateway Get(string slug, MockHistoricalResponse response) =>
        Map(HttpMethods.Get, slug, response);

    /// <summary>Registers the answer to a <c>POST</c> of <paramref name="slug"/>.</summary>
    /// <param name="slug">The API slug, without the version prefix.</param>
    /// <param name="response">The answer.</param>
    /// <returns>This gateway, so registrations chain.</returns>
    public MockHistoricalGateway Post(string slug, MockHistoricalResponse response) =>
        Map(HttpMethods.Post, slug, response);

    /// <summary>
    /// Throws if the gateway refused any request, naming every refusal.
    /// </summary>
    /// <remarks>
    /// The counterpart of <c>MockLiveGateway</c> throwing mid-exchange. Call it after an exchange a
    /// test expects to have gone well: a client that authenticated wrongly, or asked for a slug the
    /// test spelled differently when registering it, fails here with a message that says so rather
    /// than failing several assertions later on an empty body.
    /// </remarks>
    /// <exception cref="MockHistoricalGatewayException">At least one request was refused.</exception>
    public void ThrowIfRejected()
    {
        string[] rejections;
        lock (_gate)
        {
            if (_rejections.Count == 0)
            {
                return;
            }

            rejections = [.. _rejections];
        }

        throw new MockHistoricalGatewayException(
            rejections.Length == 1
                ? rejections[0]
                : $"The gateway refused {rejections.Length.ToString(CultureInfo.InvariantCulture)} requests:"
                  + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", rejections));
    }

    /// <summary>Stops the server and releases its port.</summary>
    public async ValueTask DisposeAsync()
    {
        // Not ThrowIfRejected: a refusal raised from disposal would replace whatever the test was
        // actually failing on, and a test that deliberately provokes one would have to defuse it.
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    private MockHistoricalGateway Map(string method, string slug, MockHistoricalResponse response)
    {
        ArgumentException.ThrowIfNullOrEmpty(slug);
        ArgumentNullException.ThrowIfNull(response);

        var key = $"{method} {PathFor(slug)}";
        lock (_gate)
        {
            if (!_routes.TryAdd(key, response))
            {
                throw new InvalidOperationException(
                    $"'{key}' is already registered. A route answers with one response; register a "
                    + "different slug, or start a second gateway.");
            }
        }

        return this;
    }

    private async Task HandleAsync(HttpContext context)
    {
        var request = await RecordAsync(context.Request).ConfigureAwait(false);

        if (Refuse(context.Request) is { } refusal)
        {
            // Refused requests are never dispatched to their route. If they were, deleting a check
            // above would leave every test that asserts a refusal passing on the route's own body.
            await RefuseAsync(context, RefusedStatusCode, refusal).ConfigureAwait(false);
            return;
        }

        MockHistoricalResponse? response;
        lock (_gate)
        {
            _routes.TryGetValue(request.RouteKey, out response);
        }

        if (response is null)
        {
            await RefuseAsync(
                context,
                UnroutedStatusCode,
                $"No route is registered for '{request.RouteKey}'.").ConfigureAwait(false);
            return;
        }

        await RespondAsync(context, response).ConfigureAwait(false);
    }

    private async Task<RecordedRequest> RecordAsync(HttpRequest request)
    {
        // Buffering makes the body seekable, so it can be captured whole and then handed to the
        // framework's form parser. Parsing x-www-form-urlencoded by hand here would be a second
        // implementation of a decoder the test double has no business owning.
        request.EnableBuffering();

        using var body = new MemoryStream();
        await request.Body.CopyToAsync(body).ConfigureAwait(false);
        request.Body.Position = 0;

        var form = new Dictionary<string, string>(StringComparer.Ordinal);
        if (request.HasFormContentType)
        {
            foreach (var field in await request.ReadFormAsync().ConfigureAwait(false))
            {
                form[field.Key] = string.Join(',', field.Value.ToArray());
            }

            request.Body.Position = 0;
        }

        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in request.Query)
        {
            query[parameter.Key] = string.Join(',', parameter.Value.ToArray());
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            // Authorization is the one header a test cannot see. The key is what it carries, the
            // guard below is the only thing that needs to read it, and a recorded copy would be a
            // second place it could escape from.
            if (!string.Equals(header.Key, AuthorizationHeader, StringComparison.OrdinalIgnoreCase))
            {
                headers[header.Key] = string.Join(',', header.Value.ToArray());
            }
        }

        var recorded = new RecordedRequest
        {
            Method = request.Method,
            Path = request.Path.Value ?? string.Empty,
            Query = query,
            RawQuery = request.QueryString.Value ?? string.Empty,
            Form = form,
            Headers = headers,
            Body = body.ToArray(),
        };

        lock (_gate)
        {
            _requests.Add(recorded);
        }

        return recorded;
    }

    /// <summary>
    /// Returns why this request is refused, or <see langword="null"/> if it is acceptable.
    /// </summary>
    /// <remarks>
    /// <b>No message below interpolates anything the request carried.</b> The only two values that
    /// reach one are <see cref="ExpectedUserAgentPrefix"/> and a name out of
    /// <see cref="KeyParameterNames"/>, both of which this harness owns. That is structural
    /// rather than careful: a message with no request data in it cannot leak the API key, and it
    /// holds for whatever a broken client sends rather than for the cases someone thought of.
    /// </remarks>
    private string? Refuse(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(AuthorizationHeader, out var authorization))
        {
            return "The request carried no Authorization header. The historical API takes HTTP "
                + "Basic with the API key as the username and an empty password.";
        }

        var credential = authorization.ToString();
        if (!credential.StartsWith(BasicScheme, StringComparison.Ordinal))
        {
            return "The Authorization header is not HTTP Basic. The historical API takes Basic "
                + "with the API key as the username and an empty password.";
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(credential[BasicScheme.Length..]));
        }
        catch (FormatException)
        {
            return "The Basic credential is not valid base64.";
        }

        var separator = decoded.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0)
        {
            return "The Basic credential carries no ':' separating the username from the password.";
        }

        if (!string.Equals(decoded[..separator], ExpectedApiKey, StringComparison.Ordinal))
        {
            return "The Basic username is not the expected API key.";
        }

        if (separator != decoded.Length - 1)
        {
            return "The Basic password is not empty. The historical API takes the API key as the "
                + "username and nothing at all as the password.";
        }

        if (!request.Headers.TryGetValue(UserAgentHeader, out var userAgent)
            || !userAgent.ToString().StartsWith(ExpectedUserAgentPrefix, StringComparison.Ordinal))
        {
            return $"The User-Agent does not start with '{ExpectedUserAgentPrefix}'.";
        }

        foreach (var parameter in request.Query)
        {
            foreach (var name in KeyParameterNames)
            {
                if (string.Equals(parameter.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return "The API key travels in the Authorization header and nowhere else; the "
                        + $"query string carries a '{name}' parameter.";
                }
            }

            foreach (var value in parameter.Value)
            {
                if (value is not null && value.Contains(ExpectedApiKey, StringComparison.Ordinal))
                {
                    return "The API key travels in the Authorization header and nowhere else; it "
                        + "appears as the value of a query parameter.";
                }
            }
        }

        // Safe to read request.Form synchronously here: HandleAsync calls RecordAsync before
        // Refuse, and RecordAsync calls EnableBuffering() and awaits ReadFormAsync() first, so the
        // parsed form is already cached on the request by the time this method runs. Reordering
        // HandleAsync to refuse before recording would not throw — request.Form's synchronous
        // getter falls back to ReadFormAsync().GetAwaiter().GetResult() and that succeeds. What it
        // actually breaks is quieter: Refuse would drain the body before EnableBuffering() made it
        // seekable, so RecordedRequest.Body comes back empty while the response still answers
        // 200 OK. Post_Form_RecordsEveryFieldOfTheBody's
        // Assert.Contains("stype_in=raw_symbol", ...) is what catches that — it fails against an
        // empty string. The HasFormContentType guard below is load-bearing on its own account too:
        // drop it and FormFeature.ReadForm() throws "Incorrect Content-Type" for a request with no
        // form, turning 18 of this file's 60 tests into a 500.
        if (request.HasFormContentType)
        {
            foreach (var field in request.Form)
            {
                foreach (var name in KeyParameterNames)
                {
                    if (string.Equals(field.Key, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return "The API key travels in the Authorization header and nowhere else; the "
                            + $"form carries a '{name}' parameter.";
                    }
                }

                foreach (var value in field.Value)
                {
                    if (value is not null && value.Contains(ExpectedApiKey, StringComparison.Ordinal))
                    {
                        return "The API key travels in the Authorization header and nowhere else; it "
                            + "appears as the value of a form parameter.";
                    }
                }
            }
        }

        return null;
    }

    private async Task RefuseAsync(HttpContext context, int statusCode, string reason)
    {
        lock (_gate)
        {
            _rejections.Add(reason);
        }

        var body = Encoding.UTF8.GetBytes(reason);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.ContentLength = body.Length;
        await context.Response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }

    private async Task RespondAsync(HttpContext context, MockHistoricalResponse response)
    {
        var body = response.Body;
        var firstByte = 0;

        var range = response.SupportsRange
            ? ParseOpenEndedRange(context.Request.Headers.Range, body.Length, out firstByte)
            : RangeRequest.None;

        if (range == RangeRequest.Satisfiable)
        {
            context.Response.StatusCode = StatusCodes.Status206PartialContent;
            context.Response.ContentType = response.ContentType;
            ApplyExtraHeaders(context, response);

            var tail = body[firstByte..];
            var last = (body.Length - 1).ToString(CultureInfo.InvariantCulture);
            var total = body.Length.ToString(CultureInfo.InvariantCulture);
            context.Response.Headers.ContentRange =
                $"bytes {firstByte.ToString(CultureInfo.InvariantCulture)}-{last}/{total}";

            // A range response states its length. The client asked for a specific tail and gets
            // exactly that many bytes; there is nothing here for chunking to be useful for.
            context.Response.ContentLength = tail.Length;
            await WriteBodyAsync(context, tail).ConfigureAwait(false);
            return;
        }

        if (range == RangeRequest.Unsatisfiable)
        {
            await RefuseRangeAsync(context, body.Length).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = response.StatusCode;
        context.Response.ContentType = response.ContentType;
        ApplyExtraHeaders(context, response);

        if (response.DropAfterBytes is { } dropAfter)
        {
            // No Content-Length, so the transfer is chunked and the connection goes before the
            // terminating chunk. The client reads that as a transfer that failed, which is the
            // point; a body that simply ended would be indistinguishable from success.
            await WriteBodyAsync(context, body[..dropAfter]).ConfigureAwait(false);
            await WaitForDropSignalAsync(context, response).ConfigureAwait(false);
            context.Abort();
            return;
        }

        if (!response.Chunked)
        {
            context.Response.ContentLength = body.Length;
        }

        await WriteBodyAsync(context, body).ConfigureAwait(false);
    }

    private async Task WaitForDropSignalAsync(HttpContext context, MockHistoricalResponse response)
    {
        if (response.DropWhen is not { } signal)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(checked((int)Timeout.TotalMilliseconds));

        try
        {
            await signal.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The client hung up, or the test never signalled. Either way the connection is about
            // to be reset, which is what the caller asked for; waiting longer only delays a
            // failure that is already inevitable.
        }
    }

    private static void ApplyExtraHeaders(HttpContext context, MockHistoricalResponse response)
    {
        foreach (var header in response.ExtraHeaders)
        {
            context.Response.Headers.Append(header.Key, header.Value);
        }
    }

    private static async Task WriteBodyAsync(HttpContext context, ReadOnlyMemory<byte> body)
    {
        for (var written = 0; written < body.Length; written += ChunkSize)
        {
            var chunk = body.Slice(written, Math.Min(ChunkSize, body.Length - written));
            await context.Response.Body.WriteAsync(chunk, context.RequestAborted).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
        }
    }

    /// <summary>What a request's <c>Range</c> header asks this response for.</summary>
    private enum RangeRequest
    {
        /// <summary>
        /// No <c>Range</c> header, or one in a form the API's own clients never send. The whole
        /// body goes out with a <c>200</c>.
        /// </summary>
        None,

        /// <summary><c>bytes=N-</c> naming a byte the body actually holds.</summary>
        Satisfiable,

        /// <summary><c>bytes=N-</c> with <c>N</c> at or past the end of the body.</summary>
        Unsatisfiable,
    }

    /// <summary>
    /// Answers an unsatisfiable range the way the spec does, and loudly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>bytes=N-</c> with <c>N</c> equal to the body's length is exactly the request a resumed
    /// download makes when the local file is already complete, and it is the case
    /// <see href="https://github.com/jerbersoft/databentodotnet/issues/39">#39</see> has to tell
    /// apart from "shorter, so resume". Serving the whole body again would let a client that
    /// miscomputed its offset append a second copy and still go green — the failure this harness
    /// exists to make impossible. <c>416</c> with <c>Content-Range: bytes */{total}</c> is what
    /// RFC 9110 §15.5.17 says and what a real byte-serving origin does.
    /// </para>
    /// <para>
    /// <b>Not recorded as a rejection.</b> <see cref="Rejections"/> is about a request the harness
    /// refused to engage with at all; this one was understood and answered. A client's handling of
    /// a <c>416</c> is a thing a test may legitimately want to drive, and it should not have to
    /// defuse <see cref="ThrowIfRejected"/> to do it.
    /// </para>
    /// </remarks>
    private static async Task RefuseRangeAsync(HttpContext context, int bodyLength)
    {
        var total = bodyLength.ToString(CultureInfo.InvariantCulture);
        var body = Encoding.UTF8.GetBytes(
            $"The requested range starts at or past the end of a {total}-byte body.");

        context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
        context.Response.ContentType = "text/plain; charset=utf-8";

        // The unsatisfied-range form: no first or last byte, only the length the client got wrong.
        context.Response.Headers.ContentRange = $"bytes */{total}";
        context.Response.ContentLength = body.Length;
        await context.Response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses <c>bytes=N-</c> — the open-ended form, and the only one a resumed download sends.
    /// </summary>
    /// <remarks>
    /// Any other form — a closed <c>bytes=N-M</c>, a suffix <c>bytes=-N</c>, a unit that is not
    /// <c>bytes</c>, a count that is not a number — comes back <see cref="RangeRequest.None"/> and
    /// the body goes out in full, which a test asserting a <c>206</c> catches immediately. That is
    /// the honest shape for a double: answering an unsupported form <c>416</c> would be an error
    /// path no test drives, and a guard nobody exercises is a guard that can be silently broken.
    /// An <em>out-of-bounds</em> range is a different thing — the client asked a question this
    /// response understands and got the answer wrong — so that one is
    /// <see cref="RangeRequest.Unsatisfiable"/>.
    /// </remarks>
    private static RangeRequest ParseOpenEndedRange(string? header, int bodyLength, out int firstByte)
    {
        const string Unit = "bytes=";

        firstByte = 0;
        if (header is null || !header.StartsWith(Unit, StringComparison.Ordinal) || !header.EndsWith('-'))
        {
            return RangeRequest.None;
        }

        // NumberStyles.None admits no sign and no separators, so a negative or padded count fails
        // here rather than needing a second check below.
        if (!int.TryParse(header[Unit.Length..^1], NumberStyles.None, CultureInfo.InvariantCulture, out firstByte))
        {
            firstByte = 0;
            return RangeRequest.None;
        }

        return firstByte < bodyLength ? RangeRequest.Satisfiable : RangeRequest.Unsatisfiable;
    }
}
