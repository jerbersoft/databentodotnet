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
    private readonly IOptions<DatabentoOptions> _root;

    /// <summary>Creates the validator.</summary>
    public HistoricalValidator(IOptions<DatabentoOptions> root)
    {
        ArgumentNullException.ThrowIfNull(root);

        _root = root;
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, HistoricalOptions options)
    {
        var result = HistoricalResolver.Resolve(
            options,
            _root.Value,
            Environment.GetEnvironmentVariable(LiveSessionResolver.ApiKeyEnvironmentVariable));

        return result.Succeeded
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(result.Failures);
    }
}
