using System.Diagnostics.CodeAnalysis;
using DatabentoDotNet.Historical;
using Microsoft.Extensions.Logging;

namespace DatabentoDotNet.Reference;

/// <summary>
/// A client for Databento's reference data API: security master, corporate actions, and
/// adjustment factors.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>reference::Client</c> (<c>reference.rs:34-92</c>) — its <c>key</c>,
/// <c>base_url</c>, <c>gateway</c> and <c>request</c>, which are the whole of that type once its
/// three subclient accessors are set aside.
/// </para>
/// <para>
/// <b>This client sends through <see cref="HistoricalClient"/>, and that is the port rather than a
/// shortcut around one.</b> Upstream's reference client is a separate type in the same crate that
/// reuses the historical transport through crate-internal visibility: its <c>request</c> composes
/// <c>v{API_VERSION}/{slug}</c> against a base URL derived from
/// <see cref="HistoricalGateway"/>, attaches the same HTTP Basic credential, and sends with the
/// same <c>Accept: application/json</c> default header and the same user agent. The reference API
/// is the historical transport with a different set of slugs. Separate .NET assemblies have no
/// equivalent of <c>pub(crate)</c> and this repo declares no <c>InternalsVisibleTo</c> anywhere,
/// so the reuse goes through <see cref="HistoricalClient"/>'s public transport — which is public
/// for reasons of its own, documented on that type.
/// </para>
/// <para>
/// <b>All three subclient properties, and all six of the reference API's endpoints.</b>
/// <see cref="AdjustmentFactors"/> arrived with its endpoint in
/// <see href="https://github.com/jerbersoft/databentodotnet/issues/53">#53</see>,
/// <see cref="SecurityMaster"/> with its two in
/// <see href="https://github.com/jerbersoft/databentodotnet/issues/54">#54</see>,
/// and <see cref="CorporateActions"/> with its two documentation endpoints in
/// <see href="https://github.com/jerbersoft/databentodotnet/issues/56">#56</see>
/// and its <c>get_range</c> in
/// <see href="https://github.com/jerbersoft/databentodotnet/issues/55">#55</see>.
/// A facade with no endpoints on it would be a public empty class, which is why none of the three
/// was declared in #48 — the same call <see cref="HistoricalClient"/> records for M3.
/// </para>
/// <para>
/// <b>Thread-safe for concurrent requests once configured</b>, for the same reason
/// <see cref="HistoricalClient"/> is: everything below the surface is one
/// <see cref="HttpClient"/>, and the properties are <see langword="init"/>-only and therefore
/// frozen before the first request.
/// </para>
/// <para>
/// <b>No builder.</b> Upstream's <c>ClientBuilder&lt;AK&gt;</c> is generic type-state whose only
/// purpose is to make "no API key" unrepresentable. C# 11 <see langword="required"/> init
/// properties do that natively, checked by the compiler at every construction site. See
/// PORTING.md §2.
/// </para>
/// <code>
/// await using var client = new ReferenceClient { ApiKey = new ApiKey(key) };
/// </code>
/// </remarks>
public sealed class ReferenceClient : IAsyncDisposable
{
    private readonly Lazy<HistoricalClient> _transport;
    private readonly Lazy<AdjustmentFactorsClient> _adjustmentFactors;
    private readonly Lazy<SecurityMasterClient> _securityMaster;
    private readonly Lazy<CorporateActionsClient> _corporateActions;
    private readonly bool _ownsTransport;

    private ApiKey _apiKey = null!;
    private HistoricalGateway _gateway = HistoricalGateway.Bo1;
    private Uri? _baseUrl;
    private string? _userAgentExtension;
    private ILoggerFactory? _loggerFactory;

    private volatile bool _disposed;

    /// <summary>Creates a client that owns its transport. Configure it through the init properties.</summary>
    /// <remarks>
    /// The transport is built on first use, not here. An <see langword="init"/> accessor runs
    /// <em>after</em> the constructor body, so <see cref="ApiKey"/> does not exist yet at the point
    /// where an eager constructor would want to build an <see cref="HttpClient"/> from it;
    /// deferring is what makes <see langword="required"/> init properties and a fully configured
    /// transport compatible at all. <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/>
    /// because this type is documented as safe for concurrent requests: two threads racing into
    /// the first request must get one transport, not two.
    /// </remarks>
    public ReferenceClient()
    {
        _ownsTransport = true;
        _transport = new Lazy<HistoricalClient>(CreateTransport, LazyThreadSafetyMode.ExecutionAndPublication);
        _adjustmentFactors = CreateAdjustmentFactorsHolder();
        _securityMaster = CreateSecurityMasterHolder();
        _corporateActions = CreateCorporateActionsHolder();
    }

