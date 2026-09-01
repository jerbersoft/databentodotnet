using DatabentoDotNet.Extensions.Hosting;
using DatabentoDotNet.Historical;
using DatabentoDotNet.Reference;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers Databento's historical, reference and live clients on an
/// <see cref="IServiceCollection"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Namespace <see cref="Microsoft.Extensions.DependencyInjection"/>, not
/// <see cref="DatabentoDotNet.Extensions.Hosting"/>.</b> This is the one deliberate exception to
/// <c>DatabentoDotNet.*</c> everywhere: the near-universal convention for a DI extensions class is
/// that its <c>Add*</c> methods appear on <see cref="IServiceCollection"/> with no extra
/// <see langword="using"/>, and the exception stops at this file.
/// </para>
/// <para>
/// <b>Sessions are declared in code and never conjured from configuration keys.</b> There is no
/// scan of <c>Databento:Live</c>'s children anywhere in this file, and there must not be one: a
/// session that exists because somebody added a JSON key, with no handler registered anywhere,
/// fails at startup with a cause that reads like a bug in this package rather than a missing
/// <c>AddDatabentoLive</c> call in the consumer's own <c>Program.cs</c>.
/// </para>
/// </remarks>
public static class DatabentoServiceCollectionExtensions
{
    /// <summary>
    /// The name of the <see cref="System.Net.Http.HttpClient"/> registration the historical and
    /// reference clients share: <c>DatabentoDotNet.Historical</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Public because the commonest thing a consumer does to an <c>IHttpClientFactory</c>
    /// registration needs it.</b> Layering a proxy, a corporate <c>HttpMessageHandler</c>, or a
    /// Polly resilience policy onto this package's transport is
    /// <c>services.AddHttpClient(DatabentoServiceCollectionExtensions.HttpClientName)</c> followed
    /// by the ordinary builder call — and the standard way of spelling that has no form that does
    /// not name the client. Without the constant a consumer would have to guess the string, and a
    /// wrong guess is silent: it configures a second, unused client rather than failing.
    /// </para>
    /// <para>
    /// The name is therefore load-bearing in the same way <c>LiveSessionMetrics.MeterName</c> is.
    /// Changing it detaches every consumer handler already attached to it, with nothing saying so.
    /// </para>
    /// </remarks>
    public const string HttpClientName = "DatabentoDotNet.Historical";

    /// <summary>Registers <see cref="DatabentoOptions"/>, bound from the conventional <c>Databento</c> section.</summary>
    public static IServiceCollection AddDatabento(this IServiceCollection services) =>
        AddDatabento(services, DatabentoOptions.DefaultSectionName);

    /// <summary>Registers <see cref="DatabentoOptions"/>, bound from <paramref name="section"/>.</summary>
    public static IServiceCollection AddDatabento(this IServiceCollection services, IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(section);

        // The path, not the section. See DatabentoSectionPath.
        return AddDatabento(services, section.Path);
    }

    /// <summary>Registers <see cref="DatabentoOptions"/>, bound from the configuration section at <paramref name="sectionPath"/>.</summary>
    /// <remarks>
    /// <b>Call this before the other <c>Add*</c> methods, and you will be told if you do not.</b>
    /// The first registration that needs a root fixes it for the whole collection — this one when
    /// it runs first, and <see cref="DatabentoOptions.DefaultSectionName"/> when something else
    /// does — so naming a second, different root afterwards throws rather than leaving the earlier
    /// registrations bound to the earlier root. Naming the root already in force is a no-op.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// This collection is already bound to a different configuration section.
    /// </exception>
    public static IServiceCollection AddDatabento(this IServiceCollection services, string sectionPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPath);

