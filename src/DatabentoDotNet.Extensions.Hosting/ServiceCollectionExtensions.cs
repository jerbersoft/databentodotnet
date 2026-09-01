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
    public static IServiceCollection AddDatabento(this IServiceCollection services, string sectionPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPath);

        services.AddSingleton(new DatabentoSectionPath(sectionPath));
        services.AddOptions<DatabentoOptions>().BindConfiguration(sectionPath);

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
        var transportRegistered = services.Any(descriptor =>
            descriptor.ServiceType == typeof(IValidateOptions<HistoricalOptions>)
            && !descriptor.IsKeyedService
            && descriptor.ImplementationType == typeof(HistoricalValidator));

        var path = SectionPathFor(services);
        services.AddOptions<HistoricalOptions>().BindConfiguration($"{path}:Historical").ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<HistoricalOptions>, HistoricalValidator>());

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
                        PooledConnectionLifetime = ResolveHistorical(provider).PooledConnectionLifetime.ToTimeSpan(),
                    });
        }

        services.TryAddSingleton(provider =>
        {
            var resolved = ResolveHistorical(provider);
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
    /// <b>Idempotent per session name.</b> Calling this twice for one name — directly, or because
    /// your code and a library you depend on each register the same session — yields one runner and
    /// one <see cref="IHostedService"/>, not two. See the comment on the guard inside.
    /// </remarks>
    public static DatabentoLiveBuilder AddDatabentoLive(this IServiceCollection services, string name)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var path = SectionPathFor(services);

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
        // LiveSessionService for every session. Reading the keyed runner's own descriptor answers
        // for both without inventing a marker service.
        var alreadyRegistered = services.Any(descriptor =>
            descriptor.ServiceType == typeof(LiveSessionRunner)
            && descriptor.IsKeyedService
            && string.Equals(descriptor.ServiceKey as string, name, StringComparison.Ordinal));

        if (!alreadyRegistered)
        {
            services.AddOptions<LiveSessionOptions>(name)
                    .BindConfiguration($"{path}:Live:{name}")
                    .ValidateOnStart();

            // One validator per session, each skipping the names it does not own. Enumerable rather
            // than TryAddSingleton: two sessions need two of these, and TryAdd would register one.
            services.AddSingleton<IValidateOptions<LiveSessionOptions>>(provider =>
                new LiveSessionValidator(name, provider.GetRequiredService<IOptions<DatabentoOptions>>()));

            // Keyed by session name, so two sessions in one host are two runners with two handlers
            // and two independent reconnect states. Also what LiveSessionHealthCheck resolves.
            services.AddKeyedSingleton(name, (provider, key) => CreateRunner(provider, (string)key!));

            // AddSingleton rather than AddHostedService: the latter is TryAddEnumerable on
            // IHostedService by implementation type, so a second session would silently not be
            // registered — both would be LiveSessionService.
            services.AddSingleton<IHostedService>(provider =>
                new LiveSessionService(provider.GetRequiredKeyedService<LiveSessionRunner>(name)));
        }

        // Outside the guard, and TryAddSingleton is what makes that safe: registered here as well
        // as in AddDatabento, both orders and both entry points yield the one instance. It is not
        // belt and braces. AddDatabentoLive works standalone — SectionPathFor falls back to the
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
    private static LiveSessionRunner CreateRunner(IServiceProvider provider, string name)
    {
        var options = provider.GetRequiredService<IOptionsMonitor<LiveSessionOptions>>().Get(name);
        var root = provider.GetRequiredService<IOptions<DatabentoOptions>>().Value;

        var result = LiveSessionResolver.Resolve(
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
            provider.GetService<LiveSessionMetrics>());
    }

    /// <summary>
    /// Resolves the historical client's configuration from the container, throwing the same
    /// failure list <see cref="HistoricalValidator"/> would have reported if <c>ValidateOnStart</c>
    /// had run. A real host never reaches the throw: a bad configuration fails
    /// <c>host.StartAsync()</c> before any hosted service — or this factory — ever runs. A
    /// container built directly, with no host and no startup validation pass, reaches this
    /// instead, and gets the same message a host would have shown.
    /// </summary>
    private static ResolvedHistorical ResolveHistorical(IServiceProvider provider)
    {
        var options = provider.GetRequiredService<IOptions<HistoricalOptions>>().Value;
        var root = provider.GetRequiredService<IOptions<DatabentoOptions>>().Value;
        var environmentApiKey = Environment.GetEnvironmentVariable(LiveSessionResolver.ApiKeyEnvironmentVariable);

        var result = HistoricalResolver.Resolve(options, root, environmentApiKey);
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

    private static string SectionPathFor(IServiceCollection services) =>
        services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(DatabentoSectionPath))
                ?.ImplementationInstance is DatabentoSectionPath marker
            ? marker.Value
            : DatabentoOptions.DefaultSectionName;
}