    /// <summary>
    /// Creates a client that sends through an existing <see cref="HistoricalClient"/>, sharing its
    /// connection pool, and does not dispose it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>For a consumer holding both.</b> The reference and historical APIs share a host, a
    /// gateway, an API version and an auth scheme, so two independently configured clients mean
    /// two <see cref="HttpClient"/>s and two connection pools to the same origin for no reason.
    /// This constructor is how they become one.
    /// </para>
    /// <para>
    /// <b>Ownership does not transfer.</b> <see cref="DisposeAsync"/> leaves
    /// <paramref name="transport"/> open, because whoever created it is still using it. Disposing
    /// it while this client is alive is the caller's mistake to avoid, and it surfaces as
    /// <see cref="ObjectDisposedException"/> from the next request rather than as anything subtler.
    /// </para>
    /// <para>
    /// The configuration properties below report <paramref name="transport"/>'s own settings, so
    /// they describe what this client actually does either way. Assigning one of them alongside
    /// this constructor throws rather than silently having no effect — see
    /// <see cref="ApiKey"/>.
    /// </para>
    /// </remarks>
    /// <param name="transport">The client to send through. Not disposed by this one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="transport"/> is <see langword="null"/>.</exception>
    [SetsRequiredMembers]
    public ReferenceClient(HistoricalClient transport)
    {
        ArgumentNullException.ThrowIfNull(transport);

        // True for the length of this constructor body only, and that ordering is load-bearing.
        // The init accessors below refuse an assignment on a client that did not build its own
        // transport — which is what makes `new ReferenceClient(t) { ApiKey = ... }` throw rather
        // than silently have no effect — and this constructor has to make exactly those five
        // assignments to copy the transport's configuration onto them. So the guard closes on the
        // last line, after them. An object initializer runs *after* a constructor body, so the
        // case the guard exists for is still refused.
        _ownsTransport = true;

        ApiKey = transport.ApiKey;
        Gateway = transport.Gateway;
        BaseUrl = transport.BaseUrl;
        UserAgentExtension = transport.UserAgentExtension;
        LoggerFactory = transport.LoggerFactory;

        _transport = new Lazy<HistoricalClient>(transport);
        _adjustmentFactors = CreateAdjustmentFactorsHolder();
        _securityMaster = CreateSecurityMasterHolder();
        _corporateActions = CreateCorporateActionsHolder();
        _ownsTransport = false;
    }

    /// <summary>The API key to authenticate with. Validated when it is constructed.</summary>
    /// <remarks>
    /// <para>
    /// The type, never a <see langword="string"/>: <see cref="DatabentoDotNet.ApiKey.ToString"/> is
    /// redacted, so formatting the object that holds the key cannot leak it. The key reaches the
    /// wire in exactly one place in this library — the <c>Authorization</c> header
    /// <see cref="HistoricalClient"/> builds — and nowhere else, not as a query parameter and not
    /// as a form field.
    /// </para>
    /// <para>
    /// <b>Assigning this alongside <see cref="ReferenceClient(HistoricalClient)"/> throws.</b> That
    /// constructor's transport is already built and already carries a credential, so an assignment
    /// here could not reach the wire — and a property reporting a key that no request carries is
    /// exactly the kind of confidently wrong answer that is worse than an exception. The other four
    /// configuration properties refuse an assignment on that path for the same reason.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The client was constructed over an existing <see cref="HistoricalClient"/>.
    /// </exception>
    public required ApiKey ApiKey
    {
        get => _apiKey;
        init
        {
            ThrowIfTransportSupplied(nameof(ApiKey));
            _apiKey = value;
        }
    }

    /// <summary>The gateway to send requests to. Defaults to <see cref="HistoricalGateway.Bo1"/>.</summary>
    /// <remarks>
    /// Ignored when <see cref="BaseUrl"/> is set. <see cref="HistoricalGateway"/> is reused rather
    /// than re-declared: upstream's reference client derives its base URL from that same enum
    /// (<c>reference.rs:37</c>), and a second gateway type for one API would be a worse public
    /// surface than one shared with a sibling package.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The client was constructed over an existing <see cref="HistoricalClient"/>.
    /// </exception>
    public HistoricalGateway Gateway
    {
        get => _gateway;
        init
        {
            ThrowIfTransportSupplied(nameof(Gateway));
            _gateway = value;
        }
    }