        // The root is fixed once per container, and naming a second one is an error rather than a
        // last-writer-wins. A container has exactly one Databento section; two different ones is a
        // contradiction, and the shape it took before #101 was that the *earlier* registrations
        // stayed bound to the earlier root while everything after moved to the later one. That
        // container starts, resolves, and reads half its settings from a key the consumer never
        // wrote.
        //
        // A repeat naming the *same* path is a no-op, which is what every other Add* in this file
        // already promises.
        //
        // OrdinalIgnoreCase, and not by analogy with the session names elsewhere in this file,
        // which are Ordinal because they are service keys. This is a configuration path, and
        // IConfiguration resolves keys case-insensitively — "databento" and "Databento" are the
        // same section, so rejecting the pair would be a false alarm about a difference the
        // configuration system does not have.
        var pinned = PinnedSectionPath(services);

        if (pinned is null)
        {
            services.AddSingleton(new DatabentoSectionPath(sectionPath));
            services.AddOptions<DatabentoOptions>().BindConfiguration(sectionPath);
        }
        else if (!string.Equals(pinned, sectionPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"AddDatabento(\"{sectionPath}\") cannot run: this IServiceCollection is already "
                    + $"bound to the configuration section \"{pinned}\". A container has one Databento "
                    + "root, and the registrations made before this call have already bound to that "
                    + "one — so honouring both would leave some options reading from "
                    + $"\"{pinned}\" and the rest from \"{sectionPath}\". Call AddDatabento before "
                    + "AddDatabentoHistorical, AddDatabentoReference and AddDatabentoLive, which pin "
                    + "the default root when they run first.");
        }

        // One meter for the process, shared by every session — the tag on each measurement is what
        // separates them, not a meter each. TryAddSingleton so calling AddDatabento twice yields
        // one, and the untyped registration so the container picks the constructor: the
        // IMeterFactory one when a host has called AddMetrics, the parameterless one otherwise.
        // A container that registers neither still resolves this; CreateRunner asks with
        // GetService, so a consumer who never wanted metrics gets a null and no publishing.
        services.TryAddSingleton<LiveSessionMetrics>();

