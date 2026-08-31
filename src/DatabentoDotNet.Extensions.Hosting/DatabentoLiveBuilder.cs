using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

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
}
