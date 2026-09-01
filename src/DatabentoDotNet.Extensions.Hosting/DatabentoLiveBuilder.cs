using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DatabentoDotNet.Extensions.Hosting;

/// <summary>
/// Configures one named live session. Returned by <c>AddDatabentoLive</c>.
/// </summary>
public sealed class DatabentoLiveBuilder
{
    /// <summary>
    /// The name the no-argument <c>AddDatabentoLive</c> overload uses: <c>Default</c>.
    /// </summary>
    /// <remarks>
    /// A literal name rather than an empty one, so the configuration path is
    /// <c>Databento:Live:{name}</c> in every case. The alternative — the single session's keys
    /// directly under <c>Databento:Live</c> and named ones beneath it — makes
    /// <c>Databento:Live:Dataset</c> and <c>Databento:Live:equities</c> siblings of different
    /// kinds, which is ambiguous to read and worse to report an error against.
    /// </remarks>
    public const string DefaultSessionName = "Default";

    internal DatabentoLiveBuilder(IServiceCollection services, string name)
    {
        Services = services;
        Name = name;
    }

    /// <summary>The session's name, which is also its configuration key and its service key.</summary>
    public string Name { get; }

    /// <summary>The collection this session was registered into.</summary>
    public IServiceCollection Services { get; }

    /// <summary>Registers the handler this session's records are dispatched to.</summary>
    /// <typeparam name="THandler">The handler type. Constructed once, as a singleton.</typeparam>
    /// <remarks>
    /// <b>A singleton, and that is the dispatch contract rather than a default.</b> A scope per
    /// record would allocate, in the one package whose reason to exist is that it does not. A
    /// handler needing scoped services opens a scope inside
    /// <see cref="ILiveRecordHandler.OnFlushAsync"/>, which is where I/O belongs anyway.
    /// </remarks>
    public DatabentoLiveBuilder AddRecordHandler<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>()
        where THandler : class, ILiveRecordHandler
    {
        Services.AddKeyedSingleton<ILiveRecordHandler, THandler>(Name);
        return this;
    }

    /// <summary>Registers the handler this session's records are dispatched to, built by a factory.</summary>
    public DatabentoLiveBuilder AddRecordHandler(Func<IServiceProvider, ILiveRecordHandler> implementationFactory)
    {
        ArgumentNullException.ThrowIfNull(implementationFactory);
        Services.AddKeyedSingleton<ILiveRecordHandler>(Name, (provider, _) => implementationFactory(provider));
        return this;
    }

    /// <summary>Registers a <see cref="LiveSessionHealthCheck"/> reporting this session's state.</summary>
    /// <param name="name">
    /// The registration's name, which is what a health-check endpoint reports. Defaults to
    /// <c>databento-live-{session}</c>, so two sessions in one host produce two distinct entries
    /// without either caller naming one.
    /// </param>
    /// <param name="failureStatus">
    /// What a stopped or faulted session reports. Defaults to
    /// <see cref="HealthStatus.Unhealthy"/>; pass <see cref="HealthStatus.Degraded"/> for a
    /// session whose loss should not take the process out of rotation.
    /// </param>
    /// <param name="tags">Tags the endpoint can filter on, or <see langword="null"/> for none.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// <para>
    /// <b>Opt-in, and that is the whole point of it living here.</b> Nothing in
    /// <c>AddDatabentoLive</c> registers a check, so a consumer who never calls this gets none —
    /// no registration, no probe, no cost. A check installed by default would be one every
    /// consumer's <c>/health</c> endpoint reports on whether or not they asked.
    /// </para>
    /// <para>
    /// <b>Registered straight into <see cref="HealthCheckServiceOptions"/> rather than through
    /// <c>IHealthChecksBuilder</c>.</b> Going through the builder would make the consumer's own
    /// <c>AddHealthChecks()</c> a prerequisite — call this first and it would throw, or worse,
    /// silently create a second registry. Configuring the options directly means the two compose
    /// in either order, which is the same property <c>TryAddSingleton</c> buys for the historical
    /// and reference clients.
    /// </para>
    /// <para>
    /// The registration resolves the keyed <see cref="LiveSessionRunner"/> lazily, when the check
    /// first runs, so calling this does not build a session at registration time.
    /// </para>
    /// </remarks>
    public DatabentoLiveBuilder AddHealthCheck(
        string? name = null,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
    {
        var registrationName = name ?? $"databento-live-{Name}";

        Services.Configure<HealthCheckServiceOptions>(options =>
            options.Registrations.Add(new HealthCheckRegistration(
                registrationName,
                provider => new LiveSessionHealthCheck(
                    provider.GetRequiredKeyedService<LiveSessionRunner>(Name)),
                failureStatus,
                tags)));

        return this;
    }
}
