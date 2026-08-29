using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
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
/// project references <c>DatabentoDotNet.Historical</c>, for <c>DateRange</c> and
/// <c>DateTimeRange</c>, but not <c>DatabentoDotNet.Dbn</c> — deliberately; see the csproj. The
/// facts it encodes are the published ones:
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
    /// A literal rather than a call into the library's user-agent type, because this project does
    /// not reference <c>DatabentoDotNet.Dbn</c>, where <c>UserAgent</c> lives — see the class
    /// remarks. The real client's user agent is
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
    private readonly TaskCompletionSource _clientHungUp = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The key <see cref="NoteClientHungUp"/> stamps on an <see cref="HttpContext"/> it has already
    /// counted, so the three places that notice a hang-up cannot count one request twice.
    /// </summary>
    private static readonly object HangUpMarker = new();

    private int _clientHungUpCount;
    private int _handlersRunning;

    /// <summary>
    /// The waiters for <see cref="Idle"/>, or <see langword="null"/> when no handler is running.
    /// Created on the first handler to enter and completed by the last one to leave.
    /// </summary>
    private TaskCompletionSource? _idle;

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

    /// <summary>
    /// Completes the first time a client goes away before the response it asked for has finished —
    /// the server's side of a connection closed early.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A property of the connection, not of any one test.</b> It is the only thing a server can
    /// observe about a client that stopped reading: the socket ends, and the next write down it
    /// fails. Everything a test wants to conclude from that — that a caller's <c>break</c> really
    /// released the response rather than leaking it to a finalizer, that a cancelled read really
    /// tore the transfer down — is a conclusion from this one fact, and it is a fact only the
    /// server can supply. A test that inspected the client's own objects instead would be asking
    /// the code under test whether it had done its job.
    /// </para>
    /// <para>
    /// Three things complete it, because a client can go away at more than one moment and the
    /// server notices differently each time. A hang-up while the handler is <em>writing</em> shows
    /// up as the body write failing — the broken pipe — and <see cref="WriteBodyAsync"/> catches
    /// it. A hang-up while the handler is <em>waiting</em> (a response holding the connection open
    /// on <c>MockHistoricalResponse.Dropped(…, dropWhen)</c>, say) never reaches a write at all,
    /// and shows up as <c>IConnectionLifetimeFeature.ConnectionClosed</c>, which
    /// <see cref="HandleAsync"/> registers on for exactly as long as the handler runs. And because
    /// that registration is racing the handler's own exit, <see cref="HandleAsync"/> checks the
    /// request's abort state once more in a <see langword="finally"/>. The three are the same
    /// event seen from three places, they cost one increment between them, and they all land here.
    /// </para>
    /// <para>
    /// <b>It is not instant, and the delay is the client's rather than this harness's.</b>
    /// Disposing an <see cref="HttpResponseMessage"/> whose chunked body is unfinished makes
    /// <c>SocketsHttpHandler</c> try to drain the remainder so the connection can go back in the
    /// pool; the socket closes only when that drain gives up, which is two seconds on the default
    /// <c>ResponseDrainTimeout</c>. A test waiting on this should budget seconds, not
    /// milliseconds.
    /// </para>
    /// <para>
    /// It never completes on its own, so a test <b>must</b> bound its wait —
    /// <c>await gateway.ClientHungUp.WaitAsync(…)</c> — or a client that leaks the connection
    /// hangs the run instead of failing it.
    /// </para>
    /// <para>
    /// <b>It is a per-gateway latch, and on a gateway with more than one route that is a trap.</b>
    /// It says a client hung up at some point, not that one is hanging up now — so a test that
    /// registers a <c>Dropped</c> route <em>and</em> the route it actually cares about will find
    /// this already completed by the first one and conclude nothing. <see cref="ClientHungUpCount"/>
    /// is what such a test wants: it counts hang-ups rather than latching on the first, so a test
    /// can read it before and after the thing it is testing and assert on the difference. Use the
    /// count when the gateway serves more than one route; the latch is only enough when a test can
    /// see that nothing else could have completed it.
    /// </para>
    /// </remarks>
    public Task ClientHungUp => _clientHungUp.Task;

    /// <summary>
    /// How many requests have ended with the client going away first — <see cref="ClientHungUp"/>
    /// counted rather than latched.
    /// </summary>
    /// <remarks>
    /// Monotonic, and at most one per request however many of the three detection points notice it.
    /// A test that has to be sure the hang-up it is asserting on is <em>its</em> hang-up reads this
    /// before and after and compares, which is the only form that survives a second route being
    /// added to the gateway later.
    /// </remarks>
    public int ClientHungUpCount
    {
        get
        {
            lock (_gate)
            {
                return _clientHungUpCount;
            }
        }
    }

    /// <summary>
    /// Completes when the handlers that had started have all finished — the gateway
    /// <em>became</em> idle, which is a weaker claim than it <em>being</em> idle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What a test needs before it reads anything a handler writes on its way out.</b> A client
    /// stops waiting at the moment its <em>response</em> is settled, and the handler behind it goes
    /// on running for a while after that: it unwinds a <c>Dropped</c> response's wait, disposes the
    /// registration <see cref="HandleAsync"/> made, and only then returns. Anything recorded in
    /// that window — <see cref="ClientHungUpCount"/>, most obviously — is not there yet when the
    /// client's own call returns, so a test that reads it straight away is racing the handler and
    /// will usually win. Winning is the problem: the assertion then passes because it ran early
    /// rather than because the thing it names is true.
    /// </para>
    /// <para>
    /// So: <c>await gateway.Idle</c> first, and read afterwards. It is a fresh answer each time it
    /// is asked rather than a latch — a completed <see cref="Task"/> while nothing is in flight,
    /// and a pending one from the moment a handler enters — so it can be awaited once per request
    /// in a test that makes several.
    /// </para>
    /// <para>
    /// <b>Bound the wait.</b> Every wait inside a handler is bounded by <see cref="Timeout"/> or by
    /// Kestrel, so this cannot hang for ever, but ten seconds of silence is a worse failure than an
    /// assertion, and a test that means to observe a <em>finished</em> handler should say how long
    /// it is prepared to wait for one.
    /// </para>
    /// <para>
    /// <b>Two things it does not say, both of which matter to a test that fires requests off rather
    /// than awaiting them.</b> It counts handlers that have <em>entered</em>
    /// <see cref="HandleAsync"/>, not requests Kestrel has accepted — a connection accepted and not
    /// yet dispatched is invisible to it, so awaiting this straight after starting a request can
    /// complete before that request's handler has begun. And it is completed outside
    /// <see cref="_gate"/>, so another handler may enter between the completion and whatever the
    /// waiter goes on to read. Neither reaches the use it was written for: a test that awaits the
    /// client's own call first has that call's handler in flight by construction, and a test making
    /// one request at a time has nothing to race with. Waiting once for several requests in flight
    /// is what this is <em>not</em>.
    /// </para>
    /// </remarks>
    public Task Idle
    {
        get
        {
            lock (_gate)
            {
                return _idle?.Task ?? Task.CompletedTask;
            }
        }
    }

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
        // EnterHandler and the try are adjacent on purpose, with nothing between them that could
        // throw. Anything that escaped in that gap would skip ExitHandler, leave _handlersRunning
        // stuck above zero, and mean Idle never completes again for this gateway — after which
        // every test that waits on it burns its whole budget and fails pointing at a handler rather
        // than at the leak. The realistic thrower is the registration below, which is why it is
        // below: ConnectionClosed.Register raises ObjectDisposedException on a connection that died
        // between being accepted and being dispatched. Nothing in the current suite provokes it.
        EnterHandler();

        var onClose = default(CancellationTokenRegistration);

        try
        {
            // See ClientHungUp. This catches the hang-ups that never reach a write — a handler
            // parked in WaitForDropSignalAsync has nothing to fail on — and it is registered before
            // anything else so it covers a refusal and an unrouted request too. Its lifetime is the
            // handler's, which is what makes "the connection closed" mean "the client left before
            // the response finished" rather than "a connection closed at some point".
            //
            // ConnectionClosed rather than context.RequestAborted, and that was measured: putting a
            // callback on RequestAborted here fails this five runs out of five where
            // ConnectionClosed passes eight out of eight. RequestAborted's *IsCancellationRequested*
            // does flip, and a CancellationTokenSource linked to it does cancel —
            // WaitForDropSignalAsync stops on it — but the callback registered here never runs, and
            // the mechanism is an ordering one rather than luck. WaitForDropSignalAsync links its
            // source to RequestAborted *after* this line registered on the same token,
            // CancellationTokenSource runs its callbacks LIFO, so the linked source is cancelled
            // first, the handler unwinds, and this registration is disposed in the finally below
            // before the token reaches its own callback.
            //
            // The 2 s the whole thing takes is SocketsHttpHandler's response-drain timeout:
            // disposing a response whose body is unfinished makes it try to drain the rest so the
            // connection can be pooled, and only when that gives up does the socket close.
            var connection = context.Features.Get<IConnectionLifetimeFeature>();
            if (connection is not null)
            {
                onClose = connection.ConnectionClosed.Register(() => NoteClientHungUp(context));
            }

            var request = await RecordAsync(context.Request).ConfigureAwait(false);

            if (Refuse(context.Request) is { } refusal)
            {
                // Refused requests are never dispatched to their route. If they were, deleting a
                // check above would leave every test that asserts a refusal passing on the route's
                // own body.
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
        finally
        {
            // Belt and braces, and not redundant with the registration above — the two are racing.
            // ConnectionClosed and RequestAborted are both cancelled from ThreadPool-queued work,
            // the handler's exit is driven by the second of them, and leaving this method disposes
            // the registration. The 2 ms by which ConnectionClosed won every time it was traced is
            // a queue artefact, not an ordering guarantee: under pool starvation the two invert,
            // the callback is dropped exactly the way RequestAborted's is above, and a test waiting
            // on ClientHungUp fails after a ten-second budget with nothing to say why. Reading the
            // abort state here instead of registering for it cannot be raced, because by this point
            // whatever was going to cancel already has or never will. NoteClientHungUp counts each
            // request once however many of the three paths reach it.
            if (context.RequestAborted.IsCancellationRequested)
            {
                NoteClientHungUp(context);
            }

            // Disposed here rather than by a `using`, so that the order against ExitHandler is
            // stated rather than inherited. Disposing a CancellationTokenRegistration waits for a
            // callback already running, so once this returns nothing can still be on its way to
            // NoteClientHungUp — which is what makes "Idle completed" mean the counts have settled
            // rather than mostly settled. A `using` would run after ExitHandler and leave a test
            // that waited for idle reading a count another thread was still touching.
            onClose.Dispose();
            ExitHandler();
        }
    }

    /// <summary>
    /// Marks a handler as running, so <see cref="Idle"/> is pending until it leaves.
    /// </summary>
    private void EnterHandler()
    {
        lock (_gate)
        {
            _handlersRunning++;
            _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>
    /// Marks a handler as finished, completing <see cref="Idle"/> if it was the last one.
    /// </summary>
    /// <remarks>
    /// The source is cleared under the lock and completed outside it. Completing it while holding
    /// <see cref="_gate"/> would run a waiter's continuation — which may read
    /// <see cref="ClientHungUpCount"/>, which takes the same lock — on this thread inside the lock,
    /// which is exactly the shape that turns a harness into an intermittent deadlock.
    /// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> makes that unlikely rather
    /// than impossible, and unlikely is not the property to want here.
    /// </remarks>
    private void ExitHandler()
    {
        TaskCompletionSource? idle = null;

        lock (_gate)
        {
            if (--_handlersRunning == 0)
            {
                idle = _idle;
                _idle = null;
            }
        }

        idle?.TrySetResult();
    }

    /// <summary>
    /// Records that the client behind <paramref name="context"/> went away before its response
    /// finished — completing <see cref="ClientHungUp"/> and advancing
    /// <see cref="ClientHungUpCount"/>, at most once for the request however many detection points
    /// reach here.
    /// </summary>
    /// <remarks>
    /// The marker lives on <see cref="HttpContext.Items"/> rather than in a field because the thing
    /// being counted is one request, and there may be several in flight. <c>Items</c> is not itself
    /// thread-safe, but every read and write of the marker happens under <see cref="_gate"/>, and
    /// nothing else in this harness or in Kestrel writes that key. The registration in
    /// <see cref="HandleAsync"/> is disposed before the handler returns, and disposing a
    /// <see cref="CancellationTokenRegistration"/> waits for a callback already running, so the
    /// context is still the request's own whenever this runs.
    /// </remarks>
    /// <param name="context">The request whose client went away.</param>
    private void NoteClientHungUp(HttpContext context)
    {
        lock (_gate)
        {
            if (!context.Items.TryAdd(HangUpMarker, HangUpMarker))
            {
                return;
            }

            _clientHungUpCount++;
        }

        _clientHungUp.TrySetResult();
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
        // empty string. The HasFormContentType guard below is load-bearing rather than defensive
        // noise: removing it makes FormFeature.ReadForm() throw "Incorrect Content-Type" for any
        // request that carries no form — which is most of this suite. The damage surfaces partly as
        // 500s and partly as downstream assertion failures, e.g.
        // Warnings_ArriveAsAJsonArrayInTheXWarningHeader fails on a missing header rather than on
        // the 500 itself.
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

        if (response.DropsEveryRequest && range != RangeRequest.Unsatisfiable)
        {
            await DripAndDropAsync(context, response, firstByte).ConfigureAwait(false);
            return;
        }

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
            await DropAsync(context, response, response.StatusCode, body[..dropAfter], contentRange: null)
                .ConfigureAwait(false);
            return;
        }

        if (!response.Chunked)
        {
            context.Response.ContentLength = body.Length;
        }

        await WriteBodyAsync(context, body).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes <see cref="MockHistoricalResponse.DropAfterBytes"/> bytes from
    /// <paramref name="firstByte"/> and then drops the connection — the flaky-link answer, given to
    /// every request rather than to the first one.
    /// </summary>
    /// <remarks>
    /// <see cref="DropAsync"/> does the dropping; what this adds is where in the body to start and
    /// what to say about it. The status is <c>206</c> when the request asked to start somewhere
    /// other than the beginning, because a client that checks whether its <c>Range</c> was honoured
    /// has to see the honest answer.
    /// </remarks>
    /// <param name="context">The request being answered.</param>
    /// <param name="response">The registered response.</param>
    /// <param name="firstByte">The offset the request's <c>Range</c> asked to start at.</param>
    /// <returns>A task that completes when the client has hung up.</returns>
    private async Task DripAndDropAsync(
        HttpContext context,
        MockHistoricalResponse response,
        int firstByte)
    {
        var body = response.Body;
        var step = response.DropAfterBytes ?? body.Length;
        var slice = body.Slice(firstByte, Math.Min(step, body.Length - firstByte));

        string? contentRange = null;
        if (firstByte > 0)
        {
            var last = (body.Length - 1).ToString(CultureInfo.InvariantCulture);
            var total = body.Length.ToString(CultureInfo.InvariantCulture);
            contentRange = $"bytes {firstByte.ToString(CultureInfo.InvariantCulture)}-{last}/{total}";
        }

        var statusCode = firstByte > 0 ? StatusCodes.Status206PartialContent : response.StatusCode;
        await DropAsync(context, response, statusCode, slice, contentRange).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends <paramref name="prefix"/> as an unfinished chunked response and then half-closes the
    /// connection: what a transfer that died part-way looks like on the wire, without losing the
    /// bytes that had already gone out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A half-close, not a reset, and the difference is the whole of
    /// <see href="https://github.com/jerbersoft/databentodotnet/issues/47">#47</see>.</b> This used
    /// to be <c>context.Abort()</c>, which resets the connection — and a reset discards whatever the
    /// receiver has not yet read, the partial body included. Locally the client always won that
    /// race; on all three CI runners it lost, and three tests that assert on a delivered prefix saw
    /// nothing arrive at all, the response headers included. A <c>FIN</c> discards nothing: TCP
    /// orders it behind the bytes already queued, so the client reads the prefix and only then
    /// reaches the end. The prefix is delivered by construction rather than by timing.
    /// </para>
    /// <para>
    /// <b>The bytes go to the socket rather than to <c>Response.Body</c>, and that is not a
    /// shortcut.</b> Kestrel's body writer hands bytes to a pipe whose flush to the socket is
    /// asynchronous, so <c>FlushAsync</c> returning does not mean the kernel has them — and a
    /// <c>Shutdown</c> issued after that flush can still overtake them. Measured: it delivers zero
    /// bytes when it does. <c>Socket.SendAsync</c> completing does mean the kernel has them, which
    /// is the ordering guarantee this path needs, and it costs writing the status line and the
    /// headers here instead.
    /// </para>
    /// <para>
    /// <b>Chunked with no terminating chunk</b>, which is the framing the drop responses have always
    /// documented: the response is unfinished on its face, so a client cannot read the close as a
    /// body that simply ended. An empty <paramref name="prefix"/> sends the headers and no chunk at
    /// all rather than nothing — a response whose headers never arrived is one <c>HttpClient</c> may
    /// transparently retry on a pooled connection, which would make a test counting requests race
    /// something else instead.
    /// </para>
    /// <para>
    /// <b>Then it waits for the client to hang up</b>, bounded by <see cref="Timeout"/>, before
    /// returning to Kestrel — whose own teardown of an unanswered request would otherwise be free to
    /// fire a reset behind the <c>FIN</c> and re-open the window this method exists to close. The
    /// wait measures 0–2 ms against a real <c>HttpClient</c>, which disposes the connection as soon
    /// as the truncated body fails.
    /// </para>
    /// </remarks>
    /// <param name="context">The request being answered.</param>
    /// <param name="response">The registered response, for its content type and extra headers.</param>
    /// <param name="statusCode">The status the headers carry — <c>206</c> for a resumed transfer.</param>
    /// <param name="prefix">The bytes to deliver before the connection ends. May be empty.</param>
    /// <param name="contentRange">The <c>Content-Range</c> header value, or <see langword="null"/>.</param>
    /// <returns>A task that completes when the client has hung up, or after <see cref="Timeout"/>.</returns>
    private async Task DropAsync(
        HttpContext context,
        MockHistoricalResponse response,
        int statusCode,
        ReadOnlyMemory<byte> prefix,
        string? contentRange)
    {
        var socket = context.Features.Get<IConnectionSocketFeature>()?.Socket
            ?? throw new InvalidOperationException(
                "A dropped response needs the connection's socket, which Kestrel exposes through "
                + "IConnectionSocketFeature on its sockets transport. Without it the only way to end "
                + "the response early is a reset, and a reset is what #47 removed.");

        // Hand-written framing is version-specific, so say so rather than write HTTP/1.1 bytes down
        // an HTTP/2 connection and leave someone reading a frame parser's error. The protocol is
        // named in the message because Kestrel produces it from a closed set — a client cannot put
        // free text there — so this is the unregistered-route case rather than the credential one.
        if (!HttpProtocol.IsHttp11(context.Request.Protocol))
        {
            throw new InvalidOperationException(
                $"A dropped response writes HTTP/1.1 framing to the socket by hand, and this request "
                + $"arrived as {context.Request.Protocol}. Kestrel speaks HTTP/1.1 over the plain "
                + "loopback this harness listens on; another version needs different framing, not a "
                + "different message.");
        }

        var head = new StringBuilder()
            .Append("HTTP/1.1 ")
            .Append(statusCode.ToString(CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(ReasonPhrases.GetReasonPhrase(statusCode))
            .Append("\r\nContent-Type: ")
            .Append(response.ContentType)
            .Append("\r\nTransfer-Encoding: chunked\r\n");

        if (contentRange is not null)
        {
            head.Append("Content-Range: ").Append(contentRange).Append("\r\n");
        }

        foreach (var header in response.ExtraHeaders)
        {
            head.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
        }

        head.Append("\r\n");

        using var wire = new MemoryStream();
        wire.Write(Encoding.ASCII.GetBytes(head.ToString()));

        // One chunk per ChunkSize bytes, for the reason WriteBodyAsync writes in the same steps.
        for (var written = 0; written < prefix.Length; written += ChunkSize)
        {
            var chunk = prefix.Slice(written, Math.Min(ChunkSize, prefix.Length - written));
            wire.Write(Encoding.ASCII.GetBytes(
                chunk.Length.ToString("x", CultureInfo.InvariantCulture) + "\r\n"));
            wire.Write(chunk.Span);
            wire.Write("\r\n"u8);
        }

        await SendAllAsync(socket, wire.ToArray(), context.RequestAborted).ConfigureAwait(false);
        await WaitForDropSignalAsync(context, response).ConfigureAwait(false);

        socket.Shutdown(SocketShutdown.Send);
        await WaitForHangUpAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends every byte of <paramref name="bytes"/>, however many calls that takes.
    /// </summary>
    /// <remarks>
    /// A short send is legal and does happen on a body larger than the socket's send buffer. The
    /// loop is what makes "the kernel has all of it" true when this returns, which is the property
    /// <see cref="DropAsync"/> orders its <c>FIN</c> against.
    /// </remarks>
    /// <param name="socket">The connection's socket.</param>
    /// <param name="bytes">What to send.</param>
    /// <param name="cancellationToken">Cancels the send when the client goes away first.</param>
    /// <returns>A task that completes once every byte has been handed to the kernel.</returns>
    private static async Task SendAllAsync(
        Socket socket,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        while (!bytes.IsEmpty)
        {
            var sent = await socket.SendAsync(bytes, SocketFlags.None, cancellationToken)
                .ConfigureAwait(false);
            bytes = bytes[sent..];
        }
    }

    /// <summary>
    /// Waits for the client to close its end, or for <see cref="Timeout"/>, whichever comes first.
    /// </summary>
    /// <remarks>
    /// Kestrel raises <see cref="HttpContext.RequestAborted"/> when the connection goes while a
    /// handler is still running, which after a half-close is exactly the client finishing with the
    /// truncated response. Timing out is not a failure: everything the response promised is already
    /// on the wire, and the only thing still owed is politeness towards a client that has not
    /// noticed yet.
    /// </remarks>
    /// <param name="context">The request being answered.</param>
    /// <returns>A task that completes on the hang-up or the timeout.</returns>
    private async Task WaitForHangUpAsync(HttpContext context)
    {
        if (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        var hungUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(checked((int)Timeout.TotalMilliseconds));
        using var onHangUp = context.RequestAborted.Register(Complete, hungUp);
        using var onTimeout = timeout.Token.Register(Complete, hungUp);

        await hungUp.Task.ConfigureAwait(false);

        static void Complete(object? state) => ((TaskCompletionSource)state!).TrySetResult();
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

    /// <summary>
    /// Writes <paramref name="body"/> in <see cref="ChunkSize"/> steps, stopping early and
    /// completing <see cref="ClientHungUp"/> if the client goes away part-way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A write down a socket whose peer has closed fails — <see cref="IOException"/> for the
    /// broken pipe, or <see cref="OperationCanceledException"/> when Kestrel got there first and
    /// cancelled <see cref="HttpContext.RequestAborted"/>. Neither is a fault of this harness or
    /// of the test running it: the client asked for a response and then stopped wanting it, which
    /// is a thing clients are entitled to do and a thing several tests here arrange on purpose. So
    /// it is recorded and the write stops, rather than thrown into Kestrel's logging (which this
    /// harness silences) where nothing could see it. Swallowing it changes nothing a client can
    /// observe: the write fails <em>because</em> Kestrel has already aborted the connection, so
    /// the reset the client sees is the same either way.
    /// </para>
    /// <para>
    /// <b>What this does not prove is that the client is what went away.</b>
    /// <see cref="HttpContext.RequestAborted"/> is also cancelled by a <c>MinResponseDataRate</c>
    /// violation and by the host shutting down — so a gateway being disposed while a body is still
    /// going out can set <see cref="ClientHungUp"/> with no client having hung up at all. No test
    /// reads it at that point and none should: a test that wants the signal to mean what its name
    /// says reads <see cref="ClientHungUpCount"/> across the operation it is testing, while the
    /// gateway is still running.
    /// </para>
    /// </remarks>
    /// <param name="context">The request being answered.</param>
    /// <param name="body">The bytes to write.</param>
    /// <returns>A task that completes when the body is out, or when the client has gone.</returns>
    private async Task WriteBodyAsync(HttpContext context, ReadOnlyMemory<byte> body)
    {
        for (var written = 0; written < body.Length; written += ChunkSize)
        {
            var chunk = body.Slice(written, Math.Min(ChunkSize, body.Length - written));
            try
            {
                await context.Response.Body.WriteAsync(chunk, context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                NoteClientHungUp(context);
                return;
            }
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