    /// <summary>
    /// A base URL to send requests to instead of <see cref="Gateway"/>'s, or
    /// <see langword="null"/> to use the gateway.
    /// </summary>
    /// <remarks>
    /// The advanced knob, as upstream documents <c>base_url</c>: it exists for a test harness or a
    /// proxy, and it is how this library's own tests reach a mock gateway. A path on it is
    /// preserved — <see cref="HistoricalClient.BaseUrl"/> does that work and documents why it takes
    /// explicit effort.
    /// </remarks>
    /// <exception cref="ArgumentException">The URL is not absolute.</exception>
    /// <exception cref="InvalidOperationException">
    /// The client was constructed over an existing <see cref="HistoricalClient"/>.
    /// </exception>
    public Uri? BaseUrl
    {
        get => _baseUrl;
        init
        {
            ThrowIfTransportSupplied(nameof(BaseUrl));

            // Restated from HistoricalClient.BaseUrl rather than delegated to it, because the
            // transport is not built until the first request: without this the mistake surfaces
            // there, several stack frames from the property that caused it and long after the
            // caller has stopped looking. The transport re-checks it regardless, so this is a
            // second chance to fail early rather than the only check.
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
    /// Port of upstream's <c>user_agent_extension</c> (<c>reference.rs:135-138</c>), which composes
    /// it the same way: the library's user agent, a space, then this. An extension that is not a
    /// well-formed sequence of user-agent products and comments is rejected when the first request
    /// is sent rather than reaching Databento's logs malformed.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The client was constructed over an existing <see cref="HistoricalClient"/>.
    /// </exception>
    public string? UserAgentExtension
    {
        get => _userAgentExtension;
        init
        {
            ThrowIfTransportSupplied(nameof(UserAgentExtension));
            _userAgentExtension = value;
        }
    }

    /// <summary>
    /// Where to send this client's log messages, or <see langword="null"/> for none.
    /// </summary>
    /// <remarks>
    /// How the API's <c>X-Warning</c> header surfaces, and the only route it has. See
    /// <see cref="HistoricalClient.LoggerFactory"/>, which this is handed to and which documents
    /// why a warnings property on every response was rejected.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The client was constructed over an existing <see cref="HistoricalClient"/>.
    /// </exception>
    public ILoggerFactory? LoggerFactory
    {
        get => _loggerFactory;
        init
        {
            ThrowIfTransportSupplied(nameof(LoggerFactory));
            _loggerFactory = value;
        }
    }

    /// <summary>
    /// The <c>adjustment_factors.*</c> endpoints — the multipliers that make a price series
    /// comparable across splits, dividends and other capital events.
    /// </summary>
    /// <remarks>
    /// The first of this client's three endpoint-group facades (#53); #54–#56 add the rest. Built
    /// once and cached, because this client is documented thread-safe for concurrent requests and a
    /// bare null-coalescing assignment would let two threads each build one — the same arrangement
    /// <see cref="HistoricalClient.Metadata"/> and its three siblings use.
    /// <b>Its one endpoint costs money</b>, and reference data is a separate Databento product from
    /// historical market data: see <see cref="AdjustmentFactorsClient.GetRangeAsync"/>.
    /// </remarks>
    public AdjustmentFactorsClient AdjustmentFactors => _adjustmentFactors.Value;

    /// <summary>
    /// The <c>security_master.*</c> endpoints — what a listing is, where it trades, and every
    /// identifier it is known by.
    /// </summary>
    /// <remarks>
    /// The second of this client's three endpoint-group facades (#54). Built
    /// once and cached, for the reason <see cref="AdjustmentFactors"/> gives. <b>Both its endpoints
    /// cost money</b>, and one property common to both can spend an ISIN entitlement rather than
    /// only money — see <see cref="SecurityMasterGetRangeParams.AllocateIsins"/>.
    /// </remarks>
    public SecurityMasterClient SecurityMaster => _securityMaster.Value;

    /// <summary>
    /// The <c>corporate_actions.*</c> endpoints: what happened to a security, and the documentation
    /// the server keeps about its own events and enumerations.
    /// </summary>
    /// <remarks>
    /// The largest of this client's three endpoint-group facades — three endpoints where the others
    /// have two and one, and a hundred-and-four-field row where the others have fifty and nineteen.
    /// #56 shipped <see cref="CorporateActionsClient.ListEventsAsync"/> and
    /// <see cref="CorporateActionsClient.ListEnumsAsync"/>; #55 added
    /// <see cref="CorporateActionsClient.GetRangeAsync"/>, which is what makes this a data endpoint
    /// group rather than a documentation one. Built once and cached, for the reason
    /// <see cref="AdjustmentFactors"/> gives. <b>Alone among the three, two of its endpoints are not
    /// known to cost anything</b> — they return documentation rather than market data, and #57
    /// prices them rather than assuming.
    /// </remarks>
    public CorporateActionsClient CorporateActions => _corporateActions.Value;

    /// <summary>
    /// The HTTP transport this client sends through — built from the properties above on first
    /// read, or the one supplied to <see cref="ReferenceClient(HistoricalClient)"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Public, and that is a decision rather than an omission</b> — the same one
    /// <see cref="HistoricalClient"/> records for its own transport. The reference API has six
    /// endpoints (ROADMAP.md §6 lists them) and a caller who needs a seventh the week it ships
    /// should not have to wait for a release: <see cref="HistoricalClient.SendAsync"/> and
    /// <see cref="HistoricalClient.SendZstdJsonLinesAsync"/> reach any slug the API serves.
    /// </para>
    /// <para>
    /// It is also what makes the shared-transport constructor legible: a client built over an
    /// existing <see cref="HistoricalClient"/> returns that same instance here.
    /// </para>
    /// <para>
    /// This is a transport, not a facade. That the historical endpoints are reachable from it is a
    /// consequence of them living on the same type upstream puts them on, not an invitation to
    /// call them from here.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    public HistoricalClient Transport
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _transport.Value;
        }
    }

