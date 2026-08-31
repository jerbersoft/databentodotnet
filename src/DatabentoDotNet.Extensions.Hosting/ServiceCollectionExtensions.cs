using DatabentoDotNet.Extensions.Hosting;
using DatabentoDotNet.Historical;
using DatabentoDotNet.Reference;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    private const string HttpClientName = "DatabentoDotNet.Historical";

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

        var path = SectionPathFor(services);
        services.AddOptions<HistoricalOptions>().BindConfiguration($"{path}:Historical").ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<HistoricalOptions>, HistoricalValidator>());

        // The whole reason this package touches HTTP at all. HttpClient's own SocketsHttpHandler
        // leaves PooledConnectionLifetime infinite, so a singleton in a host that stays up for
        // weeks keeps talking to whatever address hist.databento.com resolved to on its first
        // request. The factory rotates the handler on the schedule set here.
        services.AddHttpClient(HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(provider => new SocketsHttpHandler
                {
                    // Duration.ToTimeSpan(), never the banned type by name. HistoricalClient.cs
                    // already relies on the same rule for Timeout.InfiniteTimeSpan.
                    PooledConnectionLifetime = ResolveHistorical(provider).PooledConnectionLifetime.ToTimeSpan(),
                });

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
    public static DatabentoLiveBuilder AddDatabentoLive(this IServiceCollection services, string name, Action<LiveSessionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = AddDatabentoLive(services, name);
        builder.Services.Configure(name, configure);
        return builder;
    }

    /// <summary>Registers a live session named <paramref name="name"/>, bound from <c>{section}:Live:{name}</c>.</summary>
    public static DatabentoLiveBuilder AddDatabentoLive(this IServiceCollection services, string name)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var path = SectionPathFor(services);

        services.AddOptions<LiveSessionOptions>(name)
                .BindConfiguration($"{path}:Live:{name}")
                .ValidateOnStart();

        // One validator per session, each skipping the names it does not own. Enumerable rather
        // than TryAddSingleton: two sessions need two of these, and TryAdd would register one.
        services.AddSingleton<IValidateOptions<LiveSessionOptions>>(provider =>
            new LiveSessionValidator(name, provider.GetRequiredService<IOptions<DatabentoOptions>>()));

        // Task 9 adds the keyed LiveSessionRunner and the IHostedService here.
        return new DatabentoLiveBuilder(services, name);
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