        return services;
    }

    /// <summary>Registers <see cref="HistoricalClient"/>, bound from <c>{section}:Historical</c>.</summary>
    /// <remarks>
    /// <b>Composes with <see cref="AddDatabentoReference"/> in either order</b> — see that
    /// method's remarks. Everything below is a <c>TryAdd*</c> call or additive, so calling this
    /// twice — directly, and again through <see cref="AddDatabentoReference"/> — yields exactly
    /// one <see cref="HistoricalClient"/>.
    /// </remarks>
    public static IServiceCollection AddDatabentoHistorical(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Asked before anything is registered, because the answer is what the TryAddEnumerable
        // below is about to change. ConfigurePrimaryHttpMessageHandler has no TryAdd form — it
        // appends to HttpClientFactoryOptions.HttpMessageHandlerBuilderActions, and the last action
        // wins — so the documented, tested both-orders path (AddDatabentoHistorical then
        // AddDatabentoReference, which calls it again) built two SocketsHttpHandlers on every
        // rotation and discarded one. Harmless, and on the happy path, which is the wrong place for
        // a reader to find something that looks like a leak.
        //
        // The factory's own type, not ImplementationType. #96 made the validator below a factory
        // registration so it could be handed the section path, and a factory descriptor leaves
        // ImplementationType null — so the `== typeof(HistoricalValidator)` this used to make
        // answered "no" on every call and re-registered the handler, which is the exact bug the
        // guard exists to prevent. ImplementationFactory is *declared* Func<IServiceProvider,
        // object>, but a delegate variance conversion allocates no wrapper, so the runtime type is
        // still the Func<IServiceProvider, HistoricalValidator> written below and the pattern
        // matches. ServiceDescriptor.GetImplementationType() reads the same generic argument, and
        // is internal.
        var transportRegistered = services.Any(descriptor =>
            descriptor.ServiceType == typeof(IValidateOptions<HistoricalOptions>)
            && !descriptor.IsKeyedService
            && descriptor.ImplementationFactory is Func<IServiceProvider, HistoricalValidator>);

        var path = ResolveAndPinSectionPath(services);
        services.AddOptions<HistoricalOptions>()
                .BindConfiguration(HistoricalResolver.PathFor(path))
                .ValidateOnStart();

        // The path is captured here, in the same statement group that just handed it to
        // BindConfiguration, and never read back from the container at resolution time. That is
        // what makes "the path a failure message names is the path the options were bound from"
        // true by construction: one value produces both. See #96 — messages used to be rooted at
        // the literal "Databento" and pointed a host that called AddDatabento("MyApp:Feeds") at a
        // key absent from its own file.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<HistoricalOptions>, HistoricalValidator>(
            provider => new HistoricalValidator(path, provider.GetRequiredService<IOptions<DatabentoOptions>>())));

        // The whole reason this package touches HTTP at all. HttpClient's own SocketsHttpHandler
        // leaves PooledConnectionLifetime infinite, so a singleton in a host that stays up for
        // weeks keeps talking to whatever address hist.databento.com resolved to on its first
        // request. The factory rotates the handler on the schedule set here.
        if (!transportRegistered)
        {
            services.AddHttpClient(HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(provider => new SocketsHttpHandler
                    {
                        // Duration.ToTimeSpan(), never the banned type by name. HistoricalClient.cs
                        // already relies on the same rule for Timeout.InfiniteTimeSpan.
                        PooledConnectionLifetime = ResolveHistorical(provider, path).PooledConnectionLifetime.ToTimeSpan(),
                    });
        }

        services.TryAddSingleton(provider =>
        {
            var resolved = ResolveHistorical(provider, path);
            return new HistoricalClient
            {
                ApiKey = resolved.ApiKey,
                BaseUrl = resolved.BaseUrl,
                UserAgentExtension = resolved.UserAgentExtension,
                LoggerFactory = provider.GetService<ILoggerFactory>(),
                Handler = provider.GetRequiredService<IHttpMessageHandlerFactory>()
                                  .CreateHandler(HttpClientName),
                // The factory pools handlers across clients and rotates them on its own schedule;
                // disposing one out from under it would break every other client sharing it.
                DisposesHandler = false,
            };
        });

        return services;
    }

    /// <summary>Registers <see cref="HistoricalClient"/>, then applies <paramref name="configure"/> after binding.</summary>
    /// <remarks>
    /// Registered after <see cref="AddDatabentoHistorical(IServiceCollection)"/>'s own
    /// <c>BindConfiguration</c>, and applied in that order — the same rule
    /// <see cref="AddDatabentoLive(IServiceCollection, string, Action{LiveSessionOptions})"/>
    /// follows, so a lambda here overrides a bound value rather than being overridden by it.
    /// </remarks>
    public static IServiceCollection AddDatabentoHistorical(this IServiceCollection services, Action<HistoricalOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        AddDatabentoHistorical(services);
        services.Configure(configure);
        return services;
    }

    /// <summary>Registers <see cref="ReferenceClient"/>, sharing <see cref="HistoricalClient"/>'s transport.</summary>
    /// <remarks>
    /// <b>The spec's §1 promise, in four lines.</b> Registers the transport itself if
    /// <see cref="AddDatabentoHistorical(IServiceCollection)"/> has not already done so, and reuses it if it has —
    /// <c>TryAddSingleton</c> is what makes both orders equivalent. One <see cref="HistoricalClient"/>,
    /// one <see cref="System.Net.Http.HttpClient"/>, one connection pool to
    /// <c>hist.databento.com</c>.
    /// <para>
    /// <see cref="ReferenceClient(HistoricalClient)"/> does not dispose the transport it was
    /// handed, and the container disposes the <see cref="HistoricalClient"/> singleton directly,
    /// so nothing is disposed twice.
    /// </para>
    /// <para>
    /// <b>There is deliberately no lambda overload, and #99 is where that was decided rather than
    /// left unfinished.</b> Every other <c>Add*</c> in this family has one because each owns an
    /// options type — <see cref="HistoricalOptions"/>, <see cref="LiveSessionOptions"/> per session
    /// name. This one owns none: the client it registers is
    /// <see cref="ReferenceClient(HistoricalClient)"/> over the transport
    /// <see cref="AddDatabentoHistorical(IServiceCollection)"/> configures, so every knob that
    /// shapes it is already bound from <c>{section}:Historical</c>. Configure it through
    /// <see cref="AddDatabentoHistorical(IServiceCollection, Action{HistoricalOptions})"/>.
    /// </para>
    /// <para>
    /// The two shapes an overload could take were both rejected. An
    /// <c>AddDatabentoReference(Action&lt;HistoricalOptions&gt;)</c> would be a second name for a
    /// method that already exists, over the same options instance — so a consumer calling both
    /// would write what reads as two independent configurations and get one, last-writer-wins on
    /// any property they both set. A <c>ReferenceOptions</c> of its own would be an empty class,
    /// advertising a configuration surface with nothing in it. If reference data ever gains a
    /// setting that is genuinely its own, that setting gets an issue and brings the overload with
    /// it.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddDatabentoReference(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        AddDatabentoHistorical(services);
        services.TryAddSingleton(provider =>
            new ReferenceClient(provider.GetRequiredService<HistoricalClient>()));

        return services;
    }

    /// <summary>Registers a live session under <see cref="DatabentoLiveBuilder.DefaultSessionName"/>.</summary>
    public static DatabentoLiveBuilder AddDatabentoLive(this IServiceCollection services) =>
        AddDatabentoLive(services, DatabentoLiveBuilder.DefaultSessionName);

    /// <summary>
    /// Registers the session named <see cref="DatabentoLiveBuilder.DefaultSessionName"/>, then
    /// applies <paramref name="configure"/> after binding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exactly
    /// <see cref="AddDatabentoLive(IServiceCollection, string, Action{LiveSessionOptions})"/> with
    /// the default name, and it exists because that name is the one in this family a caller should
    /// never have to spell. A host with a single session configured entirely in <c>Program.cs</c>
    /// otherwise had to write <c>DatabentoLiveBuilder.DefaultSessionName</c> — naming the thing
    /// whose whole purpose is not needing a name — and the binding still landed at
    /// <c>Databento:Live:Default</c> either way. Added by #99.
    /// </para>
    /// <para>
    /// <b>Not ambiguous with <see cref="AddDatabentoLive(IServiceCollection, string)"/>.</b> A
    /// lambda is not convertible to <see langword="string"/> and a string literal is not
    /// convertible to <see cref="Action{T}"/>, so overload resolution picks one on the argument's
    /// own type. Only a bare <see langword="null"/> is ambiguous, and it is rejected at compile
    /// time rather than silently taking a branch.
    /// </para>
    /// </remarks>
    public static DatabentoLiveBuilder AddDatabentoLive(this IServiceCollection services, Action<LiveSessionOptions> configure) =>
        AddDatabentoLive(services, DatabentoLiveBuilder.DefaultSessionName, configure);

    /// <summary>Registers a live session named <paramref name="name"/>, then applies <paramref name="configure"/> after binding.</summary>
    /// <remarks>
    /// The registration itself is idempotent — see
    /// <see cref="AddDatabentoLive(IServiceCollection, string)"/> — but <paramref name="configure"/>
    /// is not swallowed with it: a caller who passes a lambda is asking for it to run, and options
    /// configuration is additive by design. Two calls with two lambdas apply both, in order.
    /// </remarks>
    public static DatabentoLiveBuilder AddDatabentoLive(this IServiceCollection services, string name, Action<LiveSessionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = AddDatabentoLive(services, name);
        builder.Services.Configure(name, configure);
        return builder;
    }

    /// <summary>Registers a live session named <paramref name="name"/>, bound from <c>{section}:Live:{name}</c>.</summary>
    /// <remarks>
    /// <para>
    /// <b>Idempotent per session name.</b> Calling this twice for one name — directly, or because
    /// your code and a library you depend on each register the same session — yields one runner and
    /// one <see cref="IHostedService"/>, not two. See the comment on the guard inside.
    /// </para>
    /// <para>
    /// <b>Idempotent is not the same as tolerant.</b> Registering your own keyed
    /// <see cref="LiveSessionRunner"/> under <paramref name="name"/> and then calling this throws,
    /// because the alternative is a container that resolves a runner nothing binds options for and
    /// nothing starts. Registering one <i>after</i> this call is an ordinary override and is left
    /// alone — that is how a test double gets in, and it leaves nothing half-configured.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A keyed <see cref="LiveSessionRunner"/> is already registered under <paramref name="name"/>
    /// and this package did not register it.
    /// </exception>
    public static DatabentoLiveBuilder AddDatabentoLive(this IServiceCollection services, string name)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var path = ResolveAndPinSectionPath(services);

        // Registering the same session name twice registers it once, which is what every other
        // Add* in this file already promises through TryAdd*. Getting it wrong is expensive rather
        // than untidy: two IHostedService entries around one keyed runner mean the second
        // StartAsync throws "This session is Running; StartSessionAsync runs once per
        // LiveSessionRunner" — *after* the first has opened a billable session, with a message
        // that reads like a bug in this package.
        //
        // One question, asked before the registrations that answer it, and it gates all of them.
        // TryAddKeyedSingleton alone would not do: the hosted service cannot use TryAddEnumerable,
        // whose contract is that a descriptor's implementation type is distinguishable from its
        // service type — a factory descriptor for IHostedService is exactly what it refuses — and
        // AddHostedService is worse, since it deduplicates on the implementation type, which is
        // LiveSessionService for every session.
        //
        // #101 changed *what* is read. This used to read the keyed LiveSessionRunner descriptor,
        // which answers "is a runner registered under this name?" — one question short of the one
        // the guard needs, which is "did we register it?". See LiveSessionRegistration.
        var alreadyRegistered = services.Any(descriptor =>
            descriptor.ServiceType == typeof(LiveSessionRegistration)
            && descriptor.ImplementationInstance is LiveSessionRegistration registration
            && string.Equals(registration.Name, name, StringComparison.Ordinal));

        // Somebody else's runner under our name. Skipping would hand back a builder over a
        // container that resolves a runner and never starts it — no bound options, no validator,
        // no hosted service, and nothing anywhere saying which of those went missing or why.
        // Throwing at the registration call is the cheap end of that mistake; the expensive end is
        // a session that simply never produces a record.
        //
        // Only the *pre*-registration case, deliberately. A consumer who registers their own keyed
        // runner after this call has overridden ours on purpose, which is how a test double gets
        // in, and their container is fully configured rather than half.
        if (!alreadyRegistered && services.Any(descriptor =>
                descriptor.ServiceType == typeof(LiveSessionRunner)
                && descriptor.IsKeyedService
                && string.Equals(descriptor.ServiceKey as string, name, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"AddDatabentoLive(\"{name}\") cannot run: a keyed LiveSessionRunner is already "
                    + $"registered under \"{name}\", and DatabentoDotNet.Extensions.Hosting did not "
                    + "register it. Continuing would bind no LiveSessionOptions for this session, "
                    + "register no validator for it and add no hosted service to start it, leaving a "
                    + "container that resolves a runner and never reads a record. Remove that "
                    + "registration and let this call make it, or give this session a different name.");
        }

        if (!alreadyRegistered)
        {
            services.AddSingleton(new LiveSessionRegistration(name));

            services.AddOptions<LiveSessionOptions>(name)
                    .BindConfiguration(LiveSessionResolver.PathFor(path, name))
                    .ValidateOnStart();

            // One validator per session, each skipping the names it does not own. Enumerable rather
            // than TryAddSingleton: two sessions need two of these, and TryAdd would register one.
            services.AddSingleton<IValidateOptions<LiveSessionOptions>>(provider =>
                new LiveSessionValidator(path, name, provider.GetRequiredService<IOptions<DatabentoOptions>>()));

            // Keyed by session name, so two sessions in one host are two runners with two handlers
            // and two independent reconnect states. Also what LiveSessionHealthCheck resolves.
            services.AddKeyedSingleton(name, (provider, key) => CreateRunner(provider, path, (string)key!));

            // AddSingleton rather than AddHostedService: the latter is TryAddEnumerable on
            // IHostedService by implementation type, so a second session would silently not be
            // registered — both would be LiveSessionService.
            services.AddSingleton<IHostedService>(provider =>
                new LiveSessionService(provider.GetRequiredKeyedService<LiveSessionRunner>(name)));
        }

        // Outside the guard, and TryAddSingleton is what makes that safe: registered here as well
        // as in AddDatabento, both orders and both entry points yield the one instance. It is not
        // belt and braces. AddDatabentoLive works standalone — ResolveAndPinSectionPath falls back to the
        // default section when no marker was added — so a consumer who calls only this would
        // otherwise get a runner with a null metrics instance and nothing anywhere saying so.
        // Silence is the wrong failure mode for observability, which is the one feature whose
        // absence looks exactly like health.
        services.TryAddSingleton<LiveSessionMetrics>();

        return new DatabentoLiveBuilder(services, name);
    }

    /// <summary>
    /// Resolves one session's <see cref="LiveSessionRunner"/> from the container, through the same
    /// <see cref="LiveSessionResolver.Resolve"/> path <see cref="LiveSessionValidator"/> uses — a
    /// configuration that validates is a configuration that resolves, because no second path
    /// exists to disagree.
    /// </summary>
    private static LiveSessionRunner CreateRunner(IServiceProvider provider, string sectionPath, string name)
    {
        var options = provider.GetRequiredService<IOptionsMonitor<LiveSessionOptions>>().Get(name);
        var root = provider.GetRequiredService<IOptions<DatabentoOptions>>().Value;

        var result = LiveSessionResolver.Resolve(
            sectionPath,
            name,
            options,
            root,
            Environment.GetEnvironmentVariable(LiveSessionResolver.ApiKeyEnvironmentVariable));

        if (!result.Succeeded)
        {
            // Unreachable when ValidateOnStart ran — and thrown anyway. This runner is resolvable
            // from the container directly, and "the validator will have caught it" is an
            // assumption about a caller rather than a property of this code.
            throw new OptionsValidationException(name, typeof(LiveSessionOptions), result.Failures);
        }

        return new LiveSessionRunner(
            result.Session,
            provider.GetRequiredKeyedService<ILiveRecordHandler>(name),
            new ReconnectSupervisor(result.Session.Reconnect),
            provider.GetService<ILogger<LiveSessionRunner>>(),
            provider.GetService<LiveSessionMetrics>())
        {
            // Null means the session configured none, so the runner's own default stands. Spelled
            // with ?? rather than by leaving the initializer off, because an object initializer
            // cannot be conditional and "the hosted path chooses this" is worth being able to read.
            CloseTimeout = result.Session.CloseTimeout ?? LiveSessionRunner.DefaultCloseTimeout,
        };
    }

    /// <summary>
    /// Resolves the historical client's configuration from the container, throwing the same
    /// failure list <see cref="HistoricalValidator"/> would have reported if <c>ValidateOnStart</c>
    /// had run. A real host never reaches the throw: a bad configuration fails
    /// <c>host.StartAsync()</c> before any hosted service — or this factory — ever runs. A
    /// container built directly, with no host and no startup validation pass, reaches this
    /// instead, and gets the same message a host would have shown.
    /// </summary>
    private static ResolvedHistorical ResolveHistorical(IServiceProvider provider, string sectionPath)
    {
        var options = provider.GetRequiredService<IOptions<HistoricalOptions>>().Value;
        var root = provider.GetRequiredService<IOptions<DatabentoOptions>>().Value;
        var environmentApiKey = Environment.GetEnvironmentVariable(LiveSessionResolver.ApiKeyEnvironmentVariable);

        var result = HistoricalResolver.Resolve(sectionPath, options, root, environmentApiKey);
        return result.Succeeded
            ? result.Historical
            : throw new OptionsValidationException(
                Microsoft.Extensions.Options.Options.DefaultName, typeof(HistoricalOptions), result.Failures);
    }

    /// <summary>
    /// Carries the configuration section path from <c>AddDatabento</c> to the <c>Add*</c> calls
    /// that follow it.
    /// </summary>
    /// <remarks>
    /// A marker in the service collection rather than a captured <c>IConfiguration</c>, because
    /// <c>AddDatabentoHistorical()</c> takes no arguments and has no service provider to resolve
    /// one from — it runs at registration time. A path is all that has to travel:
    /// <c>BindConfiguration</c> resolves the <c>IConfiguration</c> itself, from the container,
    /// when the options are actually built.
    /// </remarks>
    private sealed class DatabentoSectionPath(string value)
    {
        public string Value { get; } = value;
    }

    /// <summary>
    /// The configured root, pinning the default one if nothing has pinned a root yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It writes, and that is the point (#101).</b> This used to be a pure read that fell back
    /// to <see cref="DatabentoOptions.DefaultSectionName"/>, and a test pinned the consequence:
    /// <c>AddDatabentoHistorical()</c> then <c>AddDatabento("MyApp:Feeds")</c> left the historical
    /// options bound to <c>Databento:Historical</c> while everything registered afterwards read
    /// <c>MyApp:Feeds</c>. That test's comment said the package "cannot tell the two apart", and
    /// it was reading the wrong two. The package genuinely cannot distinguish "the consumer wants
    /// the default root" from "the consumer has not called AddDatabento yet" — but it does not
    /// need to, because both end in the same place: the fallback is now a decision the collection
    /// records, so the *next* call naming a different root has something to contradict.
    /// </para>
    /// <para>
    /// The fallback itself is unchanged and still load-bearing: a standalone
    /// <c>AddDatabentoHistorical()</c> with no <c>AddDatabento</c> anywhere has to work, and it
    /// does.
    /// </para>
    /// </remarks>
    private static string ResolveAndPinSectionPath(IServiceCollection services)
    {
        if (PinnedSectionPath(services) is { } pinned)
        {
            return pinned;
        }

        services.AddSingleton(new DatabentoSectionPath(DatabentoOptions.DefaultSectionName));
        services.AddOptions<DatabentoOptions>().BindConfiguration(DatabentoOptions.DefaultSectionName);
        return DatabentoOptions.DefaultSectionName;
    }

    /// <summary>The root this collection is already bound to, or <see langword="null"/> if none is.</summary>
    private static string? PinnedSectionPath(IServiceCollection services) =>
        services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(DatabentoSectionPath))
                ?.ImplementationInstance is DatabentoSectionPath marker
            ? marker.Value
            : null;

    /// <summary>
    /// Evidence that <c>AddDatabentoLive</c> registered a session, as opposed to something else
    /// having registered a keyed <see cref="LiveSessionRunner"/> under the same name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A marker service, which this file argued against until #101.</b> The idempotence guard
    /// read the keyed <see cref="LiveSessionRunner"/> descriptor and the comment beside it said
    /// that answered the question "without inventing a marker service". It answered the question
    /// it was asked — <i>is a runner registered under this name?</i> — and that is one question
    /// short of the one that matters: <i>did we register it?</i> A consumer who registers their
    /// own keyed runner and then calls <c>AddDatabentoLive</c> under the same name is
    /// indistinguishable, to that guard, from a second identical call, so they silently got no
    /// options binding, no validator and no hosted service.
    /// </para>
    /// <para>
    /// Private and nested, so no consumer can name the type, so its presence cannot mean anything
    /// but this package. <see cref="DatabentoSectionPath"/> is the same pattern for the same
    /// reason, and predates the comment that ruled the pattern out.
    /// </para>
    /// </remarks>
    private sealed class LiveSessionRegistration(string name)
    {
        public string Name { get; } = name;
    }
}