    /// <summary>Releases the transport, if this client created it.</summary>
    /// <remarks>
    /// <para>
    /// Idempotent, and safe on a client that never sent a request: the transport is built on first
    /// use, so there is nothing to release until one has been. Using the client after this throws
    /// <see cref="ObjectDisposedException"/> rather than quietly building a second one.
    /// </para>
    /// <para>
    /// <b>A transport supplied to <see cref="ReferenceClient(HistoricalClient)"/> is left open.</b>
    /// This client did not create it and does not know who else holds it.
    /// </para>
    /// </remarks>
    /// <returns>The transport's own disposal, or a completed task when there is nothing to release.</returns>
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        GC.SuppressFinalize(this);

        return _ownsTransport && _transport.IsValueCreated
            ? _transport.Value.DisposeAsync()
            : ValueTask.CompletedTask;
    }

    /// <summary>
    /// The cached holder for <see cref="AdjustmentFactors"/>, shared by both constructors.
    /// </summary>
    /// <remarks>
    /// A method rather than an inline expression in each constructor, so the two cannot drift apart
    /// on the thread-safety mode — which is the half of this that is easy to get wrong and
    /// impossible to see.
    /// </remarks>
    /// <returns>The holder.</returns>
    private Lazy<AdjustmentFactorsClient> CreateAdjustmentFactorsHolder() =>
        new(() => new AdjustmentFactorsClient(this), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The cached holder for <see cref="SecurityMaster"/>, shared by both constructors.
    /// </summary>
    /// <remarks>A method rather than an inline expression, for the reason its sibling gives.</remarks>
    /// <returns>The holder.</returns>
    private Lazy<SecurityMasterClient> CreateSecurityMasterHolder() =>
        new(() => new SecurityMasterClient(this), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The cached holder for <see cref="CorporateActions"/>, shared by both constructors.
    /// </summary>
    /// <remarks>A method rather than an inline expression, for the reason its siblings give.</remarks>
    /// <returns>The holder.</returns>
    private Lazy<CorporateActionsClient> CreateCorporateActionsHolder() =>
        new(() => new CorporateActionsClient(this), LazyThreadSafetyMode.ExecutionAndPublication);

    private HistoricalClient CreateTransport() => new()
    {
        ApiKey = _apiKey,
        Gateway = _gateway,
        BaseUrl = _baseUrl,
        UserAgentExtension = _userAgentExtension,
        LoggerFactory = _loggerFactory,

        // HistoricalClient.UpgradePolicy is deliberately not carried across. It is the DBN
        // decoder's input, and no reference endpoint returns DBN: every one of them answers with
        // JSON or with zstd-framed JSON lines. Upstream's reference client has no equivalent field
        // for the same reason.
    };

    private void ThrowIfTransportSupplied(string property)
    {
        if (!_ownsTransport)
        {
            throw new InvalidOperationException(
                $"{property} cannot be set on a client constructed over an existing " +
                $"{nameof(HistoricalClient)}: that transport is already built and already " +
                $"configured. Configure the {nameof(HistoricalClient)} instead, or construct this " +
                $"client with its own {nameof(ApiKey)}.");
        }
    }
}
