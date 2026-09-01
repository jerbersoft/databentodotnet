namespace DatabentoDotNet.Extensions.Hosting;

/// <summary>Configuration common to every Databento client in the container.</summary>
/// <remarks>
/// Bound from the section handed to <c>AddDatabento</c>, conventionally <c>Databento</c>.
/// </remarks>
public sealed class DatabentoOptions
{
    /// <summary>The conventional configuration section name: <c>Databento</c>.</summary>
    public const string DefaultSectionName = "Databento";

    /// <summary>
    /// The API key every client uses unless it names its own, or <see langword="null"/> to fall
    /// back to the <c>DATABENTO_API_KEY</c> environment variable.
    /// </summary>
    /// <remarks>
    /// A <see langword="string"/> here and an <see cref="DatabentoDotNet.ApiKey"/> everywhere
    /// else, and the asymmetry is the whole reason this type exists: <c>ApiKey</c> validates in
    /// its constructor and has no parameterless form, so a configuration binder cannot produce
    /// one. The crossing happens once, in <see cref="LiveSessionResolver"/>, where a bad key
    /// becomes a startup failure naming its configuration path rather than an exception from
    /// inside a binder.
    /// </remarks>
    public string? ApiKey { get; set; }
}
