using Microsoft.Extensions.Options;

namespace DatabentoDotNet.Extensions.Hosting;

/// <summary>
/// Validates <see cref="HistoricalOptions"/> at startup by resolving it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type holds no rules</b>, for the same reason <see cref="LiveSessionValidator"/> does
/// not: it calls <see cref="HistoricalResolver.Resolve"/> and turns its failure list into a
/// <see cref="ValidateOptionsResult"/>. There is exactly one <see cref="HistoricalOptions"/> in a
/// container — unlike a live session, it is never registered under a name — so there is no name
/// to skip: every call is this validator's to answer.
/// </para>
/// <para>
/// <see langword="internal"/>, for the reason <see cref="LiveSessionValidator"/> is: the container
/// reaches it through <see cref="IValidateOptions{TOptions}"/> and nothing outside this assembly
/// names it.
/// </para>
/// </remarks>
internal sealed class HistoricalValidator : IValidateOptions<HistoricalOptions>
{
    private readonly string _sectionPath;
    private readonly IOptions<DatabentoOptions> _root;

    /// <summary>Creates the validator.</summary>
    /// <param name="sectionPath">
    /// The section <c>AddDatabento</c> was given. Handed in at registration for the reason
    /// <see cref="LiveSessionValidator"/>'s is: it must be the same value the
    /// <c>BindConfiguration</c> beside it captured, and only capturing it once guarantees that.
    /// </param>
    /// <param name="root">The root options, consulted for a key the historical section does not carry.</param>
    public HistoricalValidator(string sectionPath, IOptions<DatabentoOptions> root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPath);
        ArgumentNullException.ThrowIfNull(root);

        _sectionPath = sectionPath;
        _root = root;
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, HistoricalOptions options)
    {
        var result = HistoricalResolver.Resolve(
            _sectionPath,
            options,
            _root.Value,
            Environment.GetEnvironmentVariable(LiveSessionResolver.ApiKeyEnvironmentVariable));

        return result.Succeeded
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(result.Failures);
    }
}
