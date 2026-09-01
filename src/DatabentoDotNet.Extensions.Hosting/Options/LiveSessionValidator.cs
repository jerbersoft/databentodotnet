using Microsoft.Extensions.Options;

namespace DatabentoDotNet.Extensions.Hosting;

/// <summary>
/// Validates one named <see cref="LiveSessionOptions"/> at startup by resolving it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type holds no rules.</b> It calls <see cref="LiveSessionResolver.Resolve"/> and turns
/// its failure list into a <see cref="ValidateOptionsResult"/>, which is what makes "a
/// configuration that validates is a configuration that resolves" true by construction rather
/// than by two lists being kept in step.
/// </para>
/// <para>
/// <b><see langword="internal"/>, unlike <see cref="LiveSessionResolver"/> and
/// <c>LiveSessionRunner</c>.</b> Those two are public because a test has to drive them and this
/// repository declares no <c>InternalsVisibleTo</c>. That argument does not reach here: nothing
/// outside this assembly constructs or calls a validator — the container reaches it only through
/// <see cref="IValidateOptions{TOptions}"/>, and the tests reach the rules it enforces through the
/// resolver it delegates to. A type on the public surface is a type promised under SemVer at 1.0,
/// so the default is off.
/// </para>
/// </remarks>
internal sealed class LiveSessionValidator : IValidateOptions<LiveSessionOptions>
{
    private readonly string _sectionPath;
    private readonly string _name;
    private readonly IOptions<DatabentoOptions> _root;

    /// <summary>Creates a validator for one session.</summary>
    /// <param name="sectionPath">
    /// The section <c>AddDatabento</c> was given. Handed in at registration rather than resolved
    /// from the container, so that it is the same value the session's <c>BindConfiguration</c>
    /// captured in the statement above this validator's own registration — a value read back at
    /// resolution time could have been superseded by a later <c>AddDatabento</c> call, leaving the
    /// message naming one path and the binding using another.
    /// </param>
    /// <param name="name">The session's registration name.</param>
    /// <param name="root">The root options, consulted for a key the session does not carry.</param>
    public LiveSessionValidator(string sectionPath, string name, IOptions<DatabentoOptions> root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(root);

        _sectionPath = sectionPath;
        _name = name;
        _root = root;
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, LiveSessionOptions options)
    {
        // Skip, not Success. Every session registers one of these, so each is asked about every
        // other session's options; answering Success for a name this validator knows nothing
        // about would report a bad configuration as a good one.
        if (!string.Equals(name, _name, StringComparison.Ordinal))
        {
            return ValidateOptionsResult.Skip;
        }

        var result = LiveSessionResolver.Resolve(
            _sectionPath,
            _name,
            options,
            _root.Value,
            Environment.GetEnvironmentVariable(LiveSessionResolver.ApiKeyEnvironmentVariable));

        return result.Succeeded
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(result.Failures);
    }
}
